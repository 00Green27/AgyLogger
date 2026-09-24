namespace AgyLogger.Cli.Cli;

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models;
using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services;
using AgyLogger.Cli.Services.Proxy;

/// <summary>
/// Constructs the complete System.CommandLine command hierarchy for AgyLogger.
/// </summary>
public static class CommandLineBuilder
{
    public static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("AGY Request Logger - Antigravity CLI transcript viewer & HTTP/HTTPS Interceptor\n\nReads transcript_full.jsonl files from AGY's brain directory\nand renders them as readable Markdown documents.");

        rootCommand.AddCommand(BuildListCommand());
        rootCommand.AddCommand(BuildSyncCommand());
        rootCommand.AddCommand(BuildWatchCommand());
        rootCommand.AddCommand(BuildProxyCommand());
        rootCommand.AddCommand(CaCommandHandler.CreateCommand());
        rootCommand.AddCommand(BuildRunCommand());

        return rootCommand;
    }

    public static Command BuildListCommand()
    {
        var command = new Command("list", "List all available sessions");
        command.SetHandler(ListSessions);
        return command;
    }

    public static Command BuildSyncCommand()
    {
        var command = new Command("sync", "Sync sessions to Markdown files");
        var sessionIdArgument = new Argument<string?>("session-id", () => null, "Session ID to sync");
        var outputOption = new Option<string?>(
            aliases: ["--output", "-o"],
            description: "Output directory (default: .agylogs/)");

        command.AddArgument(sessionIdArgument);
        command.AddOption(outputOption);
        command.SetHandler(context =>
        {
            var sessionId = context.ParseResult.GetValueForArgument(sessionIdArgument);
            var output = context.ParseResult.GetValueForOption(outputOption);
            var exitCode = SyncSessions(sessionId, output);
            context.ExitCode = exitCode;
        });
        return command;
    }

    public static Command BuildWatchCommand()
    {
        var command = new Command("watch", "Watch for new/updated sessions and auto-sync");
        var outputOption = new Option<string?>(
            aliases: ["--output", "-o"],
            description: "Output directory (default: .agylogs/)");

        command.AddOption(outputOption);
        command.SetHandler(context =>
        {
            var output = context.ParseResult.GetValueForOption(outputOption);
            WatchSessions(output, context.GetCancellationToken());
            context.ExitCode = 0;
        });
        return command;
    }

    public static Command BuildProxyCommand()
    {
        var command = new Command("proxy", "Start the AgyLogger interception proxy server");

        var portOption = new Option<int>(
            aliases: ["--port", "-p"],
            getDefaultValue: () => ProxyOptions.DefaultPort,
            description: "TCP port to listen on (0 for dynamic port allocation)");

        var reverseTargetOption = new Option<string?>(
            name: "--reverse-target",
            description: "Target upstream URL for reverse proxy mode");

        var logsDirOption = new Option<string>(
            aliases: ["--logs-dir", "--output", "-o"],
            getDefaultValue: () => ProxyOptions.DefaultLogsDirectory,
            description: "Directory path where Markdown request logs are saved");

        var filterOption = new Option<string?>(
            name: "--filter",
            description: "Optional filter for request logging (use 'none' to disable housekeeping filtering)");

        var rejectWsOption = new Option<bool>(
            name: "--reject-ws",
            getDefaultValue: () => true,
            description: "Reject WebSocket Upgrade requests with HTTP 426");

        command.AddOption(portOption);
        command.AddOption(reverseTargetOption);
        command.AddOption(logsDirOption);
        command.AddOption(filterOption);
        command.AddOption(rejectWsOption);

        command.SetHandler(async (context) =>
        {
            var port = context.ParseResult.GetValueForOption(portOption);
            var reverse = context.ParseResult.GetValueForOption(reverseTargetOption);
            var logsDir = context.ParseResult.GetValueForOption(logsDirOption)!;
            var filter = context.ParseResult.GetValueForOption(filterOption);
            var rejectWs = context.ParseResult.GetValueForOption(rejectWsOption);

            var exitCode = await ProxyCommandRunner.RunAsync(
                port: port,
                reverseTarget: reverse,
                logsDir: logsDir,
                filter: filter,
                rejectWs: rejectWs,
                cancellationToken: context.GetCancellationToken()).ConfigureAwait(false);

            context.ExitCode = exitCode;
        });

        return command;
    }

    public static Command BuildRunCommand()
    {
        var command = new Command("run", "Wrap AGY CLI execution and auto-sync in the background, or run composite proxy and watcher");

        var agyArgsArgument = new Argument<string[]>(
            name: "args",
            getDefaultValue: () => [],
            description: "Optional command and arguments to pass to AGY or child process (e.g. -- agy ...)");

        var outputOption = new Option<string?>(
            aliases: ["--output", "-o"],
            description: "Output directory for transcripts (default: .agylogs/)");

        var portOption = new Option<int>(
            aliases: ["--port", "-p"],
            getDefaultValue: () => ProxyOptions.DefaultPort,
            description: "TCP listening port for proxy (0 for ephemeral)");

        var hostOption = new Option<string>(
            name: "--host",
            getDefaultValue: () => ProxyOptions.DefaultHost,
            description: "Host address to bind proxy listener to");

        var reverseTargetOption = new Option<string?>(
            name: "--reverse-target",
            description: "Target upstream URL for reverse proxy mode");

        var requestsDirOption = new Option<string?>(
            name: "--requests-dir",
            description: "Directory where Markdown request logs are saved");

        var filterOption = new Option<string?>(
            name: "--filter",
            description: "Optional filter for logged requests");

        var rejectWsOption = new Option<bool>(
            name: "--reject-ws",
            getDefaultValue: () => true,
            description: "Reject WebSocket Upgrade requests with HTTP 426");

        var caPathOption = new Option<string?>(
            name: "--ca-path",
            description: "Optional custom Root CA certificate path");

        var autoTrustOption = new Option<bool>(
            name: "--auto-trust",
            getDefaultValue: () => false,
            description: "Automatically trust Root CA certificate in CurrentUser store");

        command.AddArgument(agyArgsArgument);
        command.AddOption(outputOption);
        command.AddOption(portOption);
        command.AddOption(hostOption);
        command.AddOption(reverseTargetOption);
        command.AddOption(requestsDirOption);
        command.AddOption(filterOption);
        command.AddOption(rejectWsOption);
        command.AddOption(caPathOption);
        command.AddOption(autoTrustOption);

        command.TreatUnmatchedTokensAsErrors = true;

        command.SetHandler(async (context) =>
        {
            var childArgs = context.ParseResult.GetValueForArgument(agyArgsArgument);
            var output = context.ParseResult.GetValueForOption(outputOption);
            var port = context.ParseResult.GetValueForOption(portOption);
            var host = context.ParseResult.GetValueForOption(hostOption);
            var reverse = context.ParseResult.GetValueForOption(reverseTargetOption);
            var reqDir = context.ParseResult.GetValueForOption(requestsDirOption);
            var filter = context.ParseResult.GetValueForOption(filterOption);
            var rejectWs = context.ParseResult.GetValueForOption(rejectWsOption);
            var caPath = context.ParseResult.GetValueForOption(caPathOption);
            var autoTrust = context.ParseResult.GetValueForOption(autoTrustOption);

            var exitCode = await CompositeRunner.RunAsync(
                childArgs: childArgs,
                outputDir: output,
                port: port,
                host: host,
                reverseTarget: reverse,
                requestsDir: reqDir,
                filter: filter,
                rejectWs: rejectWs,
                caPath: caPath,
                autoTrust: autoTrust,
                cancellationToken: context.GetCancellationToken()).ConfigureAwait(false);

            context.ExitCode = exitCode;
        });

        return command;
    }

    private static void ListSessions()
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

    public static int SyncSessions(string? sessionId, string? outputDir)
    {
        if (sessionId is not null)
        {
            var info = SessionDiscovery.FindSession(sessionId);
            if (info is null)
            {
                Console.Error.WriteLine($"Session not found: {sessionId}");
                return 1;
            }

            try
            {
                SyncOne(info, outputDir);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error syncing {sessionId}: {ex.Message}");
                return 1;
            }
        }

        var sessions = SessionDiscovery.DiscoverSessions().ToList();
        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions found.");
            return 0;
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
                Console.Error.WriteLine($"  Error syncing {s.ConversationId}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nDone: {synced}/{sessions.Count} sessions synced.");
        return 0;
    }

    private static void SyncOne(SessionInfo info, string? outputDir)
    {
        var session = TranscriptParser.Parse(info.TranscriptPath);
        var exchanges = TranscriptParser.BuildExchanges(session);

        if (exchanges.Count == 0)
        {
            Console.WriteLine($"  {session.ConversationId[..8]}... - no exchanges, skipped");
            return;
        }

        var outPath = MarkdownRenderer.RenderToFile(session, exchanges, outputDir);
        Console.WriteLine($"  {session.ConversationId[..8]}... ({exchanges.Count} turns) -> {Path.GetFileName(outPath)}");
    }

    public static void WatchSessions(string? outputDir = null, CancellationToken cancellationToken = default)
    {
        using var watcher = new TranscriptWatcher(outputDir: outputDir);
        watcher.Start();

        Console.WriteLine("Performing initial sync...\n");
        _ = SyncSessions(null, outputDir);
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            Console.CancelKeyPress += cancelHandler;
        }
        catch
        {
            // Headless console environments without an interactive console throw when registering cancel handlers.
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        try
        {
            Task.Delay(Timeout.Infinite, linkedCts.Token).Wait();
        }
        catch (AggregateException)
        {
            // Expected upon Ctrl+C or token cancellation
        }
        catch (OperationCanceledException)
        {
            // Expected upon Ctrl+C or token cancellation
        }
        finally
        {
            watcher.Stop();

            try
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            catch
            {
                // Headless console environments without an interactive console throw when unregistering cancel handlers.
            }
        }
    }
}