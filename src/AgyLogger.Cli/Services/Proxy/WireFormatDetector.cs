namespace AgyLogger.Cli.Services.Proxy;

using System.Text.Json;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Classifies intercepted HTTP traffic into standard LLM wire formats
/// (Gemini, Anthropic, OpenAi, Raw).
/// </summary>
public static class WireFormatDetector
{
    /// <summary>
    /// Exact reflection target for E2EProxyHarness: takes path and body string, returns canonical lowercase tag.
    /// Named 'Detect' uniquely to prevent AmbiguousMatchException when invoked via reflection.
    /// </summary>
    public static string Detect(string? path, string? body)
    {
        return DetectFormat(path, body, headers: null, method: "POST").ToWireTag();
    }

    /// <summary>
    /// Strongly-typed detection evaluating method, path, headers, and request body.
    /// </summary>
    public static WireFormat DetectFormat(
        string? path,
        string? body,
        IReadOnlyDictionary<string, string>? headers = null,
        string? method = null)
    {
        var cleanPath = path ?? string.Empty;
        var cleanBody = body ?? string.Empty;

        // 1. Path & Header Fast Path for Gemini
        if (cleanPath.Contains("generateContent", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Contains("generativelanguage", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Contains("/v1internal", StringComparison.OrdinalIgnoreCase))
        {
            return WireFormat.Gemini;
        }

        if (headers is not null)
        {
            if (headers.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host))
            {
                if (host.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                    host.Contains("cloudcode-pa.googleapis.com", StringComparison.OrdinalIgnoreCase))
                {
                    return WireFormat.Gemini;
                }
                if (host.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase))
                {
                    return WireFormat.Anthropic;
                }
                if (host.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase))
                {
                    return WireFormat.OpenAi;
                }
            }

            if (headers.ContainsKey("x-goog-api-key"))
            {
                return WireFormat.Gemini;
            }
            if (headers.ContainsKey("anthropic-version"))
            {
                return WireFormat.Anthropic;
            }
        }

        // 2. Path Fast Path for Anthropic and OpenAI
        if (cleanPath.Contains("/v1/messages", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Contains("/messages", StringComparison.OrdinalIgnoreCase))
        {
            return WireFormat.Anthropic;
        }

        if (cleanPath.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            cleanPath.Contains("/responses", StringComparison.OrdinalIgnoreCase))
        {
            return WireFormat.OpenAi;
        }

        // 3. Deep Body Inspection
        if (!string.IsNullOrWhiteSpace(cleanBody) && cleanBody.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(cleanBody);
                var root = doc.RootElement;

                // Check for Gemini (direct or Code Assist OAuth envelope)
                if (root.TryGetProperty("contents", out var contentsProp) && contentsProp.ValueKind == JsonValueKind.Array)
                {
                    return WireFormat.Gemini;
                }
                if (root.TryGetProperty("systemInstruction", out _) ||
                    root.TryGetProperty("generationConfig", out _))
                {
                    return WireFormat.Gemini;
                }

                if (root.TryGetProperty("request", out var innerReq) && innerReq.ValueKind == JsonValueKind.Object)
                {
                    if (innerReq.TryGetProperty("contents", out _) ||
                        innerReq.TryGetProperty("systemInstruction", out _) ||
                        innerReq.TryGetProperty("generationConfig", out _))
                    {
                        return WireFormat.Gemini;
                    }
                }

                // Check for Anthropic
                if (root.TryGetProperty("messages", out var anthropicMsgs) &&
                    anthropicMsgs.ValueKind == JsonValueKind.Array &&
                    (root.TryGetProperty("max_tokens", out _) ||
                     root.TryGetProperty("anthropic_version", out _) ||
                     root.TryGetProperty("system", out _)))
                {
                    return WireFormat.Anthropic;
                }

                // Check for OpenAI
                if (root.TryGetProperty("input", out _) && root.TryGetProperty("instructions", out _))
                {
                    return WireFormat.OpenAi;
                }

                if (root.TryGetProperty("model", out _) && root.TryGetProperty("messages", out var oaiMsgs) && oaiMsgs.ValueKind == JsonValueKind.Array)
                {
                    return WireFormat.OpenAi;
                }
            }
            catch
            {
                // Unparseable JSON falls back to Raw
            }
        }

        return WireFormat.Raw;
    }
}