namespace AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Identifies the wire format and payload protocol of an intercepted LLM exchange.
/// Aligns with the taxonomy defined by ai-coding-crash-course/request-logger.
/// </summary>
public enum WireFormat
{
    /// <summary>
    /// Unclassified or unrecognized payload format.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Google Gemini API (generateContent / streamGenerateContent, both direct API key and Code Assist envelope).
    /// </summary>
    Gemini = 1,

    /// <summary>
    /// Anthropic Messages API (/v1/messages).
    /// </summary>
    Anthropic = 2,

    /// <summary>
    /// OpenAI Chat Completions API (/v1/chat/completions).
    /// </summary>
    OpenAi = 3,

    /// <summary>
    /// Generic or unparsed payload falling back to pretty-printed JSON or plain text.
    /// </summary>
    Raw = 4
}

/// <summary>
/// Extension methods and helpers for <see cref="WireFormat"/>.
/// </summary>
public static class WireFormatExtensions
{
    public const string GeminiTag = "gemini";
    public const string AnthropicTag = "anthropic";
    public const string OpenAiTag = "openai";
    public const string RawTag = "raw";
    public const string UnknownTag = "unknown";

    /// <summary>
    /// Converts a <see cref="WireFormat"/> value to its canonical lowercase string representation.
    /// </summary>
    public static string ToWireTag(this WireFormat wireFormat) => wireFormat switch
    {
        WireFormat.Gemini => GeminiTag,
        WireFormat.Anthropic => AnthropicTag,
        WireFormat.OpenAi => OpenAiTag,
        WireFormat.Raw => RawTag,
        _ => UnknownTag
    };

    /// <summary>
    /// Parses a string tag into a <see cref="WireFormat"/> value.
    /// Case-insensitive, trims leading and trailing whitespace.
    /// </summary>
    public static WireFormat FromWireTag(string? tag) => tag?.Trim().ToLowerInvariant() switch
    {
        GeminiTag => WireFormat.Gemini,
        AnthropicTag => WireFormat.Anthropic,
        OpenAiTag => WireFormat.OpenAi,
        RawTag => WireFormat.Raw,
        _ => WireFormat.Unknown
    };
}