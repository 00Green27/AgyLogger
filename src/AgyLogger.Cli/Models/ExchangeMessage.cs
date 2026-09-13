namespace AgyLogger.Cli.Models;

public sealed record ExchangeMessage
{
    public required string Role { get; init; } // 'user', 'assistant', 'tool', 'system', 'error', 'checkpoint'
    public required string Timestamp { get; init; }
    public string? Status { get; init; } // 'RUNNING', 'DONE', 'ERROR'
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public ToolInfo? Tool { get; init; }
    public List<string>? TruncatedFields { get; init; }
}
