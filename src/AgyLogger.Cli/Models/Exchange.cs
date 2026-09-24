namespace AgyLogger.Cli.Models;

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