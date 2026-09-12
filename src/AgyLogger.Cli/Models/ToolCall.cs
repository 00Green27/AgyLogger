using System.Text.Json.Serialization;

namespace AgyLogger.Cli.Models;

public sealed record ToolCall
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("args")]
    public Dictionary<string, object>? Args { get; init; }
}
