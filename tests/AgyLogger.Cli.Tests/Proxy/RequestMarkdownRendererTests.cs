namespace AgyLogger.Cli.Tests.Proxy;

using System.Text;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;
using AgyLogger.Cli.Tests.E2E.Harness;

using Xunit;

public sealed class RequestMarkdownRendererTests
{
    private static readonly string FixturesDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Fixtures");

    private static string LoadFixture(string fileName)
    {
        var directPath = Path.Combine(FixturesDirectory, fileName);
        if (File.Exists(directPath)) return File.ReadAllText(directPath);

        var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", fileName);
        if (File.Exists(fallbackPath)) return File.ReadAllText(fallbackPath);

        var found = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        if (found is not null) return File.ReadAllText(found);

        throw new FileNotFoundException($"Could not locate fixture file '{fileName}'.");
    }

    [Fact]
    public void Render_GoldenGeminiDirect_MatchesFixtureExact()
    {
        var requestJson = LoadFixture("gemini_direct_request.json");
        var responseSse = LoadFixture("gemini_direct_stream_response.txt");
        var expectedGolden = LoadFixture("golden_gemini_direct.md");

        var rawHeaders = new Dictionary<string, string>
        {
            ["host"] = "generativelanguage.googleapis.com",
            ["accept"] = "text/event-stream",
            ["content-type"] = "application/json",
            ["authorization"] = "Bearer AIzaSySecretApiKey",
            ["x-goog-api-key"] = "AIzaSySecretApiKey"
        };

        var exchange = new CapturedExchange
        {
            RequestTimestamp = DateTimeOffset.Parse("2026-09-20T19:00:00.000Z"),
            AgentName = "Antigravity CLI",
            WireFormat = WireFormat.Gemini,
            ModelName = "gemini-2.5-pro",
            Method = "POST",
            Url = "/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse",
            StatusCode = 200,
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders),
            RawRequestBody = Encoding.UTF8.GetBytes(requestJson),
            DecodedRequestBody = requestJson,
            RawResponseBody = Encoding.UTF8.GetBytes(responseSse),
            DecodedResponseBody = responseSse
        };

        var actualMarkdown = RequestMarkdownRenderer.Render(exchange);

        GoldenSnapshotAssert.Matches(expectedGolden, actualMarkdown);
    }

    [Fact]
    public void Render_GoldenGeminiOAuth_MatchesFixtureExact()
    {
        var requestJson = LoadFixture("gemini_oauth_request.json");
        var responseSse = LoadFixture("gemini_oauth_stream_response.txt");
        var expectedGolden = LoadFixture("golden_gemini_oauth.md");

        var rawHeaders = new Dictionary<string, string>
        {
            ["host"] = "cloudcode-pa.googleapis.com",
            ["accept"] = "text/event-stream",
            ["content-type"] = "application/json",
            ["authorization"] = "Bearer ya29.a0AfH6SMOAuthToken",
            ["x-goog-api-key"] = "ya29.a0AfH6SMOAuthToken"
        };

        var exchange = new CapturedExchange
        {
            RequestTimestamp = DateTimeOffset.Parse("2026-09-20T19:00:00.000Z"),
            AgentName = "Antigravity CLI",
            WireFormat = WireFormat.Gemini,
            ModelName = "gemini-2.5-pro",
            Method = "POST",
            Url = "/v1internal:streamGenerateContent",
            StatusCode = 200,
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders),
            RawRequestBody = Encoding.UTF8.GetBytes(requestJson),
            DecodedRequestBody = requestJson,
            RawResponseBody = Encoding.UTF8.GetBytes(responseSse),
            DecodedResponseBody = responseSse
        };

        var actualMarkdown = RequestMarkdownRenderer.Render(exchange);

        GoldenSnapshotAssert.Matches(expectedGolden, actualMarkdown);
    }

    [Fact]
    public void Render_XmlStructuralTags_AreBalancedAndUnambiguous()
    {
        var exchange = new CapturedExchange
        {
            WireFormat = WireFormat.Raw,
            Method = "GET",
            Url = "/ping",
            StatusCode = 200,
            DecodedRequestBody = "ping",
            DecodedResponseBody = "pong"
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("<meta>", md);
        Assert.Contains("</meta>", md);
        Assert.Contains("<headers>", md);
        Assert.Contains("</headers>", md);
        Assert.Contains("<request>", md);
        Assert.Contains("</request>", md);
        Assert.Contains("<response>", md);
        Assert.Contains("</response>", md);

        var metaStart = md.IndexOf("<meta>", StringComparison.Ordinal);
        var headersStart = md.IndexOf("<headers>", StringComparison.Ordinal);
        var requestStart = md.IndexOf("<request>", StringComparison.Ordinal);
        var responseStart = md.IndexOf("<response>", StringComparison.Ordinal);

        Assert.True(metaStart < headersStart);
        Assert.True(headersStart < requestStart);
        Assert.True(requestStart < responseStart);
    }

    [Fact]
    public void Render_HeadersBlock_ContainsRedactedValues()
    {
        var rawHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer super-secret",
            ["Content-Type"] = "application/json"
        };

        var exchange = new CapturedExchange
        {
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders)
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("authorization: [REDACTED]", md);
        Assert.Contains("content-type: application/json", md);
        Assert.DoesNotContain("super-secret", md);
    }

    [Fact]
    public void Render_DecompressionFailure_EmbedsWarningBanner()
    {
        var warning = "[request-logger] COULD NOT DECODE THIS BODY \u2014 unsupported encoding: zstd";
        var rawBytes = Encoding.UTF8.GetBytes("compressed-payload");

        var exchange = new CapturedExchange
        {
            RawRequestBody = rawBytes,
            RequestDecodingWarning = warning
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains(warning, md);
        Assert.Contains("The bytes below are still compressed. They are NOT what was actually sent as text:", md);
        Assert.Contains("compressed-payload", md);
    }

    [Fact]
    public void Render_RawWireFormat_PrettyPrintsJson()
    {
        var json = "{\"hello\":\"world\",\"count\":42}";
        var exchange = new CapturedExchange
        {
            WireFormat = WireFormat.Raw,
            DecodedRequestBody = json,
            DecodedResponseBody = json
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("```json", md);
        Assert.Contains("\"hello\": \"world\"", md);
    }

    [Fact]
    public void Render_UnparseableBody_FencesRawText()
    {
        var plain = "PLAIN TEXT BODY NOT JSON";
        var exchange = new CapturedExchange
        {
            WireFormat = WireFormat.Raw,
            DecodedRequestBody = plain,
            DecodedResponseBody = plain
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("```\nPLAIN TEXT BODY NOT JSON\n```", md);
    }

    [Fact]
    public void Render_Non200Status_ReflectedInMeta()
    {
        var exchange = new CapturedExchange
        {
            StatusCode = 426
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("- **upstream status**: 426", md);
    }

    [Fact]
    public async Task WriteToFileAsync_CreatesFileWithTimestampedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AgyLogger_Tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exchange = new CapturedExchange
            {
                RequestTimestamp = new DateTimeOffset(2026, 9, 20, 19, 0, 0, 0, TimeSpan.Zero),
                AgentName = "Test-Agent"
            };

            var path = await RequestMarkdownRenderer.WriteToFileAsync(exchange, tempDir);

            Assert.True(File.Exists(path));
            Assert.Contains("2026-09-20T19-00-00-000_test-agent.md", path);

            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("<meta>", content);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteToFileAsync_WithCompanionFiles_WritesRequestAndResponseTxt()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AgyLogger_Tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var reqBytes = Encoding.UTF8.GetBytes("raw-request-data");
            var respBytes = Encoding.UTF8.GetBytes("raw-response-data");

            var exchange = new CapturedExchange
            {
                RawRequestBody = reqBytes,
                RawResponseBody = respBytes
            };

            var path = await RequestMarkdownRenderer.WriteToFileAsync(exchange, tempDir, saveRawCompanionFiles: true);

            var baseName = Path.GetFileNameWithoutExtension(path);
            var reqTxtPath = Path.Combine(tempDir, $"{baseName}.request.txt");
            var respTxtPath = Path.Combine(tempDir, $"{baseName}.response.txt");

            Assert.True(File.Exists(reqTxtPath));
            Assert.True(File.Exists(respTxtPath));
            Assert.Equal(reqBytes, await File.ReadAllBytesAsync(reqTxtPath));
            Assert.Equal(respBytes, await File.ReadAllBytesAsync(respTxtPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task WriteToFileAsync_SanitizesAgentNameInFilename()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "AgyLogger_Tests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var exchange = new CapturedExchange
            {
                AgentName = "Custom / Dirty * Agent? Name!"
            };

            var path = await RequestMarkdownRenderer.WriteToFileAsync(exchange, tempDir);

            Assert.True(File.Exists(path));
            Assert.EndsWith(".md", path);
            Assert.DoesNotContain("/", Path.GetFileName(path));
            Assert.DoesNotContain("*", Path.GetFileName(path));
            Assert.DoesNotContain("?", Path.GetFileName(path));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Render_HeadersWithBackticks_EscapedCleanly()
    {
        var rawHeaders = new Dictionary<string, string>
        {
            ["X-Custom```"] = "value```injection"
        };

        var exchange = new CapturedExchange
        {
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders)
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        // Ensures backticks are replaced with triple single quotes to prevent breaking ``` markdown fence
        Assert.Contains("'''", md);
        Assert.DoesNotContain("```injection", md);
    }
}