using System.CommandLine;
using System.Diagnostics;

using AgyLogger.Cli.Services;
using AgyLogger.Cli.Models;

var outputOption = new Option<string?>(
    aliases: ["--output", "-o"],
    description: "Output directory (default: logs/)");

var rootCommand = new RootCommand("AGY Request Logger — Antigravity CLI transcript viewer\n\nReads transcript_full.jsonl files from AGY's brain directory\nand renders them as readable Markdown documents.");

var listCommand = new Command("list", "List all available sessions");
listCommand.SetHandler(ListSessions);

var syncCommand = new Command("sync", "Sync sessions to Markdown files");
var sessionIdArgument = new Argument<string?>("session-id", () => null, "Session ID to sync");
syncCommand.AddArgument(sessionIdArgument);
syncCommand.AddOption(outputOption);
syncCommand.SetHandler(SyncSessions, sessionIdArgument, outputOption);

var watchCommand = new Command("watch", "Watch for new/updated sessions and auto-sync");
watchCommand.AddOption(outputOption);
watchCommand.SetHandler(WatchSessions, outputOption);

var runCommand = new Command("run", "Wrap AGY CLI execution and auto-sync in the background");
var agyArgsArgument = new Argument<string[]>("args", () => [], "Arguments to pass to AGY");
runCommand.AddArgument(agyArgsArgument);
runCommand.AddOption(outputOption);
runCommand.TreatUnmatchedTokensAsErrors = true;
runCommand.SetHandler(RunAgent, agyArgsArgument, outputOption);

rootCommand.AddCommand(listCommand);
rootCommand.AddCommand(syncCommand);
rootCommand.AddCommand(watchCommand);
rootCommand.AddCommand(runCommand);

return await rootCommand.InvokeAsync(args);

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

static void RunAgent(string[] agyArgs, string? outputDir)
{
    using var watcher = new SessionWatcher(outputDir: outputDir, quiet: true);
    watcher.Start();

    var psi = new ProcessStartInfo
    {
        FileName = "agy",
        UseShellExecute = false,
    };

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
