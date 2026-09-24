namespace AgyLogger.Cli.Tests.E2E;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;
using AgyLogger.Cli.Tests.E2E.Harness;

using Xunit;

/// <summary>
/// Tier 4: Real-World Application Scenarios.
/// Executes authentic end-to-end multi-turn agy agent workloads and verifies rendered
/// output against golden baseline snapshot fixtures.
/// </summary>
public sealed class Tier4_RealWorldScenariosTests
{
    private static readonly string FixturesDirectory = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Fixtures");

    private static string LoadFixture(string fileName)
    {
        // Check relative to current base directory or project structure
        var directPath = Path.Combine(FixturesDirectory, fileName);
        if (File.Exists(directPath)) return File.ReadAllText(directPath);

        var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", fileName);
        if (File.Exists(fallbackPath)) return File.ReadAllText(fallbackPath);

        // Search recursively if running in shadow copy
        var found = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
        if (found is not null) return File.ReadAllText(found);

        throw new FileNotFoundException($"Could not locate fixture file '{fileName}'.");
    }

    [Fact]
    public void T4_01_GeminiApiKeyRoute_DirectStreamingWithThinkingAndTools()
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

        // Assert Golden Snapshot Byte-for-Byte match after deterministic scrubbing
        GoldenSnapshotAssert.Matches(expectedGolden, actualMarkdown);
    }

    [Fact]
    public void T4_02_GoogleAccountOAuthRoute_CodeAssistWrappedPayloads()
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

        // Assert Golden Snapshot Byte-for-Byte match after deterministic scrubbing
        GoldenSnapshotAssert.Matches(expectedGolden, actualMarkdown);
    }

    [Fact]
    public void T4_03_AnthropicClaudeCode_StreamingSession()
    {
        var requestJson = LoadFixture("anthropic_request.json");
        var responseSse = LoadFixture("anthropic_stream_response.txt");

        var rawHeaders = new Dictionary<string, string>
        {
            ["x-api-key"] = "sk-ant-api-key-secret",
            ["content-type"] = "application/json"
        };

        var exchange = new CapturedExchange
        {
            AgentName = "Claude Code",
            WireFormat = WireFormat.Anthropic,
            ModelName = "claude-3-7-sonnet-20250219",
            Method = "POST",
            Url = "/v1/messages",
            StatusCode = 200,
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders),
            RawRequestBody = Encoding.UTF8.GetBytes(requestJson),
            DecodedRequestBody = requestJson,
            RawResponseBody = Encoding.UTF8.GetBytes(responseSse),
            DecodedResponseBody = responseSse
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("<meta>", md);
        Assert.Contains("- **agent**: Claude Code", md);
        Assert.Contains("- **model**: claude-3-7-sonnet-20250219", md);
        Assert.Contains("x-api-key: [REDACTED]", md);
        Assert.Contains("check repository status", md);
    }

    [Fact]
    public void T4_04_OpenAiCodex_StreamingSession()
    {
        var requestJson = LoadFixture("openai_request.json");
        var responseSse = LoadFixture("openai_stream_response.txt");

        var rawHeaders = new Dictionary<string, string>
        {
            ["authorization"] = "Bearer sk-openai-secret",
            ["content-type"] = "application/json"
        };

        var exchange = new CapturedExchange
        {
            AgentName = "Codex",
            WireFormat = WireFormat.OpenAi,
            ModelName = "gpt-4o",
            Method = "POST",
            Url = "/v1/responses",
            StatusCode = 200,
            RawRequestHeaders = rawHeaders,
            RedactedRequestHeaders = SensitiveDataRedactor.RedactHeaders(rawHeaders),
            RawRequestBody = Encoding.UTF8.GetBytes(requestJson),
            DecodedRequestBody = requestJson,
            RawResponseBody = Encoding.UTF8.GetBytes(responseSse),
            DecodedResponseBody = responseSse
        };

        var md = RequestMarkdownRenderer.Render(exchange);

        Assert.Contains("<meta>", md);
        Assert.Contains("- **agent**: Codex", md);
        Assert.Contains("- **model**: gpt-4o", md);
        Assert.Contains("authorization: [REDACTED]", md);
        Assert.Contains("shell", md);
    }

    [Fact]
    public async Task T4_05_EndToEnd_RealNetworkProxyRoundtrip_WithDiskLogging()
    {
        var responsePayload = "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Echo response from upstream\"}]}}]}\n\n";

        await using var cluster = await E2ETestCluster.CreateAsync((_, _, _, _) =>
            (200, new() { ["Content-Type"] = "text/event-stream" }, Encoding.UTF8.GetBytes(responsePayload)));

        using var client = new HttpClient();
        var requestBody = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"ping upstream\"}]}]}";
        var proxyUrl = $"http://127.0.0.1:{cluster.ProxyPort}/v1beta/models/gemini-2.5-pro:streamGenerateContent";
        var response = await client.PostAsync(proxyUrl, new StringContent(requestBody, Encoding.UTF8, "application/json"));
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(responsePayload, responseText);
        Assert.Single(cluster.Upstream.ReceivedRequests);

        // Verify log markdown file was generated on disk in cluster.LogsDirectory
        var logFiles = Directory.GetFiles(cluster.LogsDirectory, "*.md");
        Assert.NotEmpty(logFiles);
        var logContent = await File.ReadAllTextAsync(logFiles[0]);
        Assert.Contains("ping upstream", logContent);
        Assert.Contains("Echo response from upstream", logContent);
        Assert.Contains("<assistant-text>", logContent);
    }
}