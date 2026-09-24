namespace AgyLogger.Cli.Tests.E2E.Harness;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

/// <summary>
/// E2E test harness that delegates directly to production implementations from AgyLogger.Cli.
/// </summary>
public static class E2EProxyHarness
{
    public static HashSet<string> SensitiveHeaders => SensitiveDataRedactor.SensitiveHeaders;

    #region Feature 3: Payload Decompression

    public static (byte[] DecodedBytes, string? Warning) Decompress(byte[]? rawBytes, string? contentEncoding)
    {
        return PayloadDecompressor.Decompress(rawBytes, contentEncoding);
    }

    #endregion

    #region Feature 4: Sensitive Header Redaction

    public static Dictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>>? headers)
    {
        return SensitiveDataRedactor.RedactHeaders(headers);
    }

    public static Dictionary<string, string> RedactHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        return SensitiveDataRedactor.RedactHeaders(headers);
    }

    public static string RedactUrl(string url)
    {
        return SensitiveDataRedactor.RedactUrl(url);
    }

    #endregion

    #region Feature 5: Wire Format Detection

    public static string DetectWireFormat(string? path, string? body, IReadOnlyDictionary<string, string>? headers = null)
    {
        return WireFormatDetector.DetectFormat(path, body, headers).ToWireTag();
    }

    public static string FindModel(string? path, string? bodyJson)
    {
        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(bodyJson);
                return GeminiPayloadParser.FindModel(doc.RootElement, path);
            }
            catch { }
        }
        return GeminiPayloadParser.FindModel(null, path);
    }

    #endregion

    #region Feature 6: Gemini Envelope Unwrapping

    public static JsonElement UnwrapGeminiRequest(JsonElement root)
    {
        return GeminiPayloadParser.UnwrapRequest(root);
    }

    public static JsonElement UnwrapGeminiResponse(JsonElement root)
    {
        return GeminiPayloadParser.UnwrapResponse(root);
    }

    #endregion

    #region Feature 7: SSE Stream Reassembly

    public static (string Thinking, string AssistantText, List<(string Name, string Args)> ToolCalls, string? FinishReason, string? Usage)
        ReassembleGeminiSse(string sseStream)
    {
        var parsed = SseChunkReassembler.ReassembleGemini(sseStream);
        var tools = parsed.ToolCalls.Select(t => (t.Name, t.ArgumentsJson)).ToList();
        return (parsed.Thinking ?? string.Empty, parsed.AssistantText ?? string.Empty, tools, parsed.FinishReason, parsed.UsageJson);
    }

    #endregion

    #region Feature 8: XML Structural Markdown Output

    public static string RenderMarkdown(CapturedExchange exchange)
    {
        return RequestMarkdownRenderer.Render(exchange);
    }

    #endregion

    #region Feature 9: Dynamic Root CA & Leaf Cert Generation

    public static (X509Certificate2 Cert, byte[] Pkcs12) GenerateDynamicCertificate(string hostName)
    {
        using var ca = CertificateAuthority.CreateInMemory();
        var cert = ca.GetOrCreateLeafCertificate(hostName);
        return (cert, cert.Export(X509ContentType.Pfx));
    }

    #endregion

    #region Feature 13: Defensive Guards

    public static bool ShouldLogRequest(string method, string path, string renderer)
    {
        return ProxyServer.ShouldLogRequest(method, path, renderer);
    }

    public static (bool Suppressed, bool JustDetected) TrackBurst(int requestCountInWindow)
    {
        return ProxyServer.TrackBurst(requestCountInWindow);
    }

    public static (int StatusCode, string Response) RejectUpgrade(string? connection, string? upgrade)
    {
        return ProxyServer.RejectUpgrade(connection, upgrade);
    }

    #endregion
}

/// <summary>
/// E2E test cluster pairing a TestHttpServer upstream with an in-process ProxyServer
/// bound to ephemeral port 0 with automatic cleanup.
/// </summary>
public sealed class E2ETestCluster : IAsyncDisposable
{
    public TestHttpServer Upstream { get; }
    public ProxyServer Proxy { get; }
    public string LogsDirectory { get; }
    public int ProxyPort => Proxy.BoundPort;
    public int UpstreamPort => Upstream.Port;

    public E2ETestCluster(TestHttpServer upstream, ProxyServer proxy, string logsDirectory)
    {
        Upstream = upstream;
        Proxy = proxy;
        LogsDirectory = logsDirectory;
    }

    public static async Task<E2ETestCluster> CreateAsync(
        Func<string, string, Dictionary<string, string>, byte[], (int, Dictionary<string, string>, byte[])>? upstreamHandler = null,
        Action<ProxyOptions>? configureProxy = null)
    {
        var tempLogs = Path.Combine(Path.GetTempPath(), "AgyE2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempLogs);

        var upstream = await TestHttpServer.StartAsync(upstreamHandler);
        var options = new ProxyOptions
        {
            Port = 0,
            Host = "127.0.0.1",
            ReverseTargetUrl = upstream.BaseUrl,
            LogsDirectory = tempLogs,
            FilterHousekeepingRequests = false
        };
        configureProxy?.Invoke(options);

        var proxy = new ProxyServer(options);
        await proxy.StartAsync();

        return new E2ETestCluster(upstream, proxy, tempLogs);
    }

    public async ValueTask DisposeAsync()
    {
        await Proxy.DisposeAsync();
        await Upstream.DisposeAsync();
        if (Directory.Exists(LogsDirectory))
        {
            try
            {
                Directory.Delete(LogsDirectory, true);
            }
            catch { }
        }
    }
}