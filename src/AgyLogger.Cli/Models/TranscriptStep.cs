using System.Text.Json.Serialization;

namespace AgyLogger.Cli.Models;

/// <summary>
/// One line of transcript_full.jsonl — a single step in the conversation.
/// </summary>
public sealed record TranscriptStep
{
    [JsonPropertyName("step_index")]
    public int StepIndex { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("created_at")]
    public string CreatedAt { get; init; } = "";

    [JsonPropertyName("content")]
    public string Content { get; init; } = "";

    [JsonPropertyName("thinking")]
    public string? Thinking { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("truncated_fields")]
    public List<string>? TruncatedFields { get; init; }
}