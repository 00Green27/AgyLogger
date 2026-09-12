namespace AgyLogger.Cli.Models;

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
