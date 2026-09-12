namespace AgyLogger.Cli.Models;

public sealed class ToolInfo
{
    public required string Name { get; init; }
    public string? ToolAction { get; init; }
    public string? ToolSummary { get; init; }
    public Dictionary<string, object>? Input { get; init; }
    public string? Output { get; set; }
}
