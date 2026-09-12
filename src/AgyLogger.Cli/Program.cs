using System.Diagnostics;

using AgyLogger.Cli.Services;
using AgyLogger.Cli.Models;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
switch (command)
{
    case "list":
        ListSessions();
        break;

    case "sync":
        var sessionId = args.Length > 1 ? args[1] : null;
        var outputDir = GetArgValue(args, "--output") ?? GetArgValue(args, "-o");
        SyncSessions(sessionId, outputDir);
        break;

    case "watch":
        var watchOutput = GetArgValue(args, "--output") ?? GetArgValue(args, "-o");
        WatchSessions(watchOutput);
        break;

    case "run":
        var outputDirRun = GetArgValue(args, "--output") ?? GetArgValue(args, "-o");
        RunAgent(args, outputDirRun);
        break;

    case "help" or "--help" or "-h":
    default:
        ShowHelp();
        break;
}

static void ShowHelp()
{
    Console.WriteLine("""
    AGY Request Logger — Antigravity CLI transcript viewer

    Reads transcript_full.jsonl files from AGY's brain directory
    and renders them as readable Markdown documents.

    Usage:
      AgyRequestLogger <command> [options]

    Commands:
      list                    List all available sessions
      sync [session-id]       Sync sessions to Markdown files
      watch                   Watch for new/updated sessions and auto-sync
      run [-- args]           Wrap AGY CLI execution and auto-sync in the background
      help                    Show this help

    Options:
      -o, --output <dir>      Output directory (default: logs/)

    Examples:
      AgyRequestLogger list
      AgyRequestLogger sync
      AgyRequestLogger run
      AgyRequestLogger run -- -p "my prompt"
    """);
}

static void ListSessions()
{
    var sessions = SessionDiscovery.DiscoverSessions().ToList();

    if (sessions.Count == 0)
    {
        Console.WriteLine("No sessions found.");
        return;
    }

    Console.WriteLine($"Found {sessions.Count} session(s):\n");
    Console.WriteLine($"{"ID",-40} {"Last Modified",-22} {"Size",10}");
    Console.WriteLine(new string('\u2500', 75));

    foreach (var s in sessions.OrderByDescending(s => s.LastModified))
    {
        var size = s.SizeBytes switch
        {
            < 1024 => $"{s.SizeBytes} B",
            < 1024 * 1024 => $"{s.SizeBytes / 1024.0:F1} KB",
            _ => $"{s.SizeBytes / (1024.0 * 1024):F1} MB"
        };
        Console.WriteLine($"{s.ConversationId,-40} {s.LastModified.ToLocalTime():yyyy-MM-dd HH:mm:ss}   {size,10}");
    }
}

static void SyncSessions(string? sessionId, string? outputDir)
{
    if (sessionId is not null)
    {
        // Sync a single session
        var info = SessionDiscovery.FindSession(sessionId);
        if (info is null)
        {
            Console.Error.WriteLine($"Session not found: {sessionId}");
            Environment.ExitCode = 1;
            return;
        }

        SyncOne(info, outputDir);
        return;
    }

    // Sync all sessions
    var sessions = SessionDiscovery.DiscoverSessions().ToList();
    if (sessions.Count == 0)
    {
        Console.WriteLine("No sessions found.");
        return;
    }

    Console.WriteLine($"Syncing {sessions.Count} session(s)...\n");
    var synced = 0;

    foreach (var s in sessions.OrderBy(s => s.LastModified))
    {
        try
        {
            SyncOne(s, outputDir);
            synced++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  \u274C {s.ConversationId}: {ex.Message}");
        }
    }

    Console.WriteLine($"\nDone: {synced}/{sessions.Count} sessions synced.");
}

static void SyncOne(SessionInfo info, string? outputDir)
{
    var session = TranscriptParser.Parse(info.TranscriptPath);
    var exchanges = TranscriptParser.BuildExchanges(session);

    if (exchanges.Count == 0)
    {
        Console.WriteLine($"  \u26A0 {session.ConversationId[..8]}... \u2014 no exchanges, skipped");
        return;
    }

    var outPath = MarkdownRenderer.RenderToFile(session, exchanges, outputDir);
    Console.WriteLine($"  \u2705 {session.ConversationId[..8]}... ({exchanges.Count} turns) \u2192 {Path.GetFileName(outPath)}");
}

static void WatchSessions(string? outputDir)
{
    using var watcher = new SessionWatcher(outputDir: outputDir);
    watcher.Start();

    // Sync existing sessions first
    Console.WriteLine("Performing initial sync...\n");
    SyncSessions(null, outputDir);
    Console.WriteLine();

    // Wait for Ctrl+C
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        watcher.Stop();
        cts.Cancel();
    };

    try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); }
    catch (AggregateException) { /* Ctrl+C */ }
}

static string? GetArgValue(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static void RunAgent(string[] args, string? outputDir)
{
    using var watcher = new SessionWatcher(outputDir: outputDir, quiet: true);
    watcher.Start();

    var psi = new ProcessStartInfo
    {
        FileName = "agy",
        UseShellExecute = false,
    };

    // Pass everything after "run" (except -o / --output) to agy
    var agyArgs = new List<string>();
    bool skipNext = false;
    for (int i = 1; i < args.Length; i++)
    {
        if (skipNext)
        {
            skipNext = false;
            continue;
        }
        if (args[i] == "--")
        {
            agyArgs.AddRange(args.Skip(i + 1));
            break;
        }
        if (args[i] == "-o" || args[i] == "--output")
        {
            skipNext = true;
            continue;
        }
        agyArgs.Add(args[i]);
    }

    foreach (var arg in agyArgs)
    {
        psi.ArgumentList.Add(arg);
    }

    try
    {
        using var process = Process.Start(psi);
        if (process is not null)
        {
            process.WaitForExit();
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"\nFailed to start AGY: {ex.Message}");
    }
    finally
    {
        watcher.Stop();
    }
}
