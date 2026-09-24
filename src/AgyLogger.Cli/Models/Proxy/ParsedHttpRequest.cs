namespace AgyLogger.Cli.Models.Proxy;

using System.Collections.Generic;

/// <summary>
/// Represents a parsed HTTP request line, headers, and raw payload read from the wire.
/// </summary>
public sealed record ParsedHttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string HttpVersion { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
    public required byte[] Body { get; init; }
}