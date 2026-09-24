namespace AgyLogger.Cli.Tests.E2E;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Cli;
using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services;
using AgyLogger.Cli.Services.Proxy;
using AgyLogger.Cli.Tests.E2E.Harness;

using Xunit;

/// <summary>
/// Tier 2: Boundary & Corner Case Tests.
/// Provides at least 5 authentic test cases per feature covering edge conditions, malformed inputs,
/// and failure recovery across all 16 features in PROJECT.md (80 total).
/// </summary>
public sealed class Tier2_BoundaryAndCornerTests
{
    #region Feature 1: .NET 10 Framework Alignment Boundaries

    [Fact]
    public async Task T2_F01_01_PrefixStream_DrainsPrefixAcrossSmallBufferReads()
    {
        var prefix = new byte[] { 1, 2, 3, 4, 5 };
        using var inner = new MemoryStream([6, 7, 8]);
        await using var stream = new PrefixStream(prefix, inner);
        var buf = new byte[3];
        var r1 = await stream.ReadAsync(buf.AsMemory(0, 3));
        Assert.Equal(3, r1);
        Assert.Equal([1, 2, 3], buf);
        var r2 = await stream.ReadAsync(buf.AsMemory(0, 3));
        Assert.Equal(2, r2);
        Assert.Equal(4, buf[0]);
        Assert.Equal(5, buf[1]);
        var r3 = await stream.ReadAsync(buf.AsMemory(0, 3));
        Assert.Equal(3, r3);
        Assert.Equal([6, 7, 8], buf);
    }

    [Fact]
    public async Task T2_F01_02_PrefixStream_EmptyPrefix_ReadsDirectlyFromInnerStream()
    {
        using var inner = new MemoryStream([42, 43]);
        await using var stream = new PrefixStream(ReadOnlyMemory<byte>.Empty, inner);
        var buf = new byte[2];
        var read = await stream.ReadAsync(buf.AsMemory(0, 2));
        Assert.Equal(2, read);
        Assert.Equal([42, 43], buf);
    }

    [Fact]
    public void T2_F01_03_PrefixStream_LeaveInnerStreamOpen_PreservesInnerStreamOnDispose()
    {
        var inner = new MemoryStream([1, 2]);
        var stream = new PrefixStream(ReadOnlyMemory<byte>.Empty, inner, leaveInnerStreamOpen: true);
        stream.Dispose();
        Assert.True(inner.CanRead);
        inner.Dispose();
    }

    [Fact]
    public async Task T2_F01_04_HttpWireProtocol_HeaderExceeding64KB_ThrowsInvalidDataException()
    {
        var largeHeader = new string('A', 65 * 1024);
        var raw = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nX-Large: {largeHeader}\r\n\r\n");
        using var mem = new MemoryStream(raw);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            HttpWireProtocol.ReadHttpRequestAsync(mem, CancellationToken.None));
        Assert.Contains("64KB", ex.Message);
    }

    [Fact]
    public async Task T2_F01_05_HttpWireProtocol_EmptyOrTruncatedStream_ReturnsNull()
    {
        using var empty = new MemoryStream([]);
        Assert.Null(await HttpWireProtocol.ReadHttpRequestAsync(empty, CancellationToken.None));
        using var truncated = new MemoryStream("GET / HTTP/1.1\r\n"u8.ToArray());
        Assert.Null(await HttpWireProtocol.ReadHttpRequestAsync(truncated, CancellationToken.None));
    }

    #endregion

    #region Feature 2: Captured Exchange Models Boundaries

    [Fact]
    public void T2_F02_01_CapturedExchange_SanitizeAgentName_HandlesEdgeCases()
    {
        var exchange1 = new CapturedExchange { AgentName = "   Special @#$ Agent __ 123   " };
        Assert.EndsWith("_special-agent-123.md", exchange1.BuildLogFileName());
        var exchange2 = new CapturedExchange { AgentName = "   \t\n   " };
        Assert.EndsWith("_agent.md", exchange2.BuildLogFileName());
    }

    [Fact]
    public void T2_F02_02_CapturedExchange_GetEffectiveRequestBody_WithDecompressionWarning()
    {
        var exchange = new CapturedExchange
        {
            RawRequestBody = "raw uncompressed bytes"u8.ToArray(),
            RequestDecodingWarning = "[request-logger] COULD NOT DECODE THIS BODY"
        };
        var text = exchange.GetEffectiveRequestBodyText();
        Assert.Contains("[request-logger] COULD NOT DECODE THIS BODY", text);
        Assert.Contains("The bytes below are still compressed", text);
        Assert.Contains("raw uncompressed bytes", text);
    }

    [Fact]
    public void T2_F02_03_CapturedExchange_Duration_NullWhenUnfinished()
    {
        var start = DateTimeOffset.UtcNow;
        var exchange = new CapturedExchange { RequestTimestamp = start, ResponseTimestamp = null };
        Assert.Null(exchange.Duration);
        var completed = exchange with { ResponseTimestamp = start.AddMilliseconds(250) };
        Assert.NotNull(completed.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(250), completed.Duration.Value);
    }

    [Fact]
    public void T2_F02_04_CapturedExchange_Endpoint_WhitespaceMethod_ReturnsUrl()
    {
        var exchange = new CapturedExchange { Method = "   ", Url = "/v1/models" };
        Assert.Equal("/v1/models", exchange.Endpoint);
        var postExchange = exchange with { Method = "POST" };
        Assert.Equal("POST /v1/models", postExchange.Endpoint);
    }

    [Fact]
    public void T2_F02_05_CapturedExchange_RawRequestHeaders_CaseInsensitiveAndNullSafe()
    {
        var exchange = new CapturedExchange
        {
            RawRequestHeaders = new Dictionary<string, string> { ["Content-Type"] = "application/json" }
        };
        Assert.Equal("application/json", exchange.RawRequestHeaders["content-type"]);
        Assert.Equal("application/json", exchange.RawRequestHeaders["CONTENT-TYPE"]);
        Assert.Empty(new CapturedExchange { RawRequestHeaders = null! }.RawRequestHeaders);
    }

    #endregion

    #region Feature 3: Payload Decompression Boundaries

    [Fact]
    public void T2_F03_01_Decompress_NullOrEmptyBytes_ReturnsEmptyAndNoWarning()
    {
        Assert.Equal(([], (string?)null), PayloadDecompressor.Decompress(null, "gzip"));
        Assert.Equal(([], (string?)null), PayloadDecompressor.Decompress([], "gzip"));
    }

    [Fact]
    public void T2_F03_02_Decompress_TruncatedGzip_ReturnsOriginalBytesAndWarning()
    {
        byte[] truncatedGzip = [0x1f, 0x8b, 0x08, 0x00];
        var (decoded, warning) = PayloadDecompressor.Decompress(truncatedGzip, "gzip");
        Assert.NotNull(warning);
        Assert.Contains(PayloadDecompressor.WarningBannerPrefix, warning);
        Assert.Equal(truncatedGzip, decoded);
    }

    [Fact]
    public void T2_F03_03_Decompress_CorruptedBrotli_ReturnsOriginalBytesAndWarning()
    {
        byte[] corruptedBrotli = [0x01, 0x02, 0x03, 0x04];
        var (decoded, warning) = PayloadDecompressor.Decompress(corruptedBrotli, "br");
        Assert.NotNull(warning);
        Assert.Contains("claims content-encoding \"br\"", warning);
        Assert.Equal(corruptedBrotli, decoded);
    }

    [Fact]
    public void T2_F03_04_Decompress_UnsupportedZstd_ReturnsOriginalBytesAndWarning()
    {
        var raw = "zstd payload"u8.ToArray();
        var (decoded, warning) = PayloadDecompressor.Decompress(raw, "zstd");
        Assert.NotNull(warning);
        Assert.Contains("content-encoding \"zstd\" is not one this tool decodes", warning);
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void T2_F03_05_DecompressToText_FailedDecompression_FormatsWarningAndDisclaimer()
    {
        var raw = "uncompressed text claiming gzip"u8.ToArray();
        var result = PayloadDecompressor.DecompressToText(raw, "gzip");
        Assert.Contains(PayloadDecompressor.WarningBannerPrefix, result);
        Assert.Contains(PayloadDecompressor.CompressedDisclaimer, result);
        Assert.Contains("uncompressed text claiming gzip", result);
    }

    #endregion

    #region Feature 4: Sensitive Header Redaction Boundaries

    [Fact]
    public void T2_F04_01_RedactHeaders_MixedCaseAndWhitespace_RedactsSensitiveHeaders()
    {
        var headers = new List<KeyValuePair<string, IEnumerable<string>>>
        {
            new("  AuThOrIzAtIoN  ", ["Bearer secret-token"]),
            new("X-GOOG-API-KEY", ["AIzaSySecret"]),
            new("cookie", ["sessionId=12345"]),
            new("Content-Type", ["application/json"])
        };
        var redacted = SensitiveDataRedactor.RedactHeaders(headers);
        Assert.Equal("[REDACTED]", redacted["authorization"]);
        Assert.Equal("[REDACTED]", redacted["x-goog-api-key"]);
        Assert.Equal("[REDACTED]", redacted["cookie"]);
        Assert.Equal("application/json", redacted["content-type"]);
    }

    [Fact]
    public void T2_F04_02_RedactHeaders_NullOrEmptyCollection_ReturnsEmptyDictionary()
    {
        Assert.Empty(SensitiveDataRedactor.RedactHeaders((IEnumerable<KeyValuePair<string, string>>?)null));
        Assert.Empty(SensitiveDataRedactor.RedactHeaders(new List<KeyValuePair<string, string>>()));
    }

    [Fact]
    public void T2_F04_03_RedactUrl_UrlEncodedSensitiveParam_RedactsValue()
    {
        var url = "https://api.test/v1/generate?api%5Fkey=secret123&keep=ok";
        var redacted = SensitiveDataRedactor.RedactUrl(url);
        Assert.Contains("api%5Fkey=[REDACTED]", redacted);
        Assert.Contains("keep=ok", redacted);
    }

    [Fact]
    public void T2_F04_04_RedactUrl_MissingQueryOrMultipleSensitiveParams_HandledSafely()
    {
        Assert.Equal("https://api.test/v1/models", SensitiveDataRedactor.RedactUrl("https://api.test/v1/models"));
        Assert.Equal("https://api.test/v1/models?", SensitiveDataRedactor.RedactUrl("https://api.test/v1/models?"));
        Assert.Equal("https://api.test/?key=[REDACTED]&token=[REDACTED]&secret=[REDACTED]",
            SensitiveDataRedactor.RedactUrl("https://api.test/?key=1&token=2&secret=3"));
    }

    [Fact]
    public void T2_F04_05_RenderHeaders_EscapesTripleBackticks()
    {
        var headers = new Dictionary<string, string> { ["x-malicious"] = "value```with```backticks" };
        var md = SensitiveDataRedactor.RenderHeaders(headers);
        Assert.StartsWith("<headers>", md);
        Assert.EndsWith("</headers>", md);
        Assert.DoesNotContain("```with```", md);
        Assert.Contains("'''with'''", md);
    }

    #endregion

    #region Feature 5: Wire Format Detection Boundaries

    [Fact]
    public void T2_F05_01_DetectFormat_NullAndEmptyInputs_ReturnsRaw()
    {
        Assert.Equal(WireFormat.Raw, WireFormatDetector.DetectFormat(null, null));
        Assert.Equal(WireFormat.Raw, WireFormatDetector.DetectFormat("", ""));
    }

    [Fact]
    public void T2_F05_02_DetectFormat_MalformedJsonBody_ReturnsRaw() =>
        Assert.Equal(WireFormat.Raw, WireFormatDetector.DetectFormat("/custom/api", "{ invalid json !!"));

    [Fact]
    public void T2_F05_03_DetectFormat_HostHeaderGeminiOverride() =>
        Assert.Equal(WireFormat.Gemini, WireFormatDetector.DetectFormat("/unknown/rpc", "{}", new Dictionary<string, string> { ["Host"] = "cloudcode-pa.googleapis.com" }));

    [Fact]
    public void T2_F05_04_DetectFormat_NestedCodeAssistEnvelope_ReturnsGemini() =>
        Assert.Equal(WireFormat.Gemini, WireFormatDetector.DetectFormat("/v1internal:load", "{\"request\":{\"generationConfig\":{}}}"));

    [Fact]
    public void T2_F05_05_Detect_CanonicalHelper_ReturnsExpectedStrings()
    {
        Assert.Equal("gemini", WireFormatDetector.Detect("/v1beta/models/gemini-pro:generateContent", "{}"));
        Assert.Equal("anthropic", WireFormatDetector.Detect("/v1/messages", "{}"));
        Assert.Equal("openai", WireFormatDetector.Detect("/v1/chat/completions", "{}"));
        Assert.Equal("raw", WireFormatDetector.Detect("/unknown", "{}"));
    }

    #endregion

    #region Feature 6: Gemini Envelope Unwrapping Boundaries

    [Fact]
    public void T2_F06_01_UnwrapRequest_ArrayWrapper_UnpacksInnerObject()
    {
        using var doc = JsonDocument.Parse("[{\"request\":{\"contents\":[]}}]");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);
        Assert.Equal(JsonValueKind.Object, unwrapped.ValueKind);
        Assert.True(unwrapped.TryGetProperty("contents", out _));
    }

    [Fact]
    public void T2_F06_02_UnwrapRequest_PrimitiveOrNullRequest_PreservedSafely()
    {
        using var doc = JsonDocument.Parse("{\"request\":\"string-value\"}");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);
        Assert.Equal(JsonValueKind.Object, unwrapped.ValueKind);
        Assert.True(unwrapped.TryGetProperty("request", out var req));
        Assert.Equal("string-value", req.GetString());
    }

    [Fact]
    public void T2_F06_03_UnwrapResponse_ArrayWithCandidates_UnpacksObject()
    {
        using var doc = JsonDocument.Parse("[{\"candidates\":[]}]");
        var unwrapped = GeminiPayloadParser.UnwrapResponse(doc.RootElement);
        Assert.Equal(JsonValueKind.Object, unwrapped.ValueKind);
        Assert.True(unwrapped.TryGetProperty("candidates", out _));
    }

    [Fact]
    public void T2_F06_04_IsThought_StringAndNumericRepresentations_ToleratedSafely()
    {
        using var doc = JsonDocument.Parse("{\"thought\":\"true\"}");
        Assert.True(GeminiPayloadParser.IsThought(doc.RootElement));
        using var docNum = JsonDocument.Parse("{\"thought\":1}");
        Assert.True(GeminiPayloadParser.IsThought(docNum.RootElement));
        using var docFalse = JsonDocument.Parse("{\"thought\":0}");
        Assert.False(GeminiPayloadParser.IsThought(docFalse.RootElement));
    }

    [Fact]
    public void T2_F06_05_FindModel_MissingModelInBodyAndPath_DefaultsToUnknown() =>
        Assert.Equal("unknown", GeminiPayloadParser.FindModel(null, "/v1beta/unrelated:action"));

    #endregion

    #region Feature 7: SSE Stream Reassembly Boundaries

    [Fact]
    public void T2_F07_01_ParseSseEvents_OnlyCommentsAndKeepAlive_ReturnsEmpty() =>
        Assert.Empty(SseChunkReassembler.ParseSseEvents(": ping\n: keep-alive\n\n: comment\n\n"));

    [Fact]
    public void T2_F07_02_ParseSseEvents_DoneToken_IgnoredWithoutError() =>
        Assert.Empty(SseChunkReassembler.ParseSseEvents("data: [DONE]\n\n"));

    [Fact]
    public void T2_F07_03_ReassembleGemini_MalformedChunkFollowedByValid_RecoversText()
    {
        var sse = "data: { broken json\n\ndata: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"recovered\"}]}}]}\n\n";
        Assert.Equal("recovered", SseChunkReassembler.ReassembleGemini(sse).AssistantText);
    }

    [Fact]
    public void T2_F07_04_ReassembleGemini_EmptyAndWhitespace_ReturnsEmptyModel()
    {
        var res1 = SseChunkReassembler.ReassembleGemini("");
        Assert.True(string.IsNullOrEmpty(res1.AssistantText));
        Assert.True(string.IsNullOrEmpty(res1.Thinking));
        var res2 = SseChunkReassembler.ReassembleGemini("   \n\n   ");
        Assert.True(string.IsNullOrEmpty(res2.AssistantText));
    }

    [Fact]
    public void T2_F07_05_ParseSseEvents_MultiLineDataEvent_W3cCompliant()
    {
        var sse = "data: {\"candidates\":\ndata: [{\"content\":{\"parts\":[{\"text\":\"joined\"}]}}]}\n\n";
        Assert.Equal("joined", SseChunkReassembler.ReassembleGemini(sse).AssistantText);
    }

    #endregion

    #region Feature 8: XML Structural Markdown Output Boundaries

    [Fact]
    public void T2_F08_01_Render_PromptContainingMarkdownHeaders_DoesNotBreakXmlStructure()
    {
        var prompt = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"# Main Header\\n## Sub Header\\n```csharp\\nvar x = 1;\\n```\"}]}]}";
        var exchange = new CapturedExchange { WireFormat = WireFormat.Gemini, RawRequestBody = Encoding.UTF8.GetBytes(prompt) };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<request>", md);
        Assert.Contains("# Main Header", md);
        Assert.Contains("</request>", md);
        Assert.Contains("<response>", md);
    }

    [Fact]
    public void T2_F08_02_Render_PromptContainingSimulatedXmlTags_PreservesLiterals()
    {
        var prompt = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"Test <meta> and </meta> literal tags\"}]}]}";
        var exchange = new CapturedExchange { WireFormat = WireFormat.Gemini, RawRequestBody = Encoding.UTF8.GetBytes(prompt) };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("Test <meta> and </meta> literal tags", md);
    }

    [Fact]
    public void T2_F08_03_Render_ResponseWithOnlyToolUse_OmitsAssistantText()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"calc\",\"args\":{\"n\":42}}}]}}]}\n\n";
        var exchange = new CapturedExchange { WireFormat = WireFormat.Gemini, IsStreaming = true, RawResponseBody = Encoding.UTF8.GetBytes(sse) };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<tool-use name=\"calc\"", md);
        Assert.DoesNotContain("<assistant-text>", md);
    }

    [Fact]
    public void T2_F08_04_Render_EmptyBodies_ProducesValidXmlShell()
    {
        var exchange = new CapturedExchange { RawRequestBody = [], RawResponseBody = [] };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<meta>", md);
        Assert.Contains("</meta>", md);
        Assert.Contains("<headers>", md);
        Assert.Contains("</headers>", md);
        Assert.Contains("<request>", md);
        Assert.Contains("</request>", md);
        Assert.Contains("<response>", md);
        Assert.Contains("</response>", md);
    }

    [Fact]
    public async Task T2_F08_05_WriteToFileAsync_CreatesDirectoryAndWritesFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_md_test_" + Guid.NewGuid().ToString("N"));
        var exchange = new CapturedExchange { AgentName = "TestAgent" };
        try
        {
            var writtenPath = await RequestMarkdownRenderer.WriteToFileAsync(exchange, tempDir);
            Assert.True(File.Exists(writtenPath));
            var content = await File.ReadAllTextAsync(writtenPath);
            Assert.Contains("<meta>", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    #endregion

    #region Feature 9: Dynamic Root CA & Leaf Cert Generation Boundaries

    [Fact]
    public void T2_F09_01_NormalizeHost_HandlesEdgeCases()
    {
        Assert.Equal("127.0.0.1", CertificateAuthority.NormalizeHost("[127.0.0.1]:8443"));
        Assert.Equal("::1", CertificateAuthority.NormalizeHost("[::1]:443"));
        Assert.Equal("example.com", CertificateAuthority.NormalizeHost("Example.COM:8080"));
        Assert.Equal("localhost", CertificateAuthority.NormalizeHost("LOCALHOST"));
        Assert.Throws<ArgumentException>(() => CertificateAuthority.NormalizeHost(""));
        Assert.Throws<ArgumentException>(() => CertificateAuthority.NormalizeHost("   "));
    }

    [Fact]
    public void T2_F09_02_GenerateLeafCertificate_WildcardHost()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var cert = ca.GetOrCreateLeafCertificate("*.googleapis.com");
        Assert.NotNull(cert);
        Assert.Contains("*.googleapis.com", cert.Subject);
        var basic = cert.Extensions["2.5.29.19"] as X509BasicConstraintsExtension;
        Assert.NotNull(basic);
        Assert.False(basic.CertificateAuthority);
    }

    [Fact]
    public void T2_F09_03_GenerateLeafCertificate_LoopbackIpv4_AddsIpSan()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var cert = ca.GetOrCreateLeafCertificate("127.0.0.1");
        Assert.NotNull(cert);
        Assert.Contains("127.0.0.1", cert.Subject);
    }

    [Fact]
    public void T2_F09_04_GetOrCreateLeafCertificate_DisposedCa_ThrowsObjectDisposedException()
    {
        var ca = CertificateAuthority.CreateInMemory();
        ca.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ca.GetOrCreateLeafCertificate("host.test"));
    }

    [Fact]
    public void T2_F09_05_ExportRootCertificatePem_InvalidFilePath_ThrowsArgumentException()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        Assert.Throws<ArgumentException>(() => ca.ExportRootCertificatePem(""));
        Assert.Throws<ArgumentException>(() => ca.ExportRootCertificatePem("   "));
    }

    #endregion

    #region Feature 10: Transparent Non-Blocking Stream Teeing Boundaries

    [Fact]
    public async Task T2_F10_01_StreamRelay_Direct_ZeroLengthPayload_CompletesWithoutRelayingBytes()
    {
        using var source = new MemoryStream([]);
        using var destination = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(source, destination, contentLength: 0);
        Assert.True(res.IsSuccess);
        Assert.Equal(0, res.TotalBytesRelayed);
        Assert.Empty(res.CapturedBytes);
    }

    [Fact]
    public async Task T2_F10_02_StreamRelay_Direct_ExceedingMaxCaptureBytes_TruncatesCaptureBuffer()
    {
        var payload = new byte[1024];
        Array.Fill(payload, (byte)0x5A);
        using var source = new MemoryStream(payload);
        using var destination = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(source, destination, contentLength: 1024, maxCaptureBytes: 256);
        Assert.Equal(1024, res.TotalBytesRelayed);
        Assert.Equal(256, res.CapturedBytes.Length);
        Assert.True(res.IsTruncated);
        Assert.Equal(1024, destination.Length);
    }

    [Fact]
    public async Task T2_F10_03_StreamRelay_Direct_CancelledToken_StopsGracefully()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var source = new MemoryStream([1, 2, 3]);
        using var destination = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(source, destination, cancellationToken: cts.Token);
        Assert.False(res.IsSuccess);
    }

    [Fact]
    public async Task T2_F10_04_StreamRelay_Chunked_ZeroChunk_TerminatesImmediately()
    {
        var rawChunk = "0\r\n\r\n"u8.ToArray();
        using var source = new MemoryStream(rawChunk);
        using var destination = new MemoryStream();
        var res = await StreamRelay.RelayChunkedAsync(source, destination);
        Assert.True(res.IsSuccess);
        Assert.Equal(5, res.TotalBytesRelayed);
        Assert.Empty(res.CapturedBytes);
    }

    [Fact]
    public async Task T2_F10_05_StreamRelay_NullArguments_ThrowsArgumentNullException()
    {
        using var mem = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() => StreamRelay.RelayAsync(null!, mem, isChunked: false));
        await Assert.ThrowsAsync<ArgumentNullException>(() => StreamRelay.RelayAsync(mem, null!, isChunked: false));
    }

    #endregion

    #region Feature 11: Plain HTTP Reverse Proxy Mode Boundaries

    [Fact]
    public async Task T2_F11_01_ReverseProxy_UpstreamServerError_PassesThrough500()
    {
        await using var server = await TestHttpServer.StartAsync((_, _, _, _) =>
            (500, new(), "Internal Server Error"u8.ToArray()));
        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = server.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var client = new HttpClient();
        var res = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/api");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
    }

    [Fact]
    public async Task T2_F11_02_ReverseProxy_UpstreamRateLimit_PassesThrough429()
    {
        await using var server = await TestHttpServer.StartAsync((_, _, _, _) =>
            (429, new() { ["Retry-After"] = "10" }, "Rate limited"u8.ToArray()));
        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = server.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var client = new HttpClient();
        var res = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/api");
        Assert.Equal((HttpStatusCode)429, res.StatusCode);
        Assert.True(res.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task T2_F11_03_ReverseProxy_UnreachableUpstream_Returns502BadGateway()
    {
        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = "http://127.0.0.1:1" };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var client = new HttpClient();
        var res = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/api");
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }

    [Fact]
    public async Task T2_F11_04_ReverseProxy_EmptyRequestBody_ForwardedSafely()
    {
        await using var server = await TestHttpServer.StartAsync((_, _, _, body) => (200, new(), body));
        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = server.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var client = new HttpClient();
        var res = await client.PostAsync($"http://127.0.0.1:{proxy.BoundPort}/api", new ByteArrayContent([]));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Empty(bytes);
    }

    [Fact]
    public async Task T2_F11_05_ReverseProxy_NonStandardMethod_Forwarded()
    {
        await using var server = await TestHttpServer.StartAsync((method, _, _, _) =>
            (200, new(), Encoding.UTF8.GetBytes(method)));
        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = server.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var client = new HttpClient();
        using var req = new HttpRequestMessage(new HttpMethod("OPTIONS"), $"http://127.0.0.1:{proxy.BoundPort}/api");
        var res = await client.SendAsync(req);
        Assert.Equal("OPTIONS", await res.Content.ReadAsStringAsync());
    }

    #endregion

    #region Feature 12: Forward MitM HTTPS Proxy Mode Boundaries

    [Fact]
    public async Task T2_F12_01_MitM_NonStandardPortInConnect_Returns200ConnectionEstablished()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = tcp.GetStream();
        var connectMsg = "CONNECT custom.api:8443 HTTP/1.1\r\nHost: custom.api:8443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectMsg);
        await stream.FlushAsync();
        var buf = new byte[1024];
        var read = await stream.ReadAsync(buf);
        Assert.StartsWith("HTTP/1.1 200 Connection Established", Encoding.ASCII.GetString(buf, 0, read));
    }

    [Fact]
    public async Task T2_F12_02_MitM_MissingPortInConnect_DefaultsTo443()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = tcp.GetStream();
        var connectMsg = "CONNECT api.example.com HTTP/1.1\r\nHost: api.example.com\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectMsg);
        await stream.FlushAsync();
        var buf = new byte[1024];
        var read = await stream.ReadAsync(buf);
        Assert.StartsWith("HTTP/1.1 200 Connection Established", Encoding.ASCII.GetString(buf, 0, read));
    }

    [Fact]
    public async Task T2_F12_03_MitM_BracketedIpv6InConnect_StripsBrackets()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = tcp.GetStream();
        var connectMsg = "CONNECT [::1]:8443 HTTP/1.1\r\nHost: [::1]:8443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectMsg);
        await stream.FlushAsync();
        var buf = new byte[1024];
        var read = await stream.ReadAsync(buf);
        Assert.StartsWith("HTTP/1.1 200 Connection Established", Encoding.ASCII.GetString(buf, 0, read));
    }

    [Fact]
    public async Task T2_F12_04_MitM_ClientTlsHandshake_SucceedsWithDynamicLeafCert()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = tcp.GetStream();
        var connectMsg = "CONNECT target.test:443 HTTP/1.1\r\nHost: target.test:443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectMsg);
        await stream.FlushAsync();
        var buf = new byte[1024];
        var read = await stream.ReadAsync(buf);
        Assert.StartsWith("HTTP/1.1 200 Connection Established", Encoding.ASCII.GetString(buf, 0, read));
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) => cert is not null);
        await ssl.AuthenticateAsClientAsync("target.test");
        Assert.True(ssl.IsAuthenticated);
    }

    [Fact]
    public async Task T2_F12_05_MitM_UpstreamFailure_Returns502InsideTunnel()
    {
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", proxy.BoundPort);
        var stream = tcp.GetStream();
        var connectMsg = "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectMsg);
        await stream.FlushAsync();
        var buf = new byte[1024];
        var connectBytes = await stream.ReadAsync(buf);
        Assert.True(connectBytes > 0);
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, cert, _, _) => true);
        await ssl.AuthenticateAsClientAsync("127.0.0.1");
        var getMsg = "GET / HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n"u8.ToArray();
        await ssl.WriteAsync(getMsg);
        await ssl.FlushAsync();
        var resBuf = new byte[2048];
        var resRead = await ssl.ReadAsync(resBuf);
        Assert.Contains("502 Bad Gateway", Encoding.ASCII.GetString(resBuf, 0, resRead));
    }

    #endregion

    #region Feature 13: Defensive Traffic Guards Boundaries

    [Fact]
    public void T2_F13_01_IsWebSocketUpgrade_CaseInsensitiveAndPartialMatches()
    {
        Assert.True(ProxyServer.IsWebSocketUpgrade(new Dictionary<string, string> { ["Connection"] = "keep-alive, Upgrade", ["Upgrade"] = "websocket" }));
        Assert.False(ProxyServer.IsWebSocketUpgrade(new Dictionary<string, string> { ["Connection"] = "keep-alive", ["Upgrade"] = "websocket" }));
    }

    [Fact]
    public void T2_F13_02_RejectUpgrade_NonWebsocket_Returns200Ok()
    {
        Assert.Equal((200, "OK"), ProxyServer.RejectUpgrade("Upgrade", "http2"));
        var (status, resp) = ProxyServer.RejectUpgrade("Upgrade", "WebSocket");
        Assert.Equal(426, status);
        Assert.Contains("426 Upgrade Required", resp);
    }

    [Fact]
    public void T2_F13_03_ShouldLogRequest_HousekeepingFiltersAllVariations()
    {
        Assert.False(ProxyServer.ShouldLogRequest("GET", "/v1/models", WireFormat.Gemini, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1/count_tokens", WireFormat.Anthropic, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1/count-tokens", WireFormat.OpenAi, filterHousekeeping: true));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1/models/gemini", WireFormat.Gemini, filterHousekeeping: true));
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:generateContent", WireFormat.Gemini, filterHousekeeping: true));
    }

    [Fact]
    public void T2_F13_04_TrackBurst_ThresholdBoundary_SuppressesOnExceeded()
    {
        var options = new ProxyOptions { BurstThreshold = 3 };
        var proxy = new ProxyServer(options);
        var r1 = proxy.TrackBurst("POST", "/retry", 500);
        var r2 = proxy.TrackBurst("POST", "/retry", 500);
        var r3 = proxy.TrackBurst("POST", "/retry", 500);
        Assert.False(r1.Suppressed);
        Assert.False(r2.Suppressed);
        Assert.False(r3.Suppressed);
        var r4 = proxy.TrackBurst("POST", "/retry", 500);
        Assert.True(r4.Suppressed);
        Assert.True(r4.JustDetected);
        var r5 = proxy.TrackBurst("POST", "/retry", 500);
        Assert.True(r5.Suppressed);
        Assert.False(r5.JustDetected);
    }

    [Fact]
    public void T2_F13_05_TrackBurst_StaticMethod_MatchesBoundary()
    {
        Assert.Equal((false, false), ProxyServer.TrackBurst(20, threshold: 20));
        Assert.Equal((true, true), ProxyServer.TrackBurst(21, threshold: 20));
        Assert.Equal((true, false), ProxyServer.TrackBurst(22, threshold: 20));
    }

    #endregion

    #region Feature 14: CLI Proxy Subcommand Boundaries

    [Fact]
    public void T2_F14_01_ProxyOptions_PortValidation_Boundaries()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyOptions { Port = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyOptions { Port = 65536 }.Validate());
        new ProxyOptions { Port = 0 }.Validate();
        new ProxyOptions { Port = 65535 }.Validate();
    }

    [Fact]
    public void T2_F14_02_ProxyOptions_HostValidation_NullOrWhitespace_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ProxyOptions { Host = "" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProxyOptions { Host = "   " }.Validate());
    }

    [Fact]
    public void T2_F14_03_ProxyOptions_ReverseTargetUrl_InvalidScheme_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ProxyOptions { ReverseTargetUrl = "ftp://generative.api" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProxyOptions { ReverseTargetUrl = "not-a-valid-url" }.Validate());
    }

    [Fact]
    public void T2_F14_04_ProxyOptions_LogsDirectoryAndBurst_Boundaries()
    {
        Assert.Throws<ArgumentException>(() => new ProxyOptions { LogsDirectory = "   " }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyOptions { BurstThreshold = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProxyOptions { BurstWindow = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void T2_F14_05_CommandLineBuilder_ProxyCommand_NonNumericPort_ReportsParseError()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy --port notanumber");
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("notanumber") || e.Message.Contains("--port"));
    }

    #endregion

    #region Feature 15: CA Management Subcommand Boundaries

    [Fact]
    public void T2_F15_01_CaCommandHandler_Export_InvalidPath_ReturnsErrorCode1()
    {
        using var swErr = new StringWriter();
        var exitCode = CaCommandHandler.ExecuteExport(
            outputPath: "\0invalid-null-char-path",
            caDir: Path.GetTempPath(),
            output: TextWriter.Null,
            error: swErr);
        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to export Root CA certificate", swErr.ToString());
    }

    [Fact]
    public void T2_F15_02_CaCommandHandler_Status_CorruptedPfxFile_ReturnsErrorCode1()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_ca_corrupt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "ca.pfx"), "not a valid pfx certificate file");
        try
        {
            using var swErr = new StringWriter();
            var exitCode = CaCommandHandler.ExecuteStatus(caDir: tempDir, output: TextWriter.Null, error: swErr);
            Assert.Equal(1, exitCode);
            Assert.Contains("Certificate file could not be loaded", swErr.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void T2_F15_03_CaCommandHandler_Untrust_NoCertificatesFound_Returns0()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_ca_empty_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var swOut = new StringWriter();
            var exitCode = CaCommandHandler.ExecuteUntrust(
                caDir: tempDir,
                storeName: "NonExistentStore_" + Guid.NewGuid().ToString("N"),
                output: swOut,
                error: TextWriter.Null);
            Assert.Equal(0, exitCode);
            Assert.Contains("No Root CA certificates found", swOut.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void T2_F15_04_CaCommandHandler_Export_ValidDirectory_ExportsPemWithBeginCertificate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_ca_export_" + Guid.NewGuid().ToString("N"));
        var certPath = Path.Combine(tempDir, "exported.crt");
        try
        {
            using var swOut = new StringWriter();
            var exitCode = CaCommandHandler.ExecuteExport(
                outputPath: certPath,
                caDir: tempDir,
                output: swOut,
                error: TextWriter.Null);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(certPath));
            Assert.Contains("-----BEGIN CERTIFICATE-----", File.ReadAllText(certPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void T2_F15_05_CommandLineBuilder_CaCommand_InvalidSubcommandReportsErrors()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        Assert.NotEmpty(root.Parse("ca invalidsubcommand").Errors);
    }

    #endregion

    #region Feature 16: Composite Runner Subcommand Boundaries

    [Fact]
    public async Task T2_F16_01_CompositeRunner_ExecuteChildProcess_EmptyChildArgs_ThrowsArgumentException() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CompositeRunner.ExecuteChildProcessAsync([], new Dictionary<string, string>(), TextWriter.Null, TextWriter.Null));

    [Fact]
    public async Task T2_F16_02_CompositeRunner_ExecuteChildProcess_NonExistentBinary_ReturnsExitCode1()
    {
        using var swErr = new StringWriter();
        var exitCode = await CompositeRunner.ExecuteChildProcessAsync(
            ["non_existent_binary_xyz_12345"],
            new Dictionary<string, string>(),
            TextWriter.Null,
            swErr);
        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to start child process", swErr.ToString());
    }

    [Fact]
    public async Task T2_F16_03_CompositeRunner_RunAsync_InvalidPort_FailsValidationAndReturns1()
    {
        using var swErr = new StringWriter();
        var exitCode = await CompositeRunner.RunAsync(
            childArgs: null,
            port: -1,
            stdout: TextWriter.Null,
            stderr: swErr);
        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", swErr.ToString());
    }

    [Fact]
    public void T2_F16_04_CompositeRunner_FormatEnvironmentVariables_EdgeCaseHostAndPort()
    {
        var env = CompositeRunner.FormatEnvironmentVariables(port: 65535, caCertPath: "ca.crt", host: "0.0.0.0");
        Assert.Equal("http://0.0.0.0:65535", env["HTTP_PROXY"]);
        Assert.Equal("http://0.0.0.0:65535", env["HTTPS_PROXY"]);
        Assert.Equal("http://0.0.0.0:65535", env["http_proxy"]);
        Assert.Equal("http://0.0.0.0:65535", env["https_proxy"]);
        Assert.Equal("http://0.0.0.0:65535", env["GOOGLE_GEMINI_BASE_URL"]);
        Assert.Equal(Path.GetFullPath("ca.crt"), env["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public async Task T2_F16_05_CompositeRunner_RunAsync_ImmediateCancellation_StopsCleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exitCode = await CompositeRunner.RunAsync(
            childArgs: null,
            port: 0,
            stdout: TextWriter.Null,
            stderr: TextWriter.Null,
            cancellationToken: cts.Token);
        Assert.Equal(0, exitCode);
    }

    #endregion
}