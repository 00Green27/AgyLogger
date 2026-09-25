namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Low-level HTTP/1.1 wire protocol framing, header parsing, and byte stream transport helpers.
/// </summary>
public static class HttpWireProtocol
{
    public static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "TE",
        "Trailers",
        "Upgrade",
        "Proxy-Authorization",
        "Proxy-Authenticate"
    };

    private const int MaxHeaderSizeBytes = 64 * 1024;
    private const int HeaderReadChunkSize = 4096;

    public static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = await ReadUntilHeaderEndAsync(stream, cancellationToken).ConfigureAwait(false);
        if (result is null || result.HeaderBytes.Length == 0)
        {
            return null;
        }

        var headerString = Encoding.ASCII.GetString(result.HeaderBytes);
        var lines = headerString.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLineParts = lines[0].Split(' ', 3);
        if (requestLineParts.Length < 2)
        {
            return null;
        }

        var method = requestLineParts[0].Trim();
        var path = requestLineParts[1].Trim();
        var version = requestLineParts.Length > 2 ? requestLineParts[2].Trim() : "HTTP/1.1";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                var key = lines[i][..colonIdx].Trim();
                var val = lines[i][(colonIdx + 1)..].Trim();
                headers[key] = val;
            }
        }

        byte[] body = [];
        bool isChunked = headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (isChunked)
        {
            using var sourceStream = new PrefixStream(result.RemainingBytes, stream, leaveInnerStreamOpen: true);
            var relayResult = await StreamRelay.RelayChunkedAsync(sourceStream, Stream.Null, dechunkForCapture: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            body = relayResult.CapturedBytes;
        }
        else if (headers.TryGetValue("Content-Length", out var clStr) && long.TryParse(clStr, out var clLong) && clLong > 0 && clLong <= int.MaxValue)
        {
            var cl = (int)clLong;
            body = new byte[cl];
            var totalRead = 0;

            if (result.RemainingBytes.Length > 0)
            {
                var copyLen = Math.Min(cl, result.RemainingBytes.Length);
                Buffer.BlockCopy(result.RemainingBytes, 0, body, 0, copyLen);
                totalRead = copyLen;
            }

            while (totalRead < cl)
            {
                var r = await stream.ReadAsync(body.AsMemory(totalRead, cl - totalRead), cancellationToken).ConfigureAwait(false);
                if (r == 0) break;
                totalRead += r;
            }
            if (totalRead < cl)
            {
                Array.Resize(ref body, totalRead);
            }
        }

        return new ParsedHttpRequest
        {
            Method = method,
            Path = path,
            HttpVersion = version,
            Headers = headers,
            Body = body
        };
    }

    public static async Task<(ParsedHttpResponseHeaders? Headers, byte[] RemainingBytes)> ReadHttpResponseHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var result = await ReadUntilHeaderEndAsync(stream, cancellationToken).ConfigureAwait(false);
        if (result is null || result.HeaderBytes.Length == 0)
        {
            return (null, []);
        }

        var headerString = Encoding.ASCII.GetString(result.HeaderBytes);
        var lines = headerString.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return (null, []);
        }

        var statusLineParts = lines[0].Split(' ', 3);
        if (statusLineParts.Length < 2 || !int.TryParse(statusLineParts[1], out var statusCode))
        {
            return (null, []);
        }

        var statusDesc = statusLineParts.Length > 2 ? statusLineParts[2].Trim() : "OK";

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colonIdx = lines[i].IndexOf(':');
            if (colonIdx > 0)
            {
                var key = lines[i][..colonIdx].Trim();
                var val = lines[i][(colonIdx + 1)..].Trim();
                headers[key] = val;
            }
        }

        var parsedHeaders = new ParsedHttpResponseHeaders
        {
            StatusCode = statusCode,
            StatusDescription = statusDesc,
            Headers = headers
        };

        return (parsedHeaders, result.RemainingBytes);
    }

    private static async Task<HeaderReadResult?> ReadUntilHeaderEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var mem = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(HeaderReadChunkSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, HeaderReadChunkSize), cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return null;
                }

                var prevLen = (int)mem.Length;
                mem.Write(buffer, 0, bytesRead);

                if (mem.Length > MaxHeaderSizeBytes)
                {
                    throw new InvalidDataException("HTTP request/response header exceeded maximum size of 64KB.");
                }

                var currentLen = (int)mem.Length;
                var raw = mem.GetBuffer();
                var searchStart = Math.Max(0, prevLen - 3);
                var searchEnd = currentLen - 4;
                var foundIndex = -1;

                for (var i = searchStart; i <= searchEnd; i++)
                {
                    if (raw[i] == (byte)'\r' && raw[i + 1] == (byte)'\n' &&
                        raw[i + 2] == (byte)'\r' && raw[i + 3] == (byte)'\n')
                    {
                        foundIndex = i;
                        break;
                    }
                }

                if (foundIndex >= 0)
                {
                    var headerEnd = foundIndex + 4;
                    var headerBytes = new byte[headerEnd];
                    Buffer.BlockCopy(raw, 0, headerBytes, 0, headerEnd);

                    var remainingLen = currentLen - headerEnd;
                    byte[] remainingBytes = remainingLen > 0 ? new byte[remainingLen] : [];
                    if (remainingLen > 0)
                    {
                        Buffer.BlockCopy(raw, headerEnd, remainingBytes, 0, remainingLen);
                    }

                    return new HeaderReadResult(headerBytes, remainingBytes);
                }
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task SendForwardedRequestAsync(
        Stream upstreamStream,
        ParsedHttpRequest request,
        string targetHost,
        string pathAndQuery,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append($"{request.Method} {pathAndQuery} HTTP/1.1\r\n");
        sb.Append($"Host: {targetHost}\r\n");

        foreach (var (k, v) in request.Headers)
        {
            if (HopByHopHeaders.Contains(k)) continue;
            if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            // Force identity so upstream returns uncompressed body if possible
            if (k.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

            sb.Append($"{k}: {v}\r\n");
        }

        if (!request.Headers.ContainsKey("Content-Length") &&
            (request.Body.Length > 0 ||
             request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
             request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
             request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)))
        {
            sb.Append($"Content-Length: {request.Body.Length}\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await upstreamStream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);

        if (request.Body.Length > 0)
        {
            await upstreamStream.WriteAsync(request.Body, cancellationToken).ConfigureAwait(false);
        }

        await upstreamStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SendResponseHeadersToClientAsync(
        Stream clientStream,
        ParsedHttpResponseHeaders response,
        bool isChunked,
        bool closeConnection,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {response.StatusCode} {response.StatusDescription}\r\n");

        foreach (var (k, v) in response.Headers)
        {
            if (HopByHopHeaders.Contains(k)) continue;
            sb.Append($"{k}: {v}\r\n");
        }

        if (isChunked)
        {
            sb.Append("Transfer-Encoding: chunked\r\n");
        }

        if (closeConnection)
        {
            sb.Append("Connection: close\r\n");
        }

        sb.Append("\r\n");
        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await clientStream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task Send502BadGatewayAsync(Stream clientStream, string message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = "Bad Gateway", message });
        var header = $"HTTP/1.1 502 Bad Gateway\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        await clientStream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
        await clientStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await clientStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string CombineUrlPaths(string basePath, string requestPath)
    {
        var baseTrim = basePath.TrimEnd('/');
        var reqTrim = requestPath.StartsWith('/') ? requestPath : "/" + requestPath;
        if (string.IsNullOrEmpty(baseTrim)) return reqTrim;
        return baseTrim + reqTrim;
    }

    public static bool ShouldCloseConnection(IReadOnlyDictionary<string, string> reqHeaders, IReadOnlyDictionary<string, string> respHeaders)
    {
        if (reqHeaders.TryGetValue("Connection", out var reqConn) && reqConn.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (respHeaders.TryGetValue("Connection", out var respConn) && respConn.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    private sealed record HeaderReadResult(byte[] HeaderBytes, byte[] RemainingBytes);
}