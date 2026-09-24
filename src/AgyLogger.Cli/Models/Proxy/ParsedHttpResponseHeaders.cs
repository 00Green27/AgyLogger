namespace AgyLogger.Cli.Models.Proxy;

using System.Collections.Generic;

/// <summary>
/// Represents parsed HTTP response status line and headers read from the upstream wire.
/// </summary>
public sealed record ParsedHttpResponseHeaders
{
    public required int StatusCode { get; init; }
    public required string StatusDescription { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
}