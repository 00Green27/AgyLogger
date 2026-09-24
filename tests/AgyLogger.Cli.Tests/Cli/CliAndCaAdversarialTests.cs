namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.CommandLine;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Cli;
using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class CliAndCaAdversarialTests : IDisposable
{
    private readonly string _tempCaDir;
    private const string IsolatedStore = "AgyLoggerTestStore";

    public CliAndCaAdversarialTests()
    {
        _tempCaDir = Path.Combine(Path.GetTempPath(), "agy_adv_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempCaDir);
    }

    public void Dispose()
    {
        try
        {
            using var store = new X509Store(IsolatedStore, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindBySubjectName, CertificateAuthority.DefaultCaCommonName, false);
            foreach (var cert in matches)
            {
                store.Remove(cert);
                cert.Dispose();
            }
        }
        catch { }

        if (Directory.Exists(_tempCaDir))
        {
            try { Directory.Delete(_tempCaDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ProxyCommand_PortZero_AllocatesValidEphemeralPort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var tempLogs = Path.Combine(_tempCaDir, "logs");

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: 0,
            reverseTarget: null,
            logsDir: tempLogs,
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr,
            cancellationToken: cts.Token);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Bound Port:", output);
        Assert.DoesNotContain("Bound Port:     0", output);
        Assert.Contains("$env:GOOGLE_GEMINI_BASE_URL=", output);
    }

    [Fact]
    public async Task CompositeRunner_PortZero_AllocatesEphemeralPortAndSetsEnvironment()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CompositeRunner.RunAsync(
            childArgs: [],
            outputDir: _tempCaDir,
            port: 0,
            stdout: stdout,
            stderr: stderr,
            cancellationToken: cts.Token);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Composite Runner", output);
        Assert.DoesNotContain("http://127.0.0.1:0", output);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-8888)]
    [InlineData(int.MinValue)]
    public async Task ProxyCommand_NegativePorts_ReturnsExitCodeOne(int negativePort)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: negativePort,
            reverseTarget: null,
            logsDir: _tempCaDir,
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Theory]
    [InlineData(65536)]
    [InlineData(70000)]
    [InlineData(int.MaxValue)]
    public async Task ProxyCommand_PortGreaterThan65535_ReturnsExitCodeOne(int outOfRangePort)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: outOfRangePort,
            reverseTarget: null,
            logsDir: _tempCaDir,
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task CompositeRunner_InvalidPorts_ReturnsExitCodeOne(int invalidPort)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CompositeRunner.RunAsync(
            childArgs: [],
            outputDir: _tempCaDir,
            port: invalidPort,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Theory]
    [InlineData("ftp://invalid-scheme")]
    [InlineData("ws://invalid-scheme")]
    [InlineData("wss://invalid-scheme")]
    [InlineData("file:///invalid/path")]
    [InlineData("not-a-url")]
    [InlineData("://missing-scheme")]
    public async Task ProxyCommand_InvalidReverseTargetUrls_ReturnsExitCodeOne(string invalidUrl)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: 8888,
            reverseTarget: invalidUrl,
            logsDir: _tempCaDir,
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Theory]
    [InlineData("ftp://invalid-scheme")]
    [InlineData("not-a-url")]
    [InlineData("file:///invalid/path")]
    public async Task CompositeRunner_InvalidReverseTargetUrls_ReturnsExitCodeOne(string invalidUrl)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CompositeRunner.RunAsync(
            childArgs: [],
            outputDir: _tempCaDir,
            reverseTarget: invalidUrl,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Fact]
    public void CliParsing_PortAliases_BindEquivalently()
    {
        var root = CommandLineBuilder.BuildRootCommand();

        var result1 = root.Parse("proxy -p 9123");
        var result2 = root.Parse("proxy --port 9123");

        Assert.Empty(result1.Errors);
        Assert.Empty(result2.Errors);

        var runResult1 = root.Parse("run -p 9123");
        var runResult2 = root.Parse("run --port 9123");

        Assert.Empty(runResult1.Errors);
        Assert.Empty(runResult2.Errors);
    }

    [Fact]
    public void CliParsing_LogsDirAndOutputAliases_BindEquivalently()
    {
        var root = CommandLineBuilder.BuildRootCommand();

        var result1 = root.Parse("proxy --logs-dir ./custom_logs");
        var result2 = root.Parse("proxy --output ./custom_logs");
        var result3 = root.Parse("proxy -o ./custom_logs");

        Assert.Empty(result1.Errors);
        Assert.Empty(result2.Errors);
        Assert.Empty(result3.Errors);
    }

    [Fact]
    public void CliParsing_CaExportOutputAliases_BindEquivalently()
    {
        var root = CommandLineBuilder.BuildRootCommand();

        var result1 = root.Parse("ca export --output ./ca.crt");
        var result2 = root.Parse("ca export -o ./ca.crt");
        var result3 = root.Parse("ca export --out ./ca.crt");
        var result4 = root.Parse("ca export ./ca.crt");

        Assert.Empty(result1.Errors);
        Assert.Empty(result2.Errors);
        Assert.Empty(result3.Errors);
        Assert.Empty(result4.Errors);
    }

    [Fact]
    public void CliParsing_SyncAndWatchOutputAliases_BindEquivalently()
    {
        var root = CommandLineBuilder.BuildRootCommand();

        var sync1 = root.Parse("sync --output ./markdown");
        var sync2 = root.Parse("sync -o ./markdown");

        Assert.Empty(sync1.Errors);
        Assert.Empty(sync2.Errors);

        var watch1 = root.Parse("watch --output ./markdown");
        var watch2 = root.Parse("watch -o ./markdown");

        Assert.Empty(watch1.Errors);
        Assert.Empty(watch2.Errors);
    }

    [Fact]
    public void CaStatus_WhenNoCaExists_DoesNotCreateFilesOrDirectories()
    {
        var nonExistentDir = Path.Combine(Path.GetTempPath(), "agy_non_existent_" + Guid.NewGuid().ToString("N"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        try
        {
            Assert.False(Directory.Exists(nonExistentDir));

            var exitCode = CaCommandHandler.ExecuteStatus(nonExistentDir, IsolatedStore, stdout, stderr);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("Status:          Not Initialized", output);
            Assert.Contains("PFX File:        Not found", output);

            Assert.False(Directory.Exists(nonExistentDir));
            Assert.False(File.Exists(Path.Combine(nonExistentDir, "ca.pfx")));
            Assert.False(File.Exists(Path.Combine(nonExistentDir, "ca.crt")));
        }
        finally
        {
            if (Directory.Exists(nonExistentDir))
            {
                try { Directory.Delete(nonExistentDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void CaExport_InvalidTargetDirectory_ReturnsExitCodeOneAndDoesNotCrash()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Create an invalid path where a parent directory is actually a file
        var barrierFile = Path.Combine(_tempCaDir, "barrier.txt");
        File.WriteAllText(barrierFile, "barrier");
        var impossiblePath = Path.Combine(barrierFile, "subfolder", "ca.crt");

        var exitCode = CaCommandHandler.ExecuteExport(impossiblePath, _tempCaDir, stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to export Root CA certificate", stderr.ToString());
    }

    [Fact]
    public void CaExport_TargetDirectoryIsExistingDirectory_ReturnsExitCodeOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Target path is an existing directory instead of a file
        var existingDir = Path.Combine(_tempCaDir, "existing_directory");
        Directory.CreateDirectory(existingDir);

        var exitCode = CaCommandHandler.ExecuteExport(existingDir, _tempCaDir, stdout, stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to export Root CA certificate", stderr.ToString());
    }

    [Fact]
    public void CaTrustAndUntrust_IsolatedStore_HeadlessWithoutModalDialogs()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // Step 1: Install to isolated store
        var trustExit1 = CaCommandHandler.ExecuteTrust(_tempCaDir, IsolatedStore, stdout, stderr);
        Assert.Equal(0, trustExit1);
        Assert.Contains("Successfully installed Root CA into trust store", stdout.ToString());

        // Step 2: Idempotent reinstall
        using var stdout2 = new StringWriter();
        var trustExit2 = CaCommandHandler.ExecuteTrust(_tempCaDir, IsolatedStore, stdout2, stderr);
        Assert.Equal(0, trustExit2);
        Assert.Contains("Root CA is already installed and trusted", stdout2.ToString());

        // Step 3: Remove from store
        using var stdout3 = new StringWriter();
        var untrustExit1 = CaCommandHandler.ExecuteUntrust(_tempCaDir, IsolatedStore, stdout3, stderr);
        Assert.Equal(0, untrustExit1);
        Assert.Contains("Successfully removed Root CA from trust store", stdout3.ToString());

        // Step 4: Idempotent removal
        using var stdout4 = new StringWriter();
        var untrustExit2 = CaCommandHandler.ExecuteUntrust(_tempCaDir, IsolatedStore, stdout4, stderr);
        Assert.Equal(0, untrustExit2);
        Assert.Contains("Root CA is not currently installed in the trust store", stdout4.ToString());
    }

    [Fact]
    public async Task RootCommand_CaExportInvalidTargetDirectory_ReturnsExitCodeOne()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var errWriter = new StringWriter();
        var origErr = Console.Error;

        var barrierFile = Path.Combine(_tempCaDir, "barrier.txt");
        File.WriteAllText(barrierFile, "barrier");
        var impossiblePath = Path.Combine(barrierFile, "subfolder", "ca.crt");

        try
        {
            Console.SetError(errWriter);
            var exitCode = await root.InvokeAsync(["ca", "export", impossiblePath, "--ca-dir", _tempCaDir]);
            Assert.Equal(1, exitCode);
            Assert.Contains("Failed to export Root CA certificate", errWriter.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task RootCommand_CaStatusCorruptedPfx_ReturnsExitCodeOne()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var errWriter = new StringWriter();
        var origErr = Console.Error;

        var pfxPath = Path.Combine(_tempCaDir, "ca.pfx");
        File.WriteAllText(pfxPath, "corrupted-pfx-data");

        try
        {
            Console.SetError(errWriter);
            var exitCode = await root.InvokeAsync(["ca", "status", "--ca-dir", _tempCaDir, "--store", IsolatedStore]);
            Assert.Equal(1, exitCode);
            Assert.Contains("Error reading Root CA", errWriter.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public async Task RootCommand_SyncNonexistentSession_ReturnsExitCodeOne()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var errWriter = new StringWriter();
        var origErr = Console.Error;

        try
        {
            Console.SetError(errWriter);
            var exitCode = await root.InvokeAsync(["sync", "nonexistent-session-" + Guid.NewGuid().ToString("N")]);
            Assert.Equal(1, exitCode);
            Assert.Contains("Session not found", errWriter.ToString());
        }
        finally
        {
            Console.SetError(origErr);
        }
    }

    [Fact]
    public void WatchSessions_InHeadlessEnvironment_HandlesCancellationCleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var tempLogs = Path.Combine(_tempCaDir, "watch_logs");

        var exception = Record.Exception(() => CommandLineBuilder.WatchSessions(tempLogs, cts.Token));

        Assert.Null(exception);
    }
}