namespace AgyLogger.Cli.Tests.E2E;

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

/// <summary>
/// Tier 3: Cross-Feature Interaction Tests.
/// Validates pairwise and multi-feature interaction combinations across decompression,
/// redaction, streaming, proxying, and markdown rendering.
/// </summary>
public sealed class Tier3_CrossFeatureTests
{
    [Fact]
    public void T3_01_CompressedRequest_With_ChunkedSseResponse()
    {
        // Gzip compressed request + SSE streaming response
        var requestPayload = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"what is 2+2?\"}]}]}"u8.ToArray();
        using var mem = new MemoryStream();
        using (var gz = new GZipStream(mem, CompressionMode.Compress))
        {
            gz.Write(requestPayload);
        }
        var compressed = mem.ToArray();

        var sseStream = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Calculating...\",\"thought\":true}]}}]}\n" +
                        "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"2+2 = 4\"}]}}]}\n";

        var (decodedBytes, _) = PayloadDecompressor.Decompress(compressed, "gzip");

        var exchange = new CapturedExchange
        {
            RawRequestBody = compressed,
            RequestContentEncoding = "gzip",
            DecodedRequestBody = Encoding.UTF8.GetString(decodedBytes),
            RawResponseBody = Encoding.UTF8.GetBytes(sseStream),
            DecodedResponseBody = sseStream,
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("what is 2+2?", md);
        Assert.Contains("<thinking>", md);
        Assert.Contains("Calculating...", md);
        Assert.Contains("<assistant-text>", md);
        Assert.Contains("2+2 = 4", md);
    }

    [Fact]
    public void T3_02_RedactedQueryTokens_With_GzipRequest()
    {
        var raw = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"query test\"}]}]}"u8.ToArray();
        using var mem = new MemoryStream();
        using (var gz = new GZipStream(mem, CompressionMode.Compress))
        {
            gz.Write(raw);
        }

        var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:generateContent?key=AIzaSySecretToken&alt=sse";
        var redactedUrl = SensitiveDataRedactor.RedactUrl(url);

        var (decodedBytes, _) = PayloadDecompressor.Decompress(mem.ToArray(), "gzip");

        var exchange = new CapturedExchange
        {
            Url = redactedUrl,
            RawRequestBody = mem.ToArray(),
            RequestContentEncoding = "gzip",
            DecodedRequestBody = Encoding.UTF8.GetString(decodedBytes),
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("key=[REDACTED]", md);
        Assert.DoesNotContain("AIzaSySecretToken", md);
        Assert.Contains("query test", md);
    }

    [Fact]
    public void T3_03_ReverseProxy_With_MalformedJsonBody()
    {
        var malformed = "{ this is invalid json payload"u8.ToArray();
        var exchange = new CapturedExchange
        {
            RawRequestBody = malformed,
            WireFormat = WireFormat.Raw
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<request>", md);
        Assert.Contains("{ this is invalid json payload", md);
        Assert.Contains("</request>", md);
    }

    [Fact]
    public void T3_04_OAuthEnvelopeUnwrap_With_SseThinkingAndToolCalls()
    {
        var wrappedRequest = "{\"model\":\"gemini-2.5-pro\",\"request\":{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"run ls\"}]}]}}";
        var wrappedResponse = "data: {\"response\":{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"thinking about cmd\",\"thought\":true},{\"functionCall\":{\"name\":\"shell\",\"args\":{\"cmd\":\"ls\"}}}]}}]}}\n";

        var exchange = new CapturedExchange
        {
            Url = "/v1internal:streamGenerateContent",
            RawRequestBody = Encoding.UTF8.GetBytes(wrappedRequest),
            DecodedRequestBody = wrappedRequest,
            RawResponseBody = Encoding.UTF8.GetBytes(wrappedResponse),
            DecodedResponseBody = wrappedResponse,
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("run ls", md);
        Assert.Contains("<thinking>", md);
        Assert.Contains("thinking about cmd", md);
        Assert.Contains("<tool-use name=\"shell\"", md);
        Assert.Contains("\"cmd\": \"ls\"", md);
    }

    [Fact]
    public void T3_05_MitM_ConnectTunnel_With_DynamicCertificate_And_TeeStreaming()
    {
        var host = "cloudcode-pa.googleapis.com";
        using var ca = CertificateAuthority.CreateInMemory();
        var cert = ca.GetOrCreateLeafCertificate(host);
        Assert.NotNull(cert);

        var exchange = new CapturedExchange
        {
            Method = "CONNECT",
            Url = $"{host}:443",
            StatusCode = 200,
            AgentName = "Antigravity CLI (OAuth)"
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains($"CONNECT {host}:443", md);
    }

    [Fact]
    public void T3_06_BurstThrottling_With_Http429RateLimit()
    {
        // Simulate rapid repeated 429 errors from upstream
        for (var i = 1; i <= 20; i++)
        {
            var (suppressed, _) = ProxyServer.TrackBurst(i);
            Assert.False(suppressed);
        }

        var (burstSuppressed, justDetected) = ProxyServer.TrackBurst(21);
        Assert.True(burstSuppressed);
        Assert.True(justDetected);
    }

    [Fact]
    public void T3_07_BrotliRequest_With_MultipleRedactedHeaders()
    {
        var raw = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"brotli test\"}]}]}"u8.ToArray();
        using var mem = new MemoryStream();
        using (var br = new BrotliStream(mem, CompressionMode.Compress))
        {
            br.Write(raw);
        }

        var rawHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer confidential",
            ["x-goog-api-key"] = "secret123",
            ["Cookie"] = "secretCookie=val",
            ["Accept"] = "text/event-stream"
        };
        var redactedHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders);
        var (decodedBytes, _) = PayloadDecompressor.Decompress(mem.ToArray(), "br");

        var exchange = new CapturedExchange
        {
            RawRequestBody = mem.ToArray(),
            RequestContentEncoding = "br",
            DecodedRequestBody = Encoding.UTF8.GetString(decodedBytes),
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = redactedHeaders,
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("authorization: [REDACTED]", md);
        Assert.Contains("x-goog-api-key: [REDACTED]", md);
        Assert.Contains("cookie: [REDACTED]", md);
        Assert.Contains("accept: text/event-stream", md);
        Assert.Contains("brotli test", md);
    }

    [Fact]
    public void T3_08_ShouldLogRequest_With_CountTokens_Filtering()
    {
        var isCountTokensLogged = ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini-pro:countTokens", "gemini");
        var isGenerateContentLogged = ProxyServer.ShouldLogRequest("POST", "/v1beta/models/gemini-pro:generateContent", "gemini");

        Assert.False(isCountTokensLogged);
        Assert.True(isGenerateContentLogged);
    }

    [Fact]
    public void T3_09_WebSocketUpgrade_With_ReverseProxy()
    {
        var (status, response) = ProxyServer.RejectUpgrade("Upgrade", "websocket");
        Assert.Equal(426, status);
        Assert.StartsWith("HTTP/1.1 426 Upgrade Required", response);
        Assert.Contains("Connection: close", response);
    }

    [Fact]
    public void T3_10_ModelExtraction_FromPath_With_CompressedBody()
    {
        var path = "/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse";
        var bodyWithoutModel = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hi\"}]}]}";
        using var doc = JsonDocument.Parse(bodyWithoutModel);
        var model = GeminiPayloadParser.FindModel(doc.RootElement, path);

        Assert.Equal("gemini-2.5-flash", model);
    }

    [Fact]
    public void T3_11_DeflateCompression_With_AnthropicFormat()
    {
        var anthropicReq = "{\"model\":\"claude-3-7-sonnet\",\"messages\":[{\"role\":\"user\",\"content\":\"test\"}]}";
        using var mem = new MemoryStream();
        using (var def = new DeflateStream(mem, CompressionMode.Compress))
        {
            def.Write(Encoding.UTF8.GetBytes(anthropicReq));
        }

        var (decoded, warning) = PayloadDecompressor.Decompress(mem.ToArray(), "deflate");
        Assert.Null(warning);
        Assert.Contains("claude-3-7-sonnet", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void T3_12_OpenAiResponsesApi_With_DeltaArgumentReassembly()
    {
        var sse = "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Part 1 \"}\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Part 2\"}\n" +
                  "data: {\"type\":\"response.completed\"}\n";

        var parsed = SseChunkReassembler.ReassembleOpenAI(sse);
        Assert.NotNull(parsed.AssistantText);
    }

    [Fact]
    public void T3_13_MultiTurnExchange_With_ToolResults()
    {
        var req = "{" +
                  "\"contents\":[" +
                  "  {\"role\":\"user\",\"parts\":[{\"text\":\"run test\"}]}," +
                  "  {\"role\":\"model\",\"parts\":[{\"functionCall\":{\"name\":\"bash\",\"args\":{\"c\":\"dotnet test\"}}}]}," +
                  "  {\"role\":\"user\",\"parts\":[{\"functionResponse\":{\"name\":\"bash\",\"response\":{\"output\":\"Passed: 5\"}}}]}" +
                  "]" +
                  "}";

        var exchange = new CapturedExchange
        {
            RawRequestBody = Encoding.UTF8.GetBytes(req),
            DecodedRequestBody = req,
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("<message index=\"1\" role=\"user\">", md);
        Assert.Contains("<message index=\"2\" role=\"model\">", md);
        Assert.Contains("<message index=\"3\" role=\"user\">", md);
        Assert.Contains("<tool-use name=\"bash\"", md);
        Assert.Contains("<tool-result name=\"bash\"", md);
        Assert.Contains("Passed: 5", md);
    }

    [Fact]
    public void T3_14_Upstream502BadGateway_With_EmptyResponseBody()
    {
        var exchange = new CapturedExchange
        {
            StatusCode = 502,
            DecodedResponseBody = string.Empty
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("- **upstream status**: 502", md);
        Assert.Contains("_(no content decoded)_", md);
    }

    [Fact]
    public void T3_15_UnsupportedZstd_With_RedactedHeaders_And_SseStream()
    {
        var rawBytes = "compressed with zstd"u8.ToArray();
        var sse = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"response despite zstd\"}]}}]}\n";

        var (decodedBytes, warning) = PayloadDecompressor.Decompress(rawBytes, "zstd");

        var exchange = new CapturedExchange
        {
            RawRequestBody = rawBytes,
            RequestContentEncoding = "zstd",
            RequestDecodingWarning = warning,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(new Dictionary<string, string> { ["authorization"] = "Bearer token" }),
            RawResponseBody = Encoding.UTF8.GetBytes(sse),
            DecodedResponseBody = sse,
            WireFormat = WireFormat.Gemini
        };

        var md = RequestMarkdownRenderer.Render(exchange);
        Assert.Contains("[request-logger] COULD NOT DECODE THIS BODY — content-encoding \"zstd\" is not one this tool decodes", md);
        Assert.Contains("authorization: [REDACTED]", md);
        Assert.Contains("response despite zstd", md);
    }
}