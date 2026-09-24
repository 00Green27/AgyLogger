using System.Text.RegularExpressions;

using Xunit;

namespace AgyLogger.Cli.Tests.E2E.Harness;

/// <summary>
/// Provides snapshot / golden-file comparison with deterministic scrubbing of dynamic values
/// (timestamps, ports, volatile GUIDs).
/// </summary>
public static class GoldenSnapshotAssert
{
    private static readonly Regex TimestampRegex = new(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z?", RegexOptions.Compiled);
    private static readonly Regex PortRegex = new(@"(localhost|127\.0\.0\.1):\d{4,5}", RegexOptions.Compiled);
    private static readonly Regex GuidRegex = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>
    /// Scrubs dynamic values from a Markdown log string to ensure reproducible golden comparisons.
    /// </summary>
    public static string Scrub(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        // Replace dynamic timestamps with canonical baseline
        var scrubbed = TimestampRegex.Replace(input, "2026-09-20T19:00:00.000Z");

        // Replace ephemeral ports
        scrubbed = PortRegex.Replace(scrubbed, "$1:8888");

        // Replace random GUIDs
        scrubbed = GuidRegex.Replace(scrubbed, "00000000-0000-0000-0000-000000000000");

        // Normalize CRLF to LF
        return scrubbed.Replace("\r\n", "\n").Trim();
    }

    /// <summary>
    /// Asserts that the actual markdown matches the expected golden file or string, after scrubbing.
    /// </summary>
    public static void Matches(string expectedGolden, string actualMarkdown)
    {
        var normalizedExpected = Scrub(expectedGolden);
        var normalizedActual = Scrub(actualMarkdown);

        Assert.Equal(normalizedExpected, normalizedActual);
    }
}