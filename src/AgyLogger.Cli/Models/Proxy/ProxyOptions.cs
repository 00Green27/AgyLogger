namespace AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Configuration options for the AgyLogger HTTP/HTTPS proxy engine.
/// </summary>
public sealed record class ProxyOptions
{
    public const int DefaultPort = 8888;
    public const string DefaultHost = "127.0.0.1";
    public const string DefaultReverseTargetUrl = "https://generativelanguage.googleapis.com";
    public const string DefaultLogsDirectory = "./.agylogs/requests";
    public const string DefaultAgentNameValue = "Antigravity CLI";
    public const int DefaultBurstThreshold = 20;
    public const int DefaultBurstWindowMs = 2000;

    /// <summary>
    /// TCP listening port for the proxy. Defaults to 8888. Specify 0 to dynamically allocate an ephemeral port.
    /// </summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// IP address or hostname to bind the local listener to. Defaults to "127.0.0.1".
    /// </summary>
    public string Host { get; init; } = DefaultHost;

    /// <summary>
    /// Target upstream URL when operating in Reverse Proxy mode (for plain HTTP Gemini API key requests).
    /// Defaults to "https://generativelanguage.googleapis.com".
    /// </summary>
    public string ReverseTargetUrl { get; init; } = DefaultReverseTargetUrl;

    /// <summary>
    /// Local directory path where generated Markdown request logs will be saved.
    /// Defaults to "./.agylogs/requests".
    /// </summary>
    public string LogsDirectory { get; init; } = DefaultLogsDirectory;

    /// <summary>
    /// Whether to write raw companion files (.request.txt and .response.txt) alongside .md logs.
    /// Defaults to false.
    /// </summary>
    public bool SaveRawCompanionFiles { get; init; }

    /// <summary>
    /// Whether to omit background housekeeping requests (such as :countTokens) from disk logs.
    /// Defaults to true.
    /// </summary>
    public bool FilterHousekeepingRequests { get; init; } = true;

    /// <summary>
    /// Optional path or URL filter string to constrain which requests are logged.
    /// Defaults to null.
    /// </summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Whether to reject WebSocket Upgrade requests with HTTP 426 to force HTTP streaming fallback.
    /// Defaults to true.
    /// </summary>
    public bool RejectWebSocketUpgrades { get; init; } = true;

    /// <summary>
    /// Number of identical requests (method + path + status) allowed within <see cref="BurstWindow"/>
    /// before suppressing subsequent repeats to prevent log flooding during client retry loops.
    /// Defaults to 20.
    /// </summary>
    public int BurstThreshold { get; init; } = DefaultBurstThreshold;

    /// <summary>
    /// Time window for detecting tight client-side retry bursts.
    /// Defaults to 2,000 milliseconds.
    /// </summary>
    public TimeSpan BurstWindow { get; init; } = TimeSpan.FromMilliseconds(DefaultBurstWindowMs);

    /// <summary>
    /// Human-readable agent label written to the Markdown &lt;meta&gt; block.
    /// Defaults to "Antigravity CLI".
    /// </summary>
    public string DefaultAgentName { get; init; } = DefaultAgentNameValue;

    /// <summary>
    /// Default wire format to assume if payload inspection does not match a known signature.
    /// Defaults to <see cref="WireFormat.Gemini"/>.
    /// </summary>
    public WireFormat DefaultWireFormat { get; init; } = WireFormat.Gemini;

    /// <summary>
    /// Optional file path to export or read the Root CA certificate.
    /// Defaults to null.
    /// </summary>
    public string? CaCertPath { get; init; }

    /// <summary>
    /// Whether the proxy should attempt to install/trust the dynamically generated Root CA in the
    /// CurrentUser trusted root store on startup. Defaults to false.
    /// </summary>
    public bool AutoTrustRootCertificate { get; init; }

    /// <summary>
    /// Validates option constraints, throwing an <see cref="ArgumentException"/> if invalid.
    /// </summary>
    public void Validate()
    {
        if (Port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "Port must be between 0 and 65535 (0 specifies dynamic port allocation).");
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host must not be null or whitespace.", nameof(Host));
        }

        if (string.IsNullOrWhiteSpace(ReverseTargetUrl))
        {
            throw new ArgumentException("ReverseTargetUrl must not be null or whitespace.", nameof(ReverseTargetUrl));
        }

        if (!Uri.TryCreate(ReverseTargetUrl, UriKind.Absolute, out var targetUri) ||
            (targetUri.Scheme != Uri.UriSchemeHttp && targetUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"ReverseTargetUrl '{ReverseTargetUrl}' must be a valid absolute HTTP or HTTPS URL.", nameof(ReverseTargetUrl));
        }

        if (string.IsNullOrWhiteSpace(LogsDirectory))
        {
            throw new ArgumentException("LogsDirectory must not be null or whitespace.", nameof(LogsDirectory));
        }

        if (BurstThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BurstThreshold), BurstThreshold, "BurstThreshold must be positive.");
        }

        if (BurstWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(BurstWindow), BurstWindow, "BurstWindow must be positive.");
        }
    }
}