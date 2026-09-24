namespace AgyLogger.Cli.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

/// <summary>
/// Concurrently coordinates TranscriptWatcher and ProxyServer, manages environment variable injection,
/// displays copy-pasteable shell export commands, and controls child process execution.
/// </summary>
public static class CompositeRunner
{
    public static async Task<int> RunAsync(
        string[]? childArgs = null,
        string? outputDir = null,
        int port = ProxyOptions.DefaultPort,
        string? host = null,
        string? reverseTarget = null,
        string? requestsDir = null,
        string? filter = null,
        bool rejectWs = true,
        string? caPath = null,
        bool autoTrust = false,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;

        var transcriptOutputDir = outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".agylogs");
        var actualRequestsDir = !string.IsNullOrWhiteSpace(requestsDir)
            ? requestsDir
            : Path.Combine(transcriptOutputDir, "requests");

        var actualCaCertPath = !string.IsNullOrWhiteSpace(caPath)
            ? Path.GetFullPath(caPath)
            : CertificateAuthority.DefaultCrtPath;

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

        var proxyOptions = new ProxyOptions
        {
            Port = port,
            Host = !string.IsNullOrWhiteSpace(host) ? host : ProxyOptions.DefaultHost,
            ReverseTargetUrl = !string.IsNullOrWhiteSpace(reverseTarget) ? reverseTarget : ProxyOptions.DefaultReverseTargetUrl,
            LogsDirectory = actualRequestsDir,
            Filter = effectiveFilter,
            FilterHousekeepingRequests = filterHousekeeping,
            RejectWebSocketUpgrades = rejectWs,
            CaCertPath = actualCaCertPath,
            AutoTrustRootCertificate = autoTrust
        };

        try
        {
            proxyOptions.Validate();
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
            await using var proxyServer = new ProxyServer(proxyOptions);
            await proxyServer.StartAsync(linkedCts.Token).ConfigureAwait(false);

            var boundPort = proxyServer.BoundPort;
            var proxyHost = proxyOptions.Host;

            // Ensure Root CA certificate is exported to disk
            try
            {
                var caDir = Path.GetDirectoryName(actualCaCertPath);
                if (!string.IsNullOrEmpty(caDir) && !Directory.Exists(caDir))
                {
                    Directory.CreateDirectory(caDir);
                }

                if (!File.Exists(actualCaCertPath))
                {
                    proxyServer.CertificateAuthority.ExportRootCertificatePem(actualCaCertPath);
                }
            }
            catch (Exception ex)
            {
                await stderr.WriteLineAsync($"[AgyLogger] Warning: Could not write CA certificate: {ex.Message}").ConfigureAwait(false);
            }

            var hasChild = childArgs is { Length: > 0 };
            using var watcher = new TranscriptWatcher(brainDir: null, outputDir: transcriptOutputDir, quiet: hasChild);
            watcher.Start();

            var envVars = FormatEnvironmentVariables(boundPort, actualCaCertPath, proxyHost);

            if (childArgs is null || childArgs.Length == 0)
            {
                // Standalone mode: Print banner and environment variables
                PrintBannerAndHints(stdout, boundPort, proxyHost, transcriptOutputDir, actualRequestsDir, actualCaCertPath);

                try
                {
                    await Task.Delay(Timeout.Infinite, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal Ctrl+C shutdown
                }

                await stdout.WriteLineAsync("\n[AgyLogger] Stopping proxy server and transcript watcher...").ConfigureAwait(false);
                watcher.Stop();
                await proxyServer.StopAsync().ConfigureAwait(false);
                await stdout.WriteLineAsync("[AgyLogger] Cleanly stopped.").ConfigureAwait(false);
                return 0;
            }
            else
            {
                // Child execution mode
                await stdout.WriteLineAsync($"[AgyLogger] Proxy running on port {boundPort}. Intercepting child process...").ConfigureAwait(false);

                var childExitCode = await ExecuteChildProcessAsync(childArgs, envVars, stdout, stderr, linkedCts.Token).ConfigureAwait(false);

                await stdout.WriteLineAsync($"[AgyLogger] Child process exited with code {childExitCode}. Shutting down proxy.").ConfigureAwait(false);
                watcher.Stop();
                await proxyServer.StopAsync().ConfigureAwait(false);
                return childExitCode;
            }
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"[AgyLogger] Fatal runner error: {ex.Message}").ConfigureAwait(false);
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

    public static Dictionary<string, string> FormatEnvironmentVariables(int port, string caCertPath, string host = "127.0.0.1")
    {
        var proxyUrl = $"http://{host}:{port}";
        var normalizedCaPath = Path.GetFullPath(caCertPath);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = proxyUrl,
            ["HTTPS_PROXY"] = proxyUrl,
            ["http_proxy"] = proxyUrl,
            ["https_proxy"] = proxyUrl,
            ["NODE_EXTRA_CA_CERTS"] = normalizedCaPath,
            ["GOOGLE_GEMINI_BASE_URL"] = proxyUrl
        };
    }

    public static string GenerateExportCommands(int port, string caCertPath, string host = "127.0.0.1")
    {
        var proxyUrl = $"http://{host}:{port}";
        var normalizedCaPath = Path.GetFullPath(caCertPath);
        var unixCaPath = normalizedCaPath.Replace('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine("PowerShell:");
        sb.AppendLine($"  $env:HTTP_PROXY = \"{proxyUrl}\"");
        sb.AppendLine($"  $env:HTTPS_PROXY = \"{proxyUrl}\"");
        sb.AppendLine($"  $env:NODE_EXTRA_CA_CERTS = \"{normalizedCaPath}\"");
        sb.AppendLine($"  $env:GOOGLE_GEMINI_BASE_URL = \"{proxyUrl}\"");
        sb.AppendLine();
        sb.AppendLine("Bash / Zsh:");
        sb.AppendLine($"  export HTTP_PROXY=\"{proxyUrl}\"");
        sb.AppendLine($"  export HTTPS_PROXY=\"{proxyUrl}\"");
        sb.AppendLine($"  export NODE_EXTRA_CA_CERTS=\"{unixCaPath}\"");
        sb.AppendLine($"  export GOOGLE_GEMINI_BASE_URL=\"{proxyUrl}\"");
        sb.AppendLine();
        sb.AppendLine("Windows CMD:");
        sb.AppendLine($"  set HTTP_PROXY={proxyUrl}");
        sb.AppendLine($"  set HTTPS_PROXY={proxyUrl}");
        sb.AppendLine($"  set NODE_EXTRA_CA_CERTS={normalizedCaPath}");
        sb.AppendLine($"  set GOOGLE_GEMINI_BASE_URL={proxyUrl}");

        return sb.ToString();
    }

    public static async Task<int> ExecuteChildProcessAsync(
        string[] childArgs,
        IReadOnlyDictionary<string, string> envVars,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        if (childArgs is null || childArgs.Length == 0)
        {
            throw new ArgumentException("Child arguments cannot be empty.", nameof(childArgs));
        }

        string fileName;
        string[] targetArgs;

        if (childArgs[0].StartsWith('-'))
        {
            fileName = "agy";
            targetArgs = childArgs;
        }
        else if (childArgs[0].Equals("agy", StringComparison.OrdinalIgnoreCase) ||
                 childArgs[0].EndsWith("/agy", StringComparison.OrdinalIgnoreCase) ||
                 childArgs[0].EndsWith("\\agy.exe", StringComparison.OrdinalIgnoreCase) ||
                 childArgs[0].EndsWith("\\agy.cmd", StringComparison.OrdinalIgnoreCase))
        {
            fileName = childArgs[0];
            targetArgs = childArgs.Length > 1 ? childArgs[1..] : [];
        }
        else
        {
            fileName = childArgs[0];
            targetArgs = childArgs.Length > 1 ? childArgs[1..] : [];
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
        };

        foreach (var arg in targetArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in envVars)
        {
            psi.Environment[key] = value;
        }

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Process.Start returned null for {fileName}.");
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"[AgyLogger] Failed to start child process '{fileName}': {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch
                        {
                            // Process might have terminated already
                        }
                    }
                }
                return 130;
            }
        }
    }

    private static void PrintBannerAndHints(
        TextWriter writer,
        int port,
        string host,
        string transcriptDir,
        string requestsDir,
        string caPath)
    {
        writer.WriteLine("================================================================================");
        writer.WriteLine("  AgyLogger Composite Runner");
        writer.WriteLine("================================================================================");
        writer.WriteLine("  Status:             Running");
        writer.WriteLine($"  Proxy Address:      http://{host}:{port}");
        writer.WriteLine($"  Transcripts Output: {Path.GetFullPath(transcriptDir)}");
        writer.WriteLine($"  Requests Log:       {Path.GetFullPath(requestsDir)}");
        writer.WriteLine($"  CA Certificate:     {caPath}");
        writer.WriteLine();
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.WriteLine("  Run these export commands in your client shell to intercept agy traffic:");
        writer.WriteLine("--------------------------------------------------------------------------------");
        writer.Write(GenerateExportCommands(port, caPath, host));
        writer.WriteLine("================================================================================");
        writer.WriteLine("  Composite Runner is active. Press Ctrl+C to stop.");
        writer.WriteLine("================================================================================");
    }
}