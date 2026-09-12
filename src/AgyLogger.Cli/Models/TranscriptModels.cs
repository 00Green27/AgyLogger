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

public sealed record ToolCall
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("args")]
    public Dictionary<string, object>? Args { get; init; }
}

/// <summary>
/// Sources of transcript steps.
/// </summary>
public static class StepSource
{
    public const string UserExplicit = "USER_EXPLICIT";
    public const string Model = "MODEL";
    public const string System = "SYSTEM";
}

/// <summary>
/// Types of transcript steps.
/// </summary>
public static class StepType
{
    public const string UserInput = "USER_INPUT";
    public const string PlannerResponse = "PLANNER_RESPONSE";
    public const string ConversationHistory = "CONVERSATION_HISTORY";
    public const string SystemMessage = "SYSTEM_MESSAGE";
    public const string Checkpoint = "CHECKPOINT";
    public const string RunCommand = "RUN_COMMAND";
    public const string ViewFile = "VIEW_FILE";
    public const string CodeAction = "CODE_ACTION";
    public const string GrepSearch = "GREP_SEARCH";
    public const string ListDirectory = "LIST_DIRECTORY";
    public const string SearchWeb = "SEARCH_WEB";
    public const string ReadUrlContent = "READ_URL_CONTENT";
    public const string GenerateImage = "GENERATE_IMAGE";
    public const string InvokeSubagent = "INVOKE_SUBAGENT";
    public const string AskQuestion = "ASK_QUESTION";
    public const string Generic = "GENERIC";

    /// <summary>
    /// Non-result structural types that should be skipped during rendering.
    /// </summary>
    public static readonly HashSet<string> StructuralTypes =
    [
        UserInput, PlannerResponse, ConversationHistory,
        SystemMessage, Checkpoint
    ];

    /// <summary>
    /// Returns true if this step type carries a tool result.
    /// Tool results are any step that is NOT a structural type.
    /// </summary>
    public static bool IsToolResult(string type) => !StructuralTypes.Contains(type);
}

/// <summary>
/// A parsed AGY conversation session.
/// </summary>
public sealed class AgSession
{
    public required string ConversationId { get; init; }
    public string? Workspace { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public string? Model { get; set; }
    public List<TranscriptStep> Steps { get; init; } = [];
    public string TranscriptPath { get; init; } = "";
}

/// <summary>
/// A user↔agent exchange: one user prompt and all the agent's responses.
/// </summary>
public sealed class Exchange
{
    public string ExchangeId { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public List<ExchangeMessage> Messages { get; } = [];
}

public sealed record ExchangeMessage
{
    public required string Role { get; init; }
    public required string Timestamp { get; init; }
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public ToolInfo? Tool { get; init; }
}

public sealed class ToolInfo
{
    public required string Name { get; init; }
    public string? ToolAction { get; init; }
    public string? ToolSummary { get; init; }
    public Dictionary<string, object>? Input { get; init; }
    public string? Output { get; set; }
}
