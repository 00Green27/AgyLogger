using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Represents a captured bidirectional HTTP exchange between an agent CLI and an upstream LLM API.
/// Immutable and thread-safe data model for logging and Markdown rendering.
/// </summary>
public sealed partial record class CapturedExchange
{
    private readonly byte[] _rawRequestBody = [];
    private readonly byte[] _rawResponseBody = [];
    private readonly IReadOnlyDictionary<string, string> _rawRequestHeaders = FrozenDictionary<string, string>.Empty;
    private readonly IReadOnlyDictionary<string, string> _redactedRequestHeaders = FrozenDictionary<string, string>.Empty;
    private readonly IReadOnlyDictionary<string, string> _rawResponseHeaders = FrozenDictionary<string, string>.Empty;
    private readonly IReadOnlyDictionary<string, string> _redactedResponseHeaders = FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// Unique identifier for this captured exchange.
    /// </summary>
    public Guid ExchangeId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Timestamp when the client request was received by the proxy.
    /// </summary>
    public DateTimeOffset RequestTimestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Timestamp when the upstream response was completed by the proxy.
    /// Null if the exchange terminated prematurely or is still in progress.
    /// </summary>
    public DateTimeOffset? ResponseTimestamp { get; init; }

    /// <summary>
    /// Elapsed duration of the exchange from initial request receipt to final response completion.
    /// </summary>
    public TimeSpan? Duration => ResponseTimestamp.HasValue ? ResponseTimestamp.Value - RequestTimestamp : null;

    /// <summary>
    /// ISO-8601 UTC timestamp string formatted as "yyyy-MM-ddTHH:mm:ss.fffZ".
    /// </summary>
    public string FormattedTimestamp => RequestTimestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// HTTP request method (e.g. "POST", "GET", "CONNECT").
    /// </summary>
    public string Method { get; init; } = "POST";

    /// <summary>
    /// HTTP request target URL or path (e.g. "/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse").
    /// </summary>
    public string Url { get; init; } = "/";

    /// <summary>
    /// Upstream target host (e.g. "generativelanguage.googleapis.com").
    /// </summary>
    public string TargetHost { get; init; } = "";

    /// <summary>
    /// Combined HTTP endpoint string formatted as "{Method} {Url}".
    /// </summary>
    public string Endpoint => string.IsNullOrWhiteSpace(Method) ? Url : $"{Method} {Url}";

    /// <summary>
    /// Upstream HTTP response status code (e.g. 200, 426, 500).
    /// </summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>
    /// Upstream HTTP status description (e.g. "OK", "Upgrade Required").
    /// </summary>
    public string? StatusDescription { get; init; }

    /// <summary>
    /// Whether the response was streamed via Server-Sent Events (SSE).
    /// </summary>
    public bool IsStreaming { get; init; }

    /// <summary>
    /// Raw HTTP request headers received from the client before redaction.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawRequestHeaders
    {
        get => _rawRequestHeaders;
        init => _rawRequestHeaders = FreezeHeaders(value);
    }

    /// <summary>
    /// Sanitized HTTP request headers with sensitive credentials replaced by "[REDACTED]".
    /// </summary>
    public IReadOnlyDictionary<string, string> RedactedRequestHeaders
    {
        get => _redactedRequestHeaders;
        init => _redactedRequestHeaders = FreezeHeaders(value);
    }

    /// <summary>
    /// Raw HTTP response headers received from the upstream provider before redaction.
    /// </summary>
    public IReadOnlyDictionary<string, string> RawResponseHeaders
    {
        get => _rawResponseHeaders;
        init => _rawResponseHeaders = FreezeHeaders(value);
    }

    /// <summary>
    /// Sanitized HTTP response headers with sensitive credentials replaced by "[REDACTED]".
    /// </summary>
    public IReadOnlyDictionary<string, string> RedactedResponseHeaders
    {
        get => _redactedResponseHeaders;
        init => _redactedResponseHeaders = FreezeHeaders(value);
    }

    /// <summary>
    /// Exact raw bytes of the request body as received over the wire.
    /// </summary>
    public byte[] RawRequestBody
    {
        get => _rawRequestBody;
        init => _rawRequestBody = value ?? [];
    }

    /// <summary>
    /// Value of the request's Content-Encoding header, if declared (e.g. "gzip", "deflate", "br", "zstd").
    /// </summary>
    public string? RequestContentEncoding { get; init; }

    /// <summary>
    /// Decompressed UTF-8 string representation of the request body.
    /// </summary>
    public string? DecodedRequestBody { get; init; }

    /// <summary>
    /// Warning emitted if request body decompression failed or encountered an unsupported encoding.
    /// </summary>
    public string? RequestDecodingWarning { get; init; }

    /// <summary>
    /// Exact raw bytes of the response body as received over the wire.
    /// </summary>
    public byte[] RawResponseBody
    {
        get => _rawResponseBody;
        init => _rawResponseBody = value ?? [];
    }

    /// <summary>
    /// Value of the response's Content-Encoding header, if declared.
    /// </summary>
    public string? ResponseContentEncoding { get; init; }

    /// <summary>
    /// Decompressed UTF-8 string representation of the response body (or reassembled SSE stream).
    /// </summary>
    public string? DecodedResponseBody { get; init; }

    /// <summary>
    /// Warning emitted if response body decompression failed or encountered an unsupported encoding.
    /// </summary>
    public string? ResponseDecodingWarning { get; init; }

    /// <summary>
    /// Error message describing a network, socket, or proxy-level failure.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether any errors or decompression warnings occurred during this exchange.
    /// </summary>
    public bool HasErrors => !string.IsNullOrEmpty(ErrorMessage) ||
                             !string.IsNullOrEmpty(RequestDecodingWarning) ||
                             !string.IsNullOrEmpty(ResponseDecodingWarning);

    /// <summary>
    /// Wire format classification for this exchange (e.g. Gemini, Anthropic, OpenAi, Raw).
    /// </summary>
    public WireFormat WireFormat { get; init; } = WireFormat.Unknown;

    /// <summary>
    /// Canonical lowercase wire format tag matching the Markdown schema ("gemini", "anthropic", etc.).
    /// </summary>
    public string WireFormatTag => WireFormat.ToWireTag();

    /// <summary>
    /// Human-readable label for the client agent (e.g. "Antigravity CLI").
    /// </summary>
    public string AgentName { get; init; } = "Antigravity CLI";

    /// <summary>
    /// Extracted model identifier (e.g. "gemini-2.5-pro").
    /// </summary>
    public string? ModelName { get; init; }

    /// <summary>
    /// Constructs a filesystem-safe log filename matching the specification:
    /// "yyyy-MM-ddTHH-mm-ss-fff_{agent}.md"
    /// </summary>
    public string BuildLogFileName(string? agentOverride = null)
    {
        var stamp = RequestTimestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ss-fff", CultureInfo.InvariantCulture);
        var rawAgent = !string.IsNullOrWhiteSpace(agentOverride) ? agentOverride : AgentName;
        var sanitizedAgent = SanitizeAgentName(rawAgent);
        return $"{stamp}_{sanitizedAgent}.md";
    }

    /// <summary>
    /// Retrieves the effective request body text for markdown rendering,
    /// returning the warning banner if decompression failed, or UTF-8 text.
    /// </summary>
    public string GetEffectiveRequestBodyText()
    {
        if (!string.IsNullOrEmpty(RequestDecodingWarning))
        {
            var rawText = RawRequestBody.Length > 0 ? Encoding.UTF8.GetString(RawRequestBody) : string.Empty;
            return $"{RequestDecodingWarning}\n\nThe bytes below are still compressed. They are NOT what was actually sent as text:\n\n{rawText}";
        }

        if (DecodedRequestBody is not null)
        {
            return DecodedRequestBody;
        }

        return RawRequestBody.Length > 0 ? Encoding.UTF8.GetString(RawRequestBody) : string.Empty;
    }

    /// <summary>
    /// Retrieves the effective response body text for markdown rendering,
    /// returning the warning banner if decompression failed, or UTF-8 text.
    /// </summary>
    public string GetEffectiveResponseBodyText()
    {
        if (!string.IsNullOrEmpty(ResponseDecodingWarning))
        {
            var rawText = RawResponseBody.Length > 0 ? Encoding.UTF8.GetString(RawResponseBody) : string.Empty;
            return $"{ResponseDecodingWarning}\n\nThe bytes below are still compressed. They are NOT what was actually sent as text:\n\n{rawText}";
        }

        if (DecodedResponseBody is not null)
        {
            return DecodedResponseBody;
        }

        return RawResponseBody.Length > 0 ? Encoding.UTF8.GetString(RawResponseBody) : string.Empty;
    }

    [GeneratedRegex(@"[^a-z0-9\-]")]
    private static partial Regex IllegalCharsRegex();

    [GeneratedRegex(@"-+")]
    private static partial Regex ConsecutiveHyphensRegex();

    private static string SanitizeAgentName(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent)) return "agent";
        // Convert to lowercase, replace spaces and underscores with hyphens, remove illegal filesystem characters
        var cleaned = agent.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        cleaned = IllegalCharsRegex().Replace(cleaned, "");
        cleaned = ConsecutiveHyphensRegex().Replace(cleaned, "-").Trim('-');
        return string.IsNullOrEmpty(cleaned) ? "agent" : cleaned;
    }

    private static FrozenDictionary<string, string> FreezeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return FrozenDictionary<string, string>.Empty;
        }

        if (headers is FrozenDictionary<string, string> frozen &&
            frozen.Comparer == StringComparer.OrdinalIgnoreCase)
        {
            return frozen;
        }

        var dict = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers)
        {
            if (k is not null)
            {
                dict[k] = v;
            }
        }
        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}