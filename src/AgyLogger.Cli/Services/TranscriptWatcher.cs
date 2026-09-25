namespace AgyLogger.Cli.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

/// <summary>
/// Monitors the AGY brain directory for new or updated transcript files
/// and coordinates rendering to Markdown.
/// </summary>
public sealed class TranscriptWatcher : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private readonly string _brainDir;
    private readonly string _outputDir;
    private readonly bool _quiet;
    private readonly HashSet<string> _processedFiles = [];
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();

    public TranscriptWatcher(string? brainDir = null, string? outputDir = null, bool quiet = false)
    {
        _quiet = quiet;
        _brainDir = brainDir ?? SessionDiscovery.GetDefaultBrainDir();
        _outputDir = outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".agylogs");

        if (!Directory.Exists(_brainDir))
        {
            try
            {
                Directory.CreateDirectory(_brainDir);
            }
            catch
            {
                // Fall back if directory creation is restricted
            }
        }

        if (Directory.Exists(_brainDir))
        {
            _watcher = new FileSystemWatcher(_brainDir)
            {
                IncludeSubdirectories = true,
                Filter = "transcript_full.jsonl",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            };

            _watcher.Changed += OnTranscriptChanged;
            _watcher.Created += OnTranscriptChanged;
        }
    }

    public void Start()
    {
        if (!_quiet)
        {
            Console.WriteLine($"Watching for transcript changes in: {_brainDir}");
            Console.WriteLine($"Transcripts output directory: {_outputDir}");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.WriteLine();
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void Stop()
    {
        _cts.Cancel();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        if (!_quiet)
        {
            Console.WriteLine("\nWatcher stopped.");
        }
    }

    private async void OnTranscriptChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            long lastWriteTicks = 0;
            try
            {
                if (File.Exists(e.FullPath))
                {
                    lastWriteTicks = File.GetLastWriteTimeUtc(e.FullPath).Ticks;
                }
            }
            catch
            {
                // Transient I/O or access error while querying file metadata
            }

            // Debounce: skip if we just processed this file
            lock (_lock)
            {
                var key = $"{e.FullPath}:{lastWriteTicks}";
                if (!_processedFiles.Add(key))
                {
                    return;
                }

                // Prevent unbounded growth
                if (_processedFiles.Count > 10000)
                {
                    _processedFiles.Clear();
                }
            }

            // Small delay to let agy finish writing without blocking the FileSystemWatcher thread pool
            await Task.Delay(500, _cts.Token).ConfigureAwait(false);

            var session = TranscriptParser.Parse(e.FullPath);
            var exchanges = TranscriptParser.BuildExchanges(session);

            if (exchanges.Count == 0)
            {
                if (!_quiet)
                {
                    Console.WriteLine($"  No exchanges in {session.ConversationId}");
                }
                return;
            }

            var outPath = MarkdownRenderer.RenderToFile(session, exchanges, _outputDir);
            var shortId = session.ConversationId.Length >= 8
                ? session.ConversationId[..8]
                : session.ConversationId;

            if (!_quiet)
            {
                Console.WriteLine($"  [{DateTime.Now:HH:mm:ss}] Synced {shortId}... ({exchanges.Count} turns) -> {Path.GetFileName(outPath)}");
            }
        }
        catch (OperationCanceledException)
        {
            // Watcher stopped while processing; expected during shutdown
        }
        catch (Exception ex)
        {
            if (!_quiet)
            {
                Console.Error.WriteLine($"  Error processing {e.FullPath}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _cts.Dispose();
    }
}