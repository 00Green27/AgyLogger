namespace AgyLogger.Cli.Services.Proxy;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Coordinates execution lifecycle for the standalone proxy CLI command:
/// configuration parsing, server startup, banner and setup hints display,
/// signal cancellation trapping, and graceful shutdown.
/// </summary>
public static class ProxyCommandRunner
{
    public static async Task<int> RunAsync(
        int port,
        string? reverseTarget,
        string logsDir,
        string? filter,
        bool rejectWs,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var filterHousekeeping = true;
        string? effectiveFilter = null;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            if (filter.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                filter.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                filter.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                filterHousekeeping = false;
            }
            else
            {
                effectiveFilter = filter;
            }
        }

        var options = new ProxyOptions
        {
            Port = port,
            ReverseTargetUrl = !string.IsNullOrWhiteSpace(reverseTarget)
                ? reverseTarget
                : ProxyOptions.DefaultReverseTargetUrl,
            LogsDirectory = !string.IsNullOrWhiteSpace(logsDir)
                ? logsDir
                : ProxyOptions.DefaultLogsDirectory,
            Filter = effectiveFilter,
            FilterHousekeepingRequests = filterHousekeeping,
            RejectWebSocketUpgrades = rejectWs
        };

        try
        {
            options.Validate();
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"[AgyLogger] Configuration error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

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
            // Headless console environment without cancel key support
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);

        try
        {
            await using var proxy = new ProxyServer(options);
            await proxy.StartAsync(linkedCts.Token).ConfigureAwait(false);

            var boundPort = proxy.BoundPort;
            PrintBannerAndHints(stdout, options, boundPort);

            try
            {
                await Task.Delay(Timeout.Infinite, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown requested via Ctrl+C or cancellation token
            }

            await stdout.WriteLineAsync("\n[AgyLogger] Stopping proxy server...").ConfigureAwait(false);
            await proxy.StopAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync("[AgyLogger] Proxy server stopped cleanly.").ConfigureAwait(false);

            return 0;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"[AgyLogger] Fatal proxy error: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            try
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            catch
            {
                // Headless console environment without cancel key support
            }
        }
    }

    public static void PrintBannerAndHints(TextWriter writer, ProxyOptions options, int boundPort)
    {
        var logsAbsPath = Path.GetFullPath(options.LogsDirectory);

        writer.WriteLine("================================================================================");
        writer.WriteLine("  AgyLogger Interception Proxy Server");
        writer.WriteLine("================================================================================");
        writer.WriteLine("  Status:         Listening");
        writer.WriteLine($"  Bound Port:     {boundPort}");
        writer.WriteLine($"  Logs Directory: {logsAbsPath}");
        writer.WriteLine($"  Reverse Target: {options.ReverseTargetUrl}");
        writer.WriteLine($"  Housekeeping:   {(options.FilterHousekeepingRequests ? "Filtered" : "Logged")}");
        writer.WriteLine($"  WebSocket 426:  {(options.RejectWebSocketUpgrades ? "Enabled" : "Disabled")}");
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            writer.WriteLine($"  Filter:         {options.Filter}");
        }
        writer.WriteLine();
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine("  Setup Hints for Antigravity CLI (agy):");
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine("  [1] Gemini API Key Route (Plain HTTP Reverse Proxy):");
        writer.WriteLine($"      PowerShell:  $env:GOOGLE_GEMINI_BASE_URL=\"http://127.0.0.1:{boundPort}\"");
        writer.WriteLine($"      CMD:         set GOOGLE_GEMINI_BASE_URL=http://127.0.0.1:{boundPort}");
        writer.WriteLine($"      Bash:        export GOOGLE_GEMINI_BASE_URL=\"http://127.0.0.1:{boundPort}\"");
        writer.WriteLine();
        writer.WriteLine("  [2] Default Account / OAuth Route (Forward MitM HTTPS Proxy):");
        writer.WriteLine($"      PowerShell:  $env:HTTPS_PROXY=\"http://127.0.0.1:{boundPort}\"");
        writer.WriteLine($"      CMD:         set HTTPS_PROXY=http://127.0.0.1:{boundPort}");
        writer.WriteLine($"      Bash:        export HTTPS_PROXY=\"http://127.0.0.1:{boundPort}\"");
        writer.WriteLine("================================================================================");
        writer.WriteLine("  Proxy is running. Press Ctrl+C to stop.");
        writer.WriteLine("================================================================================");
    }
}