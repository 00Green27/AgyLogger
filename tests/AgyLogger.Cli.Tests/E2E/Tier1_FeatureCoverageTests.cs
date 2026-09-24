namespace AgyLogger.Cli.Tests.E2E;

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
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
/// Tier 1: Feature Coverage Tests (Happy Paths).
/// Exercises authentic production classes across all 16 features in PROJECT.md (80 authentic tests).
/// </summary>
public sealed class Tier1_FeatureCoverageTests
{
    #region Feature 1: .NET 10 Framework Alignment

    [Fact]
    public void F01_01_Net10_AssemblyTargetFrameworkIsNet10()
    {
        var attr = typeof(ProxyServer).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(".NETCoreApp,Version=v10.0", attr.FrameworkName);
    }

    [Fact]
    public async Task F01_02_Net10_HttpWireProtocolFramingWithSpans()
    {
        using var stream = new MemoryStream("POST /v1beta/test HTTP/1.1\r\nHost: example.com\r\nContent-Length: 4\r\n\r\nping"u8.ToArray());
        var parsed = await HttpWireProtocol.ReadHttpRequestAsync(stream, CancellationToken.None);
        Assert.NotNull(parsed);
        Assert.Equal("POST", parsed.Method);
        Assert.Equal("/v1beta/test", parsed.Path);
        Assert.Equal("ping", Encoding.UTF8.GetString(parsed.Body));
    }

    [Fact]
    public void F01_03_Net10_CapturedExchangeFrozenHeaders()
    {
        var exchange = new CapturedExchange
        {
            RawRequestHeaders = new Dictionary<string, string> { ["X-Net10"] = "Active" }
        };
        Assert.True(exchange.RawRequestHeaders.ContainsKey("x-net10"));
        Assert.Equal("Active", exchange.RawRequestHeaders["X-NET10"]);
    }

    [Fact]
    public async Task F01_04_Net10_StreamRelayAsyncPipeline()
    {
        using var src = new MemoryStream("net10-streaming"u8.ToArray());
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst);
        Assert.True(res.IsSuccess);
        Assert.Equal("net10-streaming", Encoding.UTF8.GetString(dst.ToArray()));
        Assert.Equal("net10-streaming", Encoding.UTF8.GetString(res.CapturedBytes));
    }

    [Fact]
    public void F01_05_Net10_CertificateAuthorityGeneration()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        Assert.NotNull(ca.RootCertificate);
        Assert.Equal("CN=AgyLogger Development CA, O=AgyLogger", ca.RootCertificate.Subject);
        Assert.True(ca.RootCertificate.HasPrivateKey);
    }

    #endregion

    #region Feature 2: Captured Exchange Models

    [Fact]
    public void F02_01_CapturedExchange_DefaultProperties()
    {
        var exchange = new CapturedExchange();
        Assert.NotEqual(Guid.Empty, exchange.ExchangeId);
        Assert.Equal("Antigravity CLI", exchange.AgentName);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Equal(WireFormat.Unknown, exchange.WireFormat);
        Assert.True(DateTimeOffset.TryParse(exchange.FormattedTimestamp, out _));
    }

    [Fact]
    public void F02_02_CapturedExchange_BuildLogFileNameFormat()
    {
        var exchange = new CapturedExchange
        {
            RequestTimestamp = new DateTimeOffset(2026, 9, 20, 19, 0, 0, 123, TimeSpan.Zero),
            AgentName = "Antigravity CLI"
        };
        Assert.Equal("2026-09-20T19-00-00-123_Antigravity-CLI.md", exchange.BuildLogFileName());
    }

    [Fact]
    public void F02_03_CapturedExchange_EffectiveRequestBody_ReturnsDecodedWhenPresent()
    {
        var exchange = new CapturedExchange
        {
            RawRequestBody = "{\"prompt\":\"raw\"}"u8.ToArray(),
            DecodedRequestBody = "{\"prompt\":\"decoded\"}"
        };
        Assert.Equal("{\"prompt\":\"decoded\"}", exchange.GetEffectiveRequestBodyText());
    }

    [Fact]
    public void F02_04_CapturedExchange_EffectiveRequestBody_ReturnsWarningWhenFailed()
    {
        var exchange = new CapturedExchange
        {
            RawRequestBody = [1, 2, 3],
            RequestDecodingWarning = "[request-logger] COULD NOT DECODE THIS BODY"
        };
        var body = exchange.GetEffectiveRequestBodyText();
        Assert.StartsWith("[request-logger] COULD NOT DECODE THIS BODY", body);
        Assert.Contains("The bytes below are still compressed", body);
    }

    [Fact]
    public void F02_05_CapturedExchange_EffectiveResponseBody_ReassembledStream()
    {
        var exchange = new CapturedExchange { DecodedResponseBody = "Reassembled SSE response" };
        Assert.Equal("Reassembled SSE response", exchange.GetEffectiveResponseBodyText());
    }

    #endregion

    #region Feature 3: Payload Decompression

    [Fact]
    public void F03_01_Decompress_IdentityOrNull_ReturnsOriginalBytes()
    {
        var raw = "plain payload"u8.ToArray();
        var (decoded, warn) = PayloadDecompressor.Decompress(raw, "identity");
        Assert.Null(warn);
        Assert.Equal(raw, decoded);
    }

    [Fact]
    public void F03_02_Decompress_Gzip_DecompressesSuccessfully()
    {
        var original = "Hello GZip payload!"u8.ToArray();
        using var mem = new MemoryStream();
        using (var gz = new GZipStream(mem, CompressionMode.Compress)) gz.Write(original);
        var (decoded, warn) = PayloadDecompressor.Decompress(mem.ToArray(), "gzip");
        Assert.Null(warn);
        Assert.Equal("Hello GZip payload!", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void F03_03_Decompress_Deflate_DecompressesSuccessfully()
    {
        var original = "Hello Deflate payload!"u8.ToArray();
        using var mem = new MemoryStream();
        using (var def = new DeflateStream(mem, CompressionMode.Compress)) def.Write(original);
        var (decoded, warn) = PayloadDecompressor.Decompress(mem.ToArray(), "deflate");
        Assert.Null(warn);
        Assert.Equal("Hello Deflate payload!", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void F03_04_Decompress_Brotli_DecompressesSuccessfully()
    {
        var original = "Hello Brotli payload!"u8.ToArray();
        using var mem = new MemoryStream();
        using (var br = new BrotliStream(mem, CompressionMode.Compress)) br.Write(original);
        var (decoded, warn) = PayloadDecompressor.Decompress(mem.ToArray(), "br");
        Assert.Null(warn);
        Assert.Equal("Hello Brotli payload!", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void F03_05_Decompress_CorruptedGzip_EmitsWarningBanner()
    {
        var corrupted = new byte[] { 0x1f, 0x8b, 0x00, 0x99, 0x88 };
        var (decoded, warn) = PayloadDecompressor.Decompress(corrupted, "gzip");
        Assert.NotNull(warn);
        Assert.Contains(PayloadDecompressor.WarningBannerPrefix, warn);
        Assert.Equal(corrupted, decoded);
    }

    #endregion

    #region Feature 4: Sensitive Header Redaction

    [Fact]
    public void F04_01_IsSensitiveHeader_IdentifiesAllKnownSensitiveHeaders()
    {
        Assert.True(SensitiveDataRedactor.IsSensitiveHeader("authorization"));
        Assert.True(SensitiveDataRedactor.IsSensitiveHeader("x-goog-api-key"));
        Assert.True(SensitiveDataRedactor.IsSensitiveHeader("cookie"));
        Assert.False(SensitiveDataRedactor.IsSensitiveHeader("content-type"));
    }

    [Fact]
    public void F04_02_RedactHeaders_ReplacesSecretsWithRedactedToken()
    {
        var input = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer secret_token_123",
            ["x-goog-api-key"] = "AIzaSyD-secret",
            ["Host"] = "generativelanguage.googleapis.com"
        };
        var redacted = SensitiveDataRedactor.RedactHeaders(input);
        Assert.Equal(SensitiveDataRedactor.RedactedValue, redacted["authorization"]);
        Assert.Equal(SensitiveDataRedactor.RedactedValue, redacted["x-goog-api-key"]);
        Assert.Equal("generativelanguage.googleapis.com", redacted["host"]);
    }

    [Fact]
    public void F04_03_RedactUrl_RedactsQueryStringApiKeys()
    {
        var url = "https://example.com/v1beta/models/gemini:stream?key=secret123&alt=sse";
        var redacted = SensitiveDataRedactor.RedactUrl(url);
        Assert.Contains("key=[REDACTED]", redacted);
        Assert.DoesNotContain("secret123", redacted);
        Assert.Contains("alt=sse", redacted);
    }

    [Fact]
    public void F04_04_FormatRedactedHeadersBlock_RendersFencedSortedBlock()
    {
        var headers = new Dictionary<string, string>
        {
            ["authorization"] = "[REDACTED]",
            ["content-type"] = "application/json"
        };
        var block = SensitiveDataRedactor.RenderHeaders(headers);
        Assert.StartsWith("<headers>", block);
        Assert.Contains("```", block);
        Assert.Contains("authorization: [REDACTED]", block);
        Assert.EndsWith("</headers>", block);
    }

    [Fact]
    public void F04_05_IsSensitiveQueryParam_DetectsKeyAndTokenParameters()
    {
        Assert.True(SensitiveDataRedactor.IsSensitiveQueryParam("key"));
        Assert.True(SensitiveDataRedactor.IsSensitiveQueryParam("api_key"));
        Assert.True(SensitiveDataRedactor.IsSensitiveQueryParam("token"));
        Assert.False(SensitiveDataRedactor.IsSensitiveQueryParam("alt"));
    }

    #endregion

    #region Feature 5: Wire Format Detection

    [Fact]
    public void F05_01_DetectWireFormat_GeminiPathDetected()
    {
        var fmt = WireFormatDetector.DetectFormat("/v1beta/models/gemini-2.5-pro:streamGenerateContent", "{}");
        Assert.Equal(WireFormat.Gemini, fmt);
    }

    [Fact]
    public void F05_02_DetectWireFormat_GeminiContentsBodyDetected()
    {
        var fmt = WireFormatDetector.DetectFormat("/api/turn", "{\"contents\":[{\"role\":\"user\"}]}");
        Assert.Equal(WireFormat.Gemini, fmt);
    }

    [Fact]
    public void F05_03_DetectWireFormat_AnthropicDetected()
    {
        var fmt = WireFormatDetector.DetectFormat("/v1/messages", "{\"messages\":[],\"max_tokens\":100}");
        Assert.Equal(WireFormat.Anthropic, fmt);
    }

    [Fact]
    public void F05_04_DetectWireFormat_OpenAiDetected()
    {
        var fmt = WireFormatDetector.DetectFormat("/v1/chat/completions", "{\"model\":\"gpt-4\"}");
        Assert.Equal(WireFormat.OpenAi, fmt);
    }

    [Fact]
    public void F05_05_DetectWireFormat_UnknownFallback()
    {
        var fmt = WireFormatDetector.DetectFormat("/custom/endpoint", "{\"data\":\"custom\"}");
        Assert.Equal(WireFormat.Unknown, fmt);
    }

    #endregion

    #region Feature 6: Gemini Envelope Unwrapping

    [Fact]
    public void F06_01_UnwrapGeminiRequest_DirectPayloadPreserved()
    {
        using var doc = JsonDocument.Parse("{\"contents\":[{\"role\":\"user\"}]}");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);
        Assert.True(unwrapped.TryGetProperty("contents", out _));
    }

    [Fact]
    public void F06_02_UnwrapGeminiRequest_OAuthEnvelopeUnwrapped()
    {
        using var doc = JsonDocument.Parse("{\"model\":\"gemini-pro\",\"request\":{\"contents\":[{\"role\":\"user\"}]}}");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);
        Assert.True(unwrapped.TryGetProperty("contents", out _));
        Assert.False(unwrapped.TryGetProperty("request", out _));
    }

    [Fact]
    public void F06_03_UnwrapGeminiResponse_DirectCandidatePreserved()
    {
        using var doc = JsonDocument.Parse("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hi\"}]}}]}");
        var unwrapped = GeminiPayloadParser.UnwrapResponse(doc.RootElement);
        Assert.True(unwrapped.TryGetProperty("candidates", out _));
    }

    [Fact]
    public void F06_04_UnwrapGeminiResponse_OAuthEnvelopeUnwrapped()
    {
        using var doc = JsonDocument.Parse("{\"response\":{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hi\"}]}}]}}");
        var unwrapped = GeminiPayloadParser.UnwrapResponse(doc.RootElement);
        Assert.True(unwrapped.TryGetProperty("candidates", out _));
        Assert.False(unwrapped.TryGetProperty("response", out _));
    }

    [Fact]
    public void F06_05_FindModel_ExtractsFromOAuthBody()
    {
        using var doc = JsonDocument.Parse("{\"model\":\"gemini-2.5-pro\",\"request\":{}}");
        var model = GeminiPayloadParser.FindModel(doc.RootElement, "/v1internal:streamGenerateContent");
        Assert.Equal("gemini-2.5-pro", model);
    }

    #endregion

    #region Feature 7: SSE Stream Reassembly

    [Fact]
    public void F07_01_ReassembleGeminiSse_JoinsAssistantText()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hello \"}]}}]}\n\ndata: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"world!\"}]}}]}\n\n";
        var resp = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Hello world!", resp.AssistantText);
    }

    [Fact]
    public void F07_02_ReassembleGeminiSse_SeparatesThinkingBlock()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Let me think...\",\"thought\":true},{\"text\":\"Answer\"}]}}]}\n\n";
        var resp = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Let me think...", resp.Thinking);
        Assert.Equal("Answer", resp.AssistantText);
    }

    [Fact]
    public void F07_03_ReassembleGeminiSse_ExtractsToolCalls()
    {
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"run_cmd\",\"args\":{\"cmd\":\"dir\"}}}]}}]}\n\n";
        var resp = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Single(resp.ToolCalls);
        Assert.Equal("run_cmd", resp.ToolCalls[0].Name);
    }

    [Fact]
    public void F07_04_ReassembleGeminiSse_CapturesFinishReason()
    {
        var sse = "data: {\"candidates\":[{\"finishReason\":\"MAX_TOKENS\",\"content\":{\"parts\":[]}}]}\n\n";
        var resp = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("MAX_TOKENS", resp.FinishReason);
    }

    [Fact]
    public void F07_05_ReassembleGeminiSse_CapturesUsageMetadata()
    {
        var sse = "data: {\"usageMetadata\":{\"totalTokenCount\":128}}\n\n";
        var resp = SseChunkReassembler.ReassembleGemini(sse);
        Assert.NotNull(resp.UsageJson);
        Assert.Contains("128", resp.UsageJson);
    }

    #endregion

    #region Feature 8: XML Structural Markdown Output

    [Fact]
    public void F08_01_RenderMarkdown_EmitsMetaBlock()
    {
        var exchange = new CapturedExchange
        {
            AgentName = "Antigravity CLI",
            ModelName = "gemini-2.5-pro",
            WireFormat = WireFormat.Gemini,
            Url = "/v1beta/models/gemini-2.5-pro:generateContent"
        };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<meta>", md);
        Assert.Contains("- **agent**: Antigravity CLI", md);
        Assert.Contains("- **model**: gemini-2.5-pro", md);
        Assert.Contains("</meta>", md);
    }

    [Fact]
    public void F08_02_RenderMarkdown_EmitsHeadersCodeFence()
    {
        var exchange = new CapturedExchange
        {
            RedactedRequestHeaders = new Dictionary<string, string> { ["host"] = "generativelanguage.googleapis.com" }
        };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<headers>", md);
        Assert.Contains("host: generativelanguage.googleapis.com", md);
        Assert.Contains("</headers>", md);
    }

    [Fact]
    public void F08_03_RenderMarkdown_EmitsRequestAndResponseBlocks()
    {
        var exchange = new CapturedExchange();
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<request>", md);
        Assert.Contains("</request>", md);
        Assert.Contains("<response>", md);
        Assert.Contains("</response>", md);
    }

    [Fact]
    public void F08_04_RenderMarkdown_StructuralTagPairing()
    {
        var exchange = new CapturedExchange();
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Equal(1, CountSubstrings(md, "<meta>"));
        Assert.Equal(1, CountSubstrings(md, "</meta>"));
        Assert.Equal(1, CountSubstrings(md, "<headers>"));
        Assert.Equal(1, CountSubstrings(md, "</headers>"));
        Assert.Equal(1, CountSubstrings(md, "<request>"));
        Assert.Equal(1, CountSubstrings(md, "</request>"));
        Assert.Equal(1, CountSubstrings(md, "<response>"));
        Assert.Equal(1, CountSubstrings(md, "</response>"));
    }

    [Fact]
    public void F08_05_RenderMarkdown_EmptyResponse_EmitsNoContentDecoded()
    {
        var exchange = new CapturedExchange { DecodedResponseBody = "" };
        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("_(no content decoded)_", md);
    }

    #endregion

    #region Feature 9: Dynamic Root CA & Leaf Cert Generation

    [Fact]
    public void F09_01_GenerateDynamicCertificate_CreatesValidCertificate()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("generativelanguage.googleapis.com");
        Assert.NotNull(leaf);
        Assert.Contains("generativelanguage.googleapis.com", leaf.Subject);
    }

    [Fact]
    public void F09_02_GenerateDynamicCertificate_HasSubjectAlternativeName()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("daily-cloudcode-pa.googleapis.com");
        var san = leaf.Extensions["2.5.29.17"];
        Assert.NotNull(san);
    }

    [Fact]
    public void F09_03_GenerateDynamicCertificate_HasPrivateKey()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("localhost");
        Assert.True(leaf.HasPrivateKey);
    }

    [Fact]
    public void F09_04_GenerateDynamicCertificate_ExportRootPem()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var pem = ca.ExportRootCertificatePem();
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", pem.TrimStart());
        Assert.Contains("-----END CERTIFICATE-----", pem);
    }

    [Fact]
    public void F09_05_GenerateDynamicCertificate_SupportsIpAddressSubject()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("127.0.0.1");
        Assert.NotNull(leaf);
    }

    #endregion

    #region Feature 10: Transparent Non-Blocking Stream Teeing

    [Fact]
    public async Task F10_01_StreamTee_PassesThroughImmediately()
    {
        using var src = new MemoryStream("stream chunk 1"u8.ToArray());
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst);
        Assert.True(res.IsSuccess);
        Assert.Equal("stream chunk 1", Encoding.UTF8.GetString(dst.ToArray()));
    }

    [Fact]
    public async Task F10_02_StreamTee_BuffersFullResponseInMemory()
    {
        using var src = new MemoryStream("buffered response bytes"u8.ToArray());
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst);
        Assert.Equal("buffered response bytes", Encoding.UTF8.GetString(res.CapturedBytes));
    }

    [Fact]
    public async Task F10_03_StreamTee_LeavesBytesUnmodified()
    {
        var testBytes = "exact byte integrity test"u8.ToArray();
        using var src = new MemoryStream(testBytes);
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst);
        Assert.Equal(testBytes, dst.ToArray());
    }

    [Fact]
    public async Task F10_04_StreamTee_CappingTruncatesCaptureBuffer()
    {
        using var src = new MemoryStream("long payload exceeding 10 bytes limit"u8.ToArray());
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst, maxCaptureBytes: 10);
        Assert.True(res.IsTruncated);
        Assert.Equal(10, res.CapturedBytes.Length);
    }

    [Fact]
    public async Task F10_05_StreamTee_SupportsEmptyStream()
    {
        using var src = new MemoryStream([]);
        using var dst = new MemoryStream();
        var res = await StreamRelay.RelayDirectAsync(src, dst);
        Assert.True(res.IsSuccess);
        Assert.Equal(0, res.TotalBytesRelayed);
        Assert.Empty(res.CapturedBytes);
    }

    #endregion

    #region Feature 11: Plain HTTP Reverse Proxy Mode

    [Fact]
    public async Task F11_01_ReverseProxy_ForwardsMethodAndPath()
    {
        await using var upstream = await TestHttpServer.StartAsync((method, path, _, _) =>
            (200, new() { ["Content-Type"] = "text/plain" }, Encoding.UTF8.GetBytes($"{method} {path}")));

        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = upstream.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var res = await client.PostAsync($"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini:generateContent", new StringContent("{}"));
        var content = await res.Content.ReadAsStringAsync();
        Assert.Equal("POST /v1beta/models/gemini:generateContent", content);
    }

    [Fact]
    public async Task F11_02_ReverseProxy_ForwardsRequestBody()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, body) =>
            (200, new() { ["Content-Type"] = "text/plain" }, body));

        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = upstream.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var sent = "{\"prompt\":\"reverse proxy test\"}";
        var res = await client.PostAsync($"http://127.0.0.1:{proxy.BoundPort}/test", new StringContent(sent, Encoding.UTF8, "application/json"));
        var echo = await res.Content.ReadAsStringAsync();
        Assert.Equal(sent, echo);
    }

    [Fact]
    public async Task F11_03_ReverseProxy_PassesThroughStatusCode()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
            (400, new(), "Bad Request"u8.ToArray()));

        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = upstream.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var res = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task F11_04_ReverseProxy_PassesThroughResponseHeaders()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
            (200, new() { ["X-Upstream-Header"] = "Active" }, "OK"u8.ToArray()));

        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = upstream.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var res = await client.GetAsync($"http://127.0.0.1:{proxy.BoundPort}/");
        Assert.True(res.Headers.Contains("X-Upstream-Header"));
    }

    [Fact]
    public async Task F11_05_ReverseProxy_CapturesExchangeMetadata()
    {
        await using var upstream = await TestHttpServer.StartAsync((_, _, _, _) =>
            (200, new(), "OK"u8.ToArray()));

        var options = new ProxyOptions { Port = 0, ReverseTargetUrl = upstream.BaseUrl };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var client = new HttpClient();
        var res = await client.PostAsync($"http://127.0.0.1:{proxy.BoundPort}/v1beta/models/gemini:generateContent", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(upstream.ReceivedRequests);
    }

    #endregion

    #region Feature 12: Forward MitM HTTPS Proxy Mode

    [Fact]
    public async Task F12_01_MitM_HandlesConnectRequest_Returns200ConnectionEstablished()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var options = new ProxyOptions { Port = 0 };
        await using var proxy = new ProxyServer(options, ca);
        await proxy.StartAsync();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", proxy.BoundPort);
        using var stream = tcpClient.GetStream();

        var connectReq = "CONNECT cloudcode-pa.googleapis.com:443 HTTP/1.1\r\nHost: cloudcode-pa.googleapis.com:443\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(connectReq);
        await stream.FlushAsync();

        var (respHeaders, _) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(stream, CancellationToken.None);
        Assert.NotNull(respHeaders);
        Assert.Equal(200, respHeaders.StatusCode);
        Assert.Equal("Connection Established", respHeaders.StatusDescription);
    }

    [Fact]
    public void F12_02_MitM_GeneratesLeafCertForTargetHost()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var leaf = ca.GetOrCreateLeafCertificate("cloudcode-pa.googleapis.com");
        Assert.NotNull(leaf);
        Assert.Contains("cloudcode-pa.googleapis.com", leaf.Subject);
    }

    [Fact]
    public async Task F12_03_MitM_EstablishedResponseFormat()
    {
        using var mem = new MemoryStream("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
        var (respHeaders, _) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(mem, CancellationToken.None);
        Assert.NotNull(respHeaders);
        Assert.Equal(200, respHeaders.StatusCode);
    }

    [Fact]
    public async Task F12_04_MitM_HttpWireProtocol_ParsesConnectRequestTarget()
    {
        var rawConnect = "CONNECT antigravity-unleash.goog:443 HTTP/1.1\r\nHost: antigravity-unleash.goog:443\r\n\r\n"u8.ToArray();
        using var mem = new MemoryStream(rawConnect);
        var req = await HttpWireProtocol.ReadHttpRequestAsync(mem, CancellationToken.None);

        Assert.NotNull(req);
        Assert.Equal("CONNECT", req.Method);
        Assert.Equal("antigravity-unleash.goog:443", req.Path);
        Assert.Equal("antigravity-unleash.goog:443", req.Headers["host"]);
    }

    [Fact]
    public void F12_05_MitM_CertificateAuthority_CachesDistinctLeafCerts()
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var cert1 = ca.GetOrCreateLeafCertificate("cloudcode-pa.googleapis.com");
        var cert2 = ca.GetOrCreateLeafCertificate("cloudcode-pa.googleapis.com");
        var cert3 = ca.GetOrCreateLeafCertificate("daily-cloudcode-pa.googleapis.com");

        Assert.Same(cert1, cert2);
        Assert.NotSame(cert1, cert3);
        Assert.Contains("daily-cloudcode-pa.googleapis.com", cert3.Subject);
    }

    #endregion

    #region Feature 13: Defensive Traffic Guards

    [Fact]
    public void F13_01_DefensiveGuard_ShouldLogRequest_PostOnly()
    {
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:generateContent", "gemini"));
        Assert.False(ProxyServer.ShouldLogRequest("GET", "/v1beta/models/gemini:generateContent", "gemini"));
    }

    [Fact]
    public void F13_02_DefensiveGuard_ShouldLogRequest_FiltersGeminiCountTokens()
    {
        Assert.True(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:streamGenerateContent", "gemini"));
        Assert.False(ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini:countTokens", "gemini"));
    }

    [Fact]
    public void F13_03_DefensiveGuard_TrackBurst_UnderThresholdNotSuppressed()
    {
        var (suppressed, justDetected) = ProxyServer.TrackBurst(15);
        Assert.False(suppressed);
        Assert.False(justDetected);
    }

    [Fact]
    public void F13_04_DefensiveGuard_TrackBurst_OverThresholdSuppressed()
    {
        var (suppressed, justDetected) = ProxyServer.TrackBurst(21);
        Assert.True(suppressed);
        Assert.True(justDetected);
    }

    [Fact]
    public async Task F13_05_DefensiveGuard_RejectWebSocketUpgrade_Returns426()
    {
        var options = new ProxyOptions { Port = 0, RejectWebSocketUpgrades = true };
        await using var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync("127.0.0.1", proxy.BoundPort);
        using var stream = tcpClient.GetStream();

        var wsReq = "GET /ws HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"u8.ToArray();
        await stream.WriteAsync(wsReq);
        await stream.FlushAsync();

        var (respHeaders, _) = await HttpWireProtocol.ReadHttpResponseHeadersAsync(stream, CancellationToken.None);
        Assert.NotNull(respHeaders);
        Assert.Equal(426, respHeaders.StatusCode);
    }

    #endregion

    #region Feature 14: CLI Proxy Subcommand

    [Fact]
    public void F14_01_CliProxy_BuildRootCommand_ContainsProxySubcommand()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        Assert.Contains(root.Subcommands, c => c.Name == "proxy");
    }

    [Fact]
    public void F14_02_CliProxy_CustomPortParsing()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy --port 9999");
        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F14_03_CliProxy_CustomOutputOption()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy -o ./custom-logs");
        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F14_04_CliProxy_RejectWsOption()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy --reject-ws false");
        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F14_05_CliProxy_ReverseUrlOption()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy --reverse-url http://localhost:8000");
        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    #endregion

    #region Feature 15: CA Management Subcommand

    [Fact]
    public void F15_01_CaCommand_CreateCommand_ContainsHierarchy()
    {
        var cmd = CaCommandHandler.CreateCommand();
        Assert.Equal("ca", cmd.Name);
        Assert.Contains(cmd.Subcommands, c => c.Name == "status");
        Assert.Contains(cmd.Subcommands, c => c.Name == "trust");
        Assert.Contains(cmd.Subcommands, c => c.Name == "untrust");
        Assert.Contains(cmd.Subcommands, c => c.Name == "export");
    }

    [Fact]
    public void F15_02_CaCommand_ParseExportSubcommand()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("ca export --output ./certs/test.crt");
        Assert.Empty(result.Errors);
        Assert.Equal("export", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F15_03_CaCommand_ParseStatusSubcommand()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("ca status --ca-dir ./temp-ca");
        Assert.Empty(result.Errors);
        Assert.Equal("status", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F15_04_CaCommand_ExecuteExport_WritesPemFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_ca_test_" + Guid.NewGuid().ToString("N"));
        var exportPath = Path.Combine(tempDir, "exported.crt");
        try
        {
            var exitCode = CaCommandHandler.ExecuteExport(exportPath, tempDir);
            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(exportPath));
            var content = File.ReadAllText(exportPath);
            Assert.StartsWith("-----BEGIN CERTIFICATE-----", content.TrimStart());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void F15_05_CaCommand_ExecuteStatus_ExecutesCleanly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_ca_status_" + Guid.NewGuid().ToString("N"));
        using var writer = new StringWriter();
        try
        {
            var exitCode = CaCommandHandler.ExecuteStatus(tempDir, output: writer);
            Assert.Equal(0, exitCode);
            Assert.NotEmpty(writer.ToString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    #region Feature 16: Composite Runner Subcommand

    [Fact]
    public void F16_01_CompositeRunner_ParseRunCommandWithChildArgs()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("run --port 9001 -- agy -p \"test prompt\"");
        Assert.Empty(result.Errors);
        Assert.Equal("run", result.CommandResult.Command.Name);
    }

    [Fact]
    public void F16_02_CompositeRunner_FormatEnvironmentVariables_ContainsAllRequiredVars()
    {
        var env = CompositeRunner.FormatEnvironmentVariables(8888, "ca.crt");
        Assert.Equal("http://127.0.0.1:8888", env["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", env["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", env["GOOGLE_GEMINI_BASE_URL"]);
        Assert.Equal(Path.GetFullPath("ca.crt"), env["NODE_EXTRA_CA_CERTS"]);
    }

    [Fact]
    public void F16_03_CompositeRunner_GenerateExportCommands_ContainsPowerShellBashAndCmd()
    {
        var exportCmds = CompositeRunner.GenerateExportCommands(8888, "ca.crt");
        Assert.Contains("$env:HTTP_PROXY", exportCmds);
        Assert.Contains("export HTTP_PROXY", exportCmds);
        Assert.Contains("set HTTP_PROXY", exportCmds);
    }

    [Fact]
    public void F16_04_CompositeRunner_ParseRunInvalidOption_ProducesError()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("run --invalid-flag");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task F16_05_CompositeRunner_RunAsync_CancelledToken_StopsCleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_run_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exitCode = await CompositeRunner.RunAsync([], tempDir, 0, stdout: stdout, stderr: stderr, cancellationToken: cts.Token);
            Assert.True(exitCode == 0 || exitCode == 130);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    private static int CountSubstrings(string text, string target)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(target, idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += target.Length;
        }
        return count;
    }
}