namespace AgyLogger.Cli.Models;

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
