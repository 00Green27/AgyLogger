namespace AgyLogger.Cli.Models;

public sealed record ExchangeMessage
{
    public required string Role { get; init; }
    public required string Timestamp { get; init; }
    public string? Text { get; init; }
    public string? Thinking { get; init; }
    public ToolInfo? Tool { get; init; }
}
