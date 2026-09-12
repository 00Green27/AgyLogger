namespace AgyLogger.Cli.Services;

/// <summary>
/// Watches the AGY brain directory for new/updated transcript files
/// and automatically syncs them to Markdown.
/// </summary>
public sealed class SessionWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly string _brainDir;
    private readonly string _outputDir;
    private readonly bool _quiet;
    private readonly HashSet<string> _processedFiles = [];
    private readonly object _lock = new();

    public SessionWatcher(string? brainDir = null, string? outputDir = null, bool quiet = false)
    {
        _quiet = quiet;
        _brainDir = brainDir ?? SessionDiscovery.GetDefaultBrainDir();
        _outputDir = outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), "logs");

        _watcher = new FileSystemWatcher(_brainDir)
        {
            IncludeSubdirectories = true,
            Filter = "transcript_full.jsonl",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
        };

        _watcher.Changed += OnTranscriptChanged;
        _watcher.Created += OnTranscriptChanged;
    }

    public void Start()
    {
        if (!_quiet)
        {
            Console.WriteLine($"\u2728 Watching for transcript changes in: {_brainDir}");
            Console.WriteLine($"\uD83D\uDCC4 Output directory: {_outputDir}");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();
        }

        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
        if (!_quiet)
            Console.WriteLine("\nWatcher stopped.");
    }

    private async void OnTranscriptChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: skip if we just processed this file
        lock (_lock)
        {
            var key = $"{e.FullPath}:{File.GetLastWriteTimeUtc(e.FullPath).Ticks}";
            if (!_processedFiles.Add(key))
                return;

            // Prevent unbounded growth
            if (_processedFiles.Count > 10000)
                _processedFiles.Clear();
        }

        // Small delay to let agy finish writing without blocking the FileSystemWatcher thread pool
        await Task.Delay(500);

        try
        {
            var session = TranscriptParser.Parse(e.FullPath);
            var exchanges = TranscriptParser.BuildExchanges(session);

            if (exchanges.Count == 0)
            {
                if (!_quiet) Console.WriteLine($"  \u26A0 No exchanges in {session.ConversationId}");
                return;
            }

            var outPath = MarkdownRenderer.RenderToFile(session, exchanges, _outputDir);
            var shortId = session.ConversationId.Length >= 8
                ? session.ConversationId[..8]
                : session.ConversationId;
            if (!_quiet) Console.WriteLine($"  \u2705 [{DateTime.Now:HH:mm:ss}] Synced {shortId}... ({exchanges.Count} turns) \u2192 {Path.GetFileName(outPath)}");
        }
        catch (Exception ex)
        {
            if (!_quiet) Console.Error.WriteLine($"  \u274C Error processing {e.FullPath}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
