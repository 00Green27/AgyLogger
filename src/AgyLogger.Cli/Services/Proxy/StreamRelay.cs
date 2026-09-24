namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Represents the outcome of a transparent stream relay operation.
/// </summary>
public sealed record RelayResult
{
    /// <summary>
    /// Exact bytes captured in memory during the relay, up to maxCaptureBytes.
    /// Ready for assignment to CapturedExchange.RawResponseBody or RawRequestBody.
    /// </summary>
    public required byte[] CapturedBytes { get; init; }

    /// <summary>
    /// Total number of bytes relayed over the wire from source to destination.
    /// </summary>
    public long TotalBytesRelayed { get; init; }

    /// <summary>
    /// Total number of bytes stored in the in-memory capture buffer.
    /// </summary>
    public long CapturedBytesCount => CapturedBytes.LongLength;

    /// <summary>
    /// True if the stream payload exceeded maxCaptureBytes and capture was capped
    /// while pass-through continued.
    /// </summary>
    public bool IsTruncated { get; init; }

    /// <summary>
    /// True if the destination (client) aborted or closed the connection prematurely (e.g. Ctrl+C).
    /// </summary>
    public bool ClientAborted { get; init; }

    /// <summary>
    /// True if the upstream source stream completed normally to EOF or final chunk.
    /// </summary>
    public bool UpstreamCompleted { get; init; }

    /// <summary>
    /// Exception that caused relay termination, if aborted unexpectedly.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// True if relay completed successfully without client abort or unhandled error.
    /// </summary>
    public bool IsSuccess => UpstreamCompleted && !ClientAborted && Exception is null;
}

/// <summary>
/// Provides transparent, non-blocking stream relaying between upstream endpoints and clients.
/// Guarantees zero-latency streaming pass-through via immediate flushing, simultaneous in-memory
/// tee buffering for exchange logging, maximum buffer capping against memory exhaustion,
/// and graceful handling of client aborts (e.g. Ctrl+C).
/// </summary>
public static class StreamRelay
{
    public const int DefaultBufferSize = 8192; // 8 KB
    public const long DefaultMaxCaptureBytes = 64 * 1024 * 1024; // 64 MB
    public const int MaxChunkLineLength = 64 * 1024; // 64 KB line length limit for chunk headers and trailers

    /// <summary>
    /// Dispatches stream relay based on whether the HTTP payload uses chunked transfer encoding.
    /// </summary>
    public static Task<RelayResult> RelayAsync(
        Stream source,
        Stream destination,
        bool isChunked,
        long? contentLength = null,
        long maxCaptureBytes = DefaultMaxCaptureBytes,
        int bufferSize = DefaultBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        return isChunked
            ? RelayChunkedAsync(source, destination, dechunkForCapture: true, maxCaptureBytes, bufferSize, cancellationToken)
            : RelayDirectAsync(source, destination, contentLength, maxCaptureBytes, bufferSize, cancellationToken);
    }

    /// <summary>
    /// Relays a direct stream (content-length bounded or EOF bounded) from source to destination.
    /// Flushes destination immediately after every read to eliminate buffering delay.
    /// Captures bytes up to <paramref name="maxCaptureBytes"/> in memory.
    /// </summary>
    public static async Task<RelayResult> RelayDirectAsync(
        Stream source,
        Stream destination,
        long? contentLength = null,
        long maxCaptureBytes = DefaultMaxCaptureBytes,
        int bufferSize = DefaultBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (bufferSize <= 0) bufferSize = DefaultBufferSize;
        if (maxCaptureBytes <= 0) maxCaptureBytes = DefaultMaxCaptureBytes;

        long totalBytesRelayed = 0;
        bool isTruncated = false;
        bool clientAborted = false;
        bool upstreamCompleted = false;
        Exception? error = null;

        var initialCap = contentLength.HasValue && contentLength.Value > 0
            ? (int)Math.Min(contentLength.Value, Math.Min(maxCaptureBytes, 1024 * 1024))
            : Math.Min((int)maxCaptureBytes, 64 * 1024);

        using var captureStream = new MemoryStream(initialCap);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int toRead = bufferSize;
                if (contentLength.HasValue)
                {
                    long remaining = contentLength.Value - totalBytesRelayed;
                    if (remaining <= 0)
                    {
                        upstreamCompleted = true;
                        break;
                    }
                    if (remaining < toRead)
                    {
                        toRead = (int)remaining;
                    }
                }

                int bytesRead = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    upstreamCompleted = true;
                    break;
                }

                totalBytesRelayed += bytesRead;

                // 1. Capture into memory buffer up to maxCaptureBytes (tee before write to preserve bytes on abort)
                if (captureStream.Length < maxCaptureBytes)
                {
                    int captureCount = (int)Math.Min(bytesRead, maxCaptureBytes - captureStream.Length);
                    captureStream.Write(buffer, 0, captureCount);
                    if (captureCount < bytesRead)
                    {
                        isTruncated = true;
                    }
                }
                else
                {
                    isTruncated = true;
                }

                // 2. Pass-through immediately to destination and flush
                try
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    clientAborted = true;
                    error = ex;
                    break;
                }

                if (contentLength.HasValue && totalBytesRelayed >= contentLength.Value)
                {
                    upstreamCompleted = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            clientAborted = true;
            error = ex;
        }
        catch (Exception ex)
        {
            error = ex;
            if (IsClientAbort(ex))
            {
                clientAborted = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new RelayResult
        {
            CapturedBytes = captureStream.ToArray(),
            TotalBytesRelayed = totalBytesRelayed,
            IsTruncated = isTruncated,
            ClientAborted = clientAborted,
            UpstreamCompleted = upstreamCompleted,
            Exception = error
        };
    }

    /// <summary>
    /// Relays an HTTP/1.1 chunked transfer encoding stream.
    /// Writes each chunk (with its wire framing) to destination and flushes immediately.
    /// Captures the de-chunked payload (or raw chunks if configured) into memory buffer.
    /// </summary>
    public static async Task<RelayResult> RelayChunkedAsync(
        Stream source,
        Stream destination,
        bool dechunkForCapture = true,
        long maxCaptureBytes = DefaultMaxCaptureBytes,
        int bufferSize = DefaultBufferSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (bufferSize <= 0) bufferSize = DefaultBufferSize;
        if (maxCaptureBytes <= 0) maxCaptureBytes = DefaultMaxCaptureBytes;

        long totalBytesRelayed = 0;
        bool isTruncated = false;
        bool clientAborted = false;
        bool upstreamCompleted = false;
        Exception? error = null;

        using var captureStream = new MemoryStream(64 * 1024);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Read chunk header line: "<hex-size>[;ext]\r\n"
                var headerLineBytes = await ReadLineBytesAsync(source, cancellationToken).ConfigureAwait(false);
                if (headerLineBytes.Length == 0)
                {
                    // Unexpected EOF before chunk header
                    break;
                }

                totalBytesRelayed += headerLineBytes.Length;

                if (!dechunkForCapture && captureStream.Length < maxCaptureBytes)
                {
                    AppendToCapture(captureStream, headerLineBytes, maxCaptureBytes, ref isTruncated);
                }

                // Forward chunk header line to destination
                try
                {
                    await destination.WriteAsync(headerLineBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    clientAborted = true;
                    error = ex;
                    break;
                }

                var headerText = Encoding.ASCII.GetString(headerLineBytes).TrimEnd('\r', '\n');
                var hexPart = headerText.Split(';')[0].Trim();

                if (!long.TryParse(hexPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize) || chunkSize < 0)
                {
                    error = new FormatException($"Invalid HTTP chunk size format: '{headerText}'");
                    break;
                }

                // 2. Terminating chunk (size 0)
                if (chunkSize == 0)
                {
                    bool trailersCompleted = false;
                    // Read trailing headers until empty line "\r\n"
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var trailerLine = await ReadLineBytesAsync(source, MaxChunkLineLength, cancellationToken).ConfigureAwait(false);
                        if (trailerLine.Length == 0)
                        {
                            error = new EndOfStreamException("Premature end of stream while reading chunk trailers.");
                            break;
                        }

                        totalBytesRelayed += trailerLine.Length;
                        if (!dechunkForCapture && captureStream.Length < maxCaptureBytes)
                        {
                            AppendToCapture(captureStream, trailerLine, maxCaptureBytes, ref isTruncated);
                        }

                        try
                        {
                            await destination.WriteAsync(trailerLine.AsMemory(), cancellationToken).ConfigureAwait(false);
                            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            clientAborted = true;
                            error = ex;
                            break;
                        }

                        // Empty line "\r\n" signals end of trailers
                        if ((trailerLine.Length == 2 && trailerLine[0] == (byte)'\r' && trailerLine[1] == (byte)'\n') ||
                            (trailerLine.Length == 1 && trailerLine[0] == (byte)'\n'))
                        {
                            trailersCompleted = true;
                            break;
                        }
                    }

                    if (trailersCompleted && !clientAborted && error is null)
                    {
                        upstreamCompleted = true;
                    }
                    break;
                }

                // 3. Read chunk data of length chunkSize
                long chunkRemaining = chunkSize;
                while (chunkRemaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int toRead = (int)Math.Min(buffer.Length, chunkRemaining);
                    int read = await source.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        error = new EndOfStreamException($"Premature end of stream while reading chunk data. Expected {chunkRemaining} more bytes.");
                        break;
                    }

                    totalBytesRelayed += read;
                    chunkRemaining -= read;

                    // Tee chunk data into memory capture
                    if (captureStream.Length < maxCaptureBytes)
                    {
                        int captureCount = (int)Math.Min(read, maxCaptureBytes - captureStream.Length);
                        captureStream.Write(buffer, 0, captureCount);
                        if (captureCount < read)
                        {
                            isTruncated = true;
                        }
                    }
                    else
                    {
                        isTruncated = true;
                    }

                    // Forward chunk data immediately to destination and flush
                    try
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        clientAborted = true;
                        error = ex;
                        break;
                    }
                }

                if (clientAborted || error is not null)
                {
                    break;
                }

                // 4. Read chunk trailing CRLF "\r\n"
                var crlfBytes = await ReadExactBytesAsync(source, 2, cancellationToken).ConfigureAwait(false);
                if (crlfBytes.Length > 0)
                {
                    totalBytesRelayed += crlfBytes.Length;

                    if (!dechunkForCapture && captureStream.Length < maxCaptureBytes)
                    {
                        AppendToCapture(captureStream, crlfBytes, maxCaptureBytes, ref isTruncated);
                    }

                    try
                    {
                        await destination.WriteAsync(crlfBytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        clientAborted = true;
                        error = ex;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            clientAborted = true;
            error = ex;
        }
        catch (Exception ex)
        {
            error = ex;
            if (IsClientAbort(ex))
            {
                clientAborted = true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new RelayResult
        {
            CapturedBytes = captureStream.ToArray(),
            TotalBytesRelayed = totalBytesRelayed,
            IsTruncated = isTruncated,
            ClientAborted = clientAborted,
            UpstreamCompleted = upstreamCompleted,
            Exception = error
        };
    }

    /// <summary>
    /// Reads a line ending in \n (with optional preceding \r) from source stream byte-by-byte.
    /// Note: HTTP/1.1 chunk headers and trailers are very small (typically 3-16 bytes).
    /// </summary>
    private static Task<byte[]> ReadLineBytesAsync(Stream source, CancellationToken cancellationToken)
        => ReadLineBytesAsync(source, MaxChunkLineLength, cancellationToken);

    private static async Task<byte[]> ReadLineBytesAsync(
        Stream source,
        int maxLineLength,
        CancellationToken cancellationToken = default)
    {
        var lineBytes = new List<byte>(32);
        var singleByte = new byte[1];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(singleByte.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            byte b = singleByte[0];
            lineBytes.Add(b);

            if (b == (byte)'\n')
            {
                break;
            }

            if (lineBytes.Count >= maxLineLength)
            {
                throw new InvalidDataException($"HTTP chunk line exceeded maximum length of {maxLineLength} bytes without a newline.");
            }
        }

        return lineBytes.ToArray();
    }

    /// <summary>
    /// Reads exactly count bytes from stream, or fewer if EOF is encountered.
    /// </summary>
    private static async Task<byte[]> ReadExactBytesAsync(Stream source, int count, CancellationToken cancellationToken)
    {
        var result = new byte[count];
        int totalRead = 0;

        while (totalRead < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(result.AsMemory(totalRead, count - totalRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead < count)
        {
            Array.Resize(ref result, totalRead);
        }

        return result;
    }

    private static void AppendToCapture(MemoryStream stream, byte[] bytes, long maxBytes, ref bool isTruncated)
    {
        if (stream.Length >= maxBytes)
        {
            isTruncated = true;
            return;
        }

        int count = (int)Math.Min(bytes.Length, maxBytes - stream.Length);
        stream.Write(bytes, 0, count);
        if (count < bytes.Length)
        {
            isTruncated = true;
        }
    }

    /// <summary>
    /// Detects whether an exception was caused by a client-side disconnect, socket reset,
    /// broken pipe, or operation cancellation (e.g. user pressing Ctrl+C).
    /// </summary>
    public static bool IsClientAbort(Exception? ex)
    {
        if (ex is null) return false;
        if (ex is OperationCanceledException or ObjectDisposedException) return true;

        if (ex is SocketException sex)
        {
            return IsSocketErrorAbort(sex.SocketErrorCode);
        }

        if (ex is IOException iex)
        {
            var msg = iex.Message;
            if (msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("connection was aborted", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("unexpected end of stream", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("stream was aborted", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("connection closed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex.InnerException is not null && IsClientAbort(ex.InnerException))
        {
            return true;
        }

        if (ex is AggregateException aex)
        {
            foreach (var inner in aex.InnerExceptions)
            {
                if (IsClientAbort(inner))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsSocketErrorAbort(SocketError error)
    {
        return error is SocketError.ConnectionReset
                     or SocketError.ConnectionAborted
                     or SocketError.NetworkReset
                     or SocketError.Shutdown
                     or SocketError.TimedOut
                     or SocketError.OperationAborted;
    }
}