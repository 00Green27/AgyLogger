using AgyLogger.Cli.Models;

namespace AgyLogger.Cli.Services;

/// <summary>
/// Discovers AGY conversation sessions by scanning the brain directory
/// for transcript_full.jsonl files.
/// </summary>
public static class SessionDiscovery
{
    private const string BrainSubDir = "brain";
    private const string TranscriptRelPath = ".system_generated/logs/transcript_full.jsonl";

    /// <summary>
    /// Returns the default AGY brain directory: ~/.gemini/antigravity-cli/brain/
    /// </summary>
    public static string GetDefaultBrainDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".gemini", "antigravity-cli", BrainSubDir);
    }

    /// <summary>
    /// Discovers all conversation sessions that have a transcript_full.jsonl file.
    /// </summary>
    public static IEnumerable<SessionInfo> DiscoverSessions(string? brainDir = null)
    {
        var dir = brainDir ?? GetDefaultBrainDir();
        if (!Directory.Exists(dir))
        {
            Console.Error.WriteLine($"Brain directory not found: {dir}");
            yield break;
        }

        foreach (var convDir in Directory.EnumerateDirectories(dir))
        {
            var transcriptPath = Path.Combine(convDir, TranscriptRelPath);
            if (!File.Exists(transcriptPath))
                continue;

            var conversationId = Path.GetFileName(convDir);
            var fileInfo = new FileInfo(transcriptPath);

            yield return new SessionInfo
            {
                ConversationId = conversationId,
                TranscriptPath = transcriptPath,
                LastModified = fileInfo.LastWriteTimeUtc,
                SizeBytes = fileInfo.Length,
            };
        }
    }

    /// <summary>
    /// Finds a specific session by conversation ID.
    /// </summary>
    public static SessionInfo? FindSession(string conversationId, string? brainDir = null)
    {
        var dir = brainDir ?? GetDefaultBrainDir();
        var transcriptPath = Path.Combine(dir, conversationId, TranscriptRelPath);

        if (!File.Exists(transcriptPath))
            return null;

        var fileInfo = new FileInfo(transcriptPath);
        return new SessionInfo
        {
            ConversationId = conversationId,
            TranscriptPath = transcriptPath,
            LastModified = fileInfo.LastWriteTimeUtc,
            SizeBytes = fileInfo.Length,
        };
    }
}

public sealed record SessionInfo
{
    public required string ConversationId { get; init; }
    public required string TranscriptPath { get; init; }
    public required DateTime LastModified { get; init; }
    public required long SizeBytes { get; init; }
}