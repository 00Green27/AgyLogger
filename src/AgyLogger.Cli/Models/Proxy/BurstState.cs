namespace AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Tracks repeated identical request signatures within a rolling burst detection window.
/// </summary>
public sealed class BurstState
{
    public required string Key { get; init; }
    public required long WindowStart { get; init; }
    public required int Count { get; init; }
    public required bool Warned { get; init; }
}