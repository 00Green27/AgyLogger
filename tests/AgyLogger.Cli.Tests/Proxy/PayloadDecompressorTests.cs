namespace AgyLogger.Cli.Tests.Proxy;

using System;
using System.IO;
using System.IO.Compression;
using System.Text;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

public class PayloadDecompressorTests
{
    // ---------------------------------------------------------------------------
    // Category 1: Identity & Empty Payloads
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_NullRawBytes_ReturnsEmptyArrayAndNullWarning()
    {
        var (bytes, warning) = PayloadDecompressor.Decompress(null, "gzip");
        Assert.Empty(bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_EmptyRawBytes_ReturnsEmptyArrayAndNullWarning()
    {
        var (bytes, warning) = PayloadDecompressor.Decompress(Array.Empty<byte>(), "gzip");
        Assert.Empty(bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_NullEncoding_LeavesRawBytesUntouched()
    {
        var raw = Encoding.UTF8.GetBytes("hello world");
        var (bytes, warning) = PayloadDecompressor.Decompress(raw, null);
        Assert.Equal(raw, bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_EmptyEncoding_LeavesRawBytesUntouched()
    {
        var raw = Encoding.UTF8.GetBytes("hello world");
        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "");
        Assert.Equal(raw, bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_WhitespaceEncoding_LeavesRawBytesUntouched()
    {
        var raw = Encoding.UTF8.GetBytes("hello world");
        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "   ");
        Assert.Equal(raw, bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_IdentityEncoding_LeavesRawBytesUntouched()
    {
        var raw = Encoding.UTF8.GetBytes("hello world");
        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "identity");
        Assert.Equal(raw, bytes);
        Assert.Null(warning);
    }

    [Fact]
    public void Decompress_IdentityEncodingWithMixedCaseAndWhitespace_LeavesRawBytesUntouched()
    {
        var raw = Encoding.UTF8.GetBytes("hello world");
        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "  Identity  ");
        Assert.Equal(raw, bytes);
        Assert.Null(warning);
    }

    // ---------------------------------------------------------------------------
    // Category 2: GZip Decompression
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_ValidGzip_DecompressesCorrectly()
    {
        const string expected = "hello world from gzip compression";
        var compressed = CompressGzip(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "gzip");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("GZIP")]
    [InlineData(" Gzip  ")]
    public void Decompress_ValidGzip_CaseInsensitiveEncoding(string encoding)
    {
        const string expected = "hello case insensitive gzip";
        var compressed = CompressGzip(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, encoding);

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decompress_XGzipAlias_DecompressesCorrectly()
    {
        const string expected = "hello x-gzip";
        var compressed = CompressGzip(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "x-gzip");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decompress_GzipWithParameters_DecompressesCorrectly()
    {
        const string expected = "hello gzip with parameters";
        var compressed = CompressGzip(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "gzip; q=1.0");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decompress_LargeGzipPayload_DecompressesCorrectly()
    {
        var builder = new StringBuilder();
        for (var i = 0; i < 5000; i++)
        {
            builder.Append("{\"index\":").Append(i).Append(",\"text\":\"large payload stream test\"}\n");
        }
        var expected = builder.ToString();
        var compressed = CompressGzip(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "gzip");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    // ---------------------------------------------------------------------------
    // Category 3: Deflate & ZLib Decompression
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_ValidZlibDeflate_DecompressesCorrectly()
    {
        const string expected = "hello world from zlib RFC 1950";
        var compressed = CompressZLib(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "deflate");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decompress_ValidRawDeflate_DecompressesCorrectly()
    {
        const string expected = "hello world from raw deflate RFC 1951";
        var compressed = CompressRawDeflate(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "deflate");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Decompress_Deflate_CaseInsensitiveAndWhitespace()
    {
        const string expected = "hello deflate case";
        var compressed = CompressZLib(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, " DEFLATE ");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    // ---------------------------------------------------------------------------
    // Category 4: Brotli Decompression
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_ValidBrotli_DecompressesCorrectly()
    {
        const string expected = "hello world from brotli RFC 7932";
        var compressed = CompressBrotli(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, "br");

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("BR")]
    [InlineData("brotli")]
    [InlineData("  Brotli ")]
    public void Decompress_BrotliAlias_DecompressesCorrectly(string encoding)
    {
        const string expected = "hello brotli alias";
        var compressed = CompressBrotli(expected);

        var (bytes, warning) = PayloadDecompressor.Decompress(compressed, encoding);

        Assert.Null(warning);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    // ---------------------------------------------------------------------------
    // Category 5: Unsupported Encodings
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_UnsupportedEncodingZstd_ReturnsRawBytesAndExplicitBanner()
    {
        var raw = Encoding.UTF8.GetBytes("not really zstd compressed");

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "zstd");

        Assert.Equal(raw, bytes);
        Assert.Equal("[request-logger] COULD NOT DECODE THIS BODY \u2014 content-encoding \"zstd\" is not one this tool decodes", warning);
    }

    [Fact]
    public void Decompress_UnsupportedEncodingCompress_ReturnsRawBytesAndExplicitBanner()
    {
        var raw = Encoding.UTF8.GetBytes("legacy compress data");

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "compress");

        Assert.Equal(raw, bytes);
        Assert.Equal("[request-logger] COULD NOT DECODE THIS BODY \u2014 content-encoding \"compress\" is not one this tool decodes", warning);
    }

    [Fact]
    public void Decompress_UnsupportedEncodingUnknown_ReturnsRawBytesAndExplicitBanner()
    {
        var raw = Encoding.UTF8.GetBytes("arbitrary payload");

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "custom-algo");

        Assert.Equal(raw, bytes);
        Assert.Equal("[request-logger] COULD NOT DECODE THIS BODY \u2014 content-encoding \"custom-algo\" is not one this tool decodes", warning);
    }

    // ---------------------------------------------------------------------------
    // Category 6: Corrupted / Truncated / Invalid Data
    // ---------------------------------------------------------------------------

    [Fact]
    public void Decompress_CorruptedGzip_ReturnsRawBytesAndClaimWarning()
    {
        var raw = Encoding.UTF8.GetBytes("this is not compressed gzip data");

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "gzip");

        Assert.Equal(raw, bytes);
        Assert.NotNull(warning);
        Assert.StartsWith("[request-logger] COULD NOT DECODE THIS BODY \u2014 this body claims content-encoding \"gzip\" but did not decode as that:", warning);
    }

    [Fact]
    public void Decompress_TruncatedGzip_ReturnsRawBytesAndClaimWarning()
    {
        var compressed = CompressGzip("complete payload before truncation");
        var truncated = compressed[..6]; // cut prematurely

        var (bytes, warning) = PayloadDecompressor.Decompress(truncated, "gzip");

        Assert.Equal(truncated, bytes);
        Assert.NotNull(warning);
        Assert.StartsWith("[request-logger] COULD NOT DECODE THIS BODY \u2014 this body claims content-encoding \"gzip\" but did not decode as that:", warning);
    }

    [Fact]
    public void Decompress_CorruptedBrotli_ReturnsRawBytesAndClaimWarning()
    {
        var raw = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "br");

        Assert.Equal(raw, bytes);
        Assert.NotNull(warning);
        Assert.StartsWith("[request-logger] COULD NOT DECODE THIS BODY \u2014 this body claims content-encoding \"br\" but did not decode as that:", warning);
    }

    [Fact]
    public void Decompress_CorruptedDeflate_ReturnsRawBytesAndClaimWarning()
    {
        var raw = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC };

        var (bytes, warning) = PayloadDecompressor.Decompress(raw, "deflate");

        Assert.Equal(raw, bytes);
        Assert.NotNull(warning);
        Assert.StartsWith("[request-logger] COULD NOT DECODE THIS BODY \u2014 this body claims content-encoding \"deflate\" but did not decode as that:", warning);
    }

    // ---------------------------------------------------------------------------
    // Category 7: DecompressToText & Failure Banner Formatting
    // ---------------------------------------------------------------------------

    [Fact]
    public void DecompressToText_ValidPayload_ReturnsDecodedText()
    {
        const string expected = "{\"model\":\"gemini-2.5-pro\",\"prompt\":\"write code\"}";
        var compressed = CompressGzip(expected);

        var result = PayloadDecompressor.DecompressToText(compressed, "gzip");

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecompressToText_UnsupportedZstd_ReturnsBannerWithRawText()
    {
        var raw = Encoding.UTF8.GetBytes("raw zstd content");

        var result = PayloadDecompressor.DecompressToText(raw, "zstd");

        var expected =
            "[request-logger] COULD NOT DECODE THIS BODY \u2014 content-encoding \"zstd\" is not one this tool decodes\n\n" +
            "The bytes below are still compressed. They are NOT what was actually sent as text:\n\n" +
            "raw zstd content";

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecompressToText_CorruptedGzip_ReturnsBannerWithRawText()
    {
        const string rawText = "plain text claiming gzip";
        var raw = Encoding.UTF8.GetBytes(rawText);

        var result = PayloadDecompressor.DecompressToText(raw, "gzip");

        Assert.Contains("[request-logger] COULD NOT DECODE THIS BODY \u2014 this body claims content-encoding \"gzip\" but did not decode as that:", result);
        Assert.Contains("The bytes below are still compressed. They are NOT what was actually sent as text:", result);
        Assert.Contains(rawText, result);
    }

    [Fact]
    public void DecompressToText_NullOrEmptyBytes_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, PayloadDecompressor.DecompressToText(null, "gzip"));
        Assert.Equal(string.Empty, PayloadDecompressor.DecompressToText(Array.Empty<byte>(), "gzip"));
    }

    // ---------------------------------------------------------------------------
    // Compression Test Helpers
    // ---------------------------------------------------------------------------

    private static byte[] CompressGzip(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CompressZLib(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var zl = new ZLibStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            zl.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CompressRawDeflate(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var def = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            def.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    private static byte[] CompressBrotli(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionMode.Compress, leaveOpen: true))
        {
            br.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }
}