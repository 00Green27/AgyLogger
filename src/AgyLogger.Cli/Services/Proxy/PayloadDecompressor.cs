using System.IO.Compression;
using System.Text;

namespace AgyLogger.Cli.Services.Proxy;

/// <summary>
/// Handles transparent decompression of HTTP request and response payloads
/// using built-in .NET compression streams (GZipStream, DeflateStream, ZLibStream, BrotliStream).
/// Provides defensive fallbacks and warning banners for corrupted or unsupported formats
/// matching ai-coding-crash-course/request-logger.
/// </summary>
public static class PayloadDecompressor
{
    public const string WarningBannerPrefix = "[request-logger] COULD NOT DECODE THIS BODY";
    public const string CompressedDisclaimer = "The bytes below are still compressed. They are NOT what was actually sent as text:";

    /// <summary>
    /// Decompresses raw payload bytes according to the specified content-encoding header.
    /// If decompression succeeds, returns the decompressed bytes and null warning.
    /// If decompression fails or encoding is unsupported, returns the original raw bytes and an explicit warning banner.
    /// </summary>
    /// <param name="rawBytes">The raw payload bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding header value (e.g. gzip, deflate, br, identity).</param>
    /// <returns>A tuple containing the decoded (or raw) bytes and an optional warning banner.</returns>
    public static (byte[] DecodedBytes, string? Warning) Decompress(byte[]? rawBytes, string? contentEncoding)
    {
        if (rawBytes is null || rawBytes.Length == 0)
        {
            return (Array.Empty<byte>(), null);
        }

        var kind = NormalizeEncoding(contentEncoding);

        if (string.IsNullOrEmpty(kind) || kind == "identity")
        {
            return (rawBytes, null);
        }

        try
        {
            return kind switch
            {
                "gzip" or "x-gzip" => (DecompressGzip(rawBytes), null),
                "deflate" => (DecompressDeflate(rawBytes), null),
                "br" or "brotli" => (DecompressBrotli(rawBytes), null),
                _ => (rawBytes, $"{WarningBannerPrefix} \u2014 content-encoding \"{kind}\" is not one this tool decodes")
            };
        }
        catch (Exception ex)
        {
            return (rawBytes, $"{WarningBannerPrefix} \u2014 this body claims content-encoding \"{kind}\" but did not decode as that: {ex.Message}");
        }
    }

    /// <summary>
    /// Decompresses raw payload bytes and returns the result as a UTF-8 string.
    /// If decompression fails or encoding is unsupported, returns the explicit warning banner
    /// prepended to the UTF-8 representation of the raw bytes, matching ai-coding-crash-course/request-logger.
    /// </summary>
    /// <param name="rawBytes">The raw payload bytes.</param>
    /// <param name="contentEncoding">The Content-Encoding header value.</param>
    /// <returns>The decoded string or the failure warning banner with raw bytes.</returns>
    public static string DecompressToText(byte[]? rawBytes, string? contentEncoding)
    {
        if (rawBytes is null || rawBytes.Length == 0)
        {
            return string.Empty;
        }

        var (decodedBytes, warning) = Decompress(rawBytes, contentEncoding);
        if (warning is null)
        {
            return Encoding.UTF8.GetString(decodedBytes);
        }

        return FormatFailureMessage(warning, rawBytes);
    }

    /// <summary>
    /// Formats the complete failure warning message containing the warning banner, disclaimer, and raw bytes.
    /// </summary>
    /// <param name="warningBanner">The warning banner line.</param>
    /// <param name="rawBytes">The original raw bytes.</param>
    /// <returns>The formatted failure string.</returns>
    public static string FormatFailureMessage(string warningBanner, byte[] rawBytes)
    {
        return $"{warningBanner}\n\n" +
               $"{CompressedDisclaimer}\n\n" +
               Encoding.UTF8.GetString(rawBytes);
    }

    private static string NormalizeEncoding(string? encoding)
    {
        if (string.IsNullOrWhiteSpace(encoding))
        {
            return string.Empty;
        }

        var trimmed = encoding.Trim().ToLowerInvariant();
        var semicolonIndex = trimmed.IndexOf(';');
        if (semicolonIndex >= 0)
        {
            trimmed = trimmed[..semicolonIndex].Trim();
        }

        return trimmed;
    }

    private static byte[] DecompressGzip(byte[] rawBytes)
    {
        if (rawBytes.Length < 10)
        {
            throw new InvalidDataException("Truncated or incomplete gzip header.");
        }
        using var inputStream = new MemoryStream(rawBytes, writable: false);
        using var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        gzipStream.CopyTo(outputStream);
        if (outputStream.Length == 0 && rawBytes.Length > 0)
        {
            throw new InvalidDataException("Gzip stream produced no output for non-empty input.");
        }
        return outputStream.ToArray();
    }

    private static byte[] DecompressDeflate(byte[] rawBytes)
    {
        // RFC 2616 / RFC 7230 deflate is zlib-wrapped (RFC 1950).
        // Try ZLibStream first.
        try
        {
            using var inputStream = new MemoryStream(rawBytes, writable: false);
            using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            zlibStream.CopyTo(outputStream);
            if (outputStream.Length == 0 && rawBytes.Length > 0)
            {
                throw new InvalidDataException("Zlib stream produced no output for non-empty input.");
            }
            return outputStream.ToArray();
        }
        catch (InvalidDataException)
        {
            // Fall back to raw RFC 1951 DeflateStream (some implementations omit zlib headers).
            using var inputStream = new MemoryStream(rawBytes, writable: false);
            using var deflateStream = new DeflateStream(inputStream, CompressionMode.Decompress);
            using var outputStream = new MemoryStream();
            deflateStream.CopyTo(outputStream);
            if (outputStream.Length == 0 && rawBytes.Length > 0)
            {
                throw new InvalidDataException("Deflate stream produced no output for non-empty input.");
            }
            return outputStream.ToArray();
        }
    }

    private static byte[] DecompressBrotli(byte[] rawBytes)
    {
        using var inputStream = new MemoryStream(rawBytes, writable: false);
        using var brotliStream = new BrotliStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        brotliStream.CopyTo(outputStream);
        if (outputStream.Length == 0 && rawBytes.Length > 0)
        {
            throw new InvalidDataException("Brotli stream produced no output for non-empty input.");
        }
        return outputStream.ToArray();
    }
}