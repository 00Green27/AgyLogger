namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Cli;
using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class ProxyCommandTests
{
    [Fact]
    public void ProxyCommand_DefaultOptions_BindsExpectedValues()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy");

        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    [Fact]
    public async Task ProxyCommand_PortZero_AllocatesEphemeralPort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: 0,
            reverseTarget: null,
            logsDir: Path.Combine(Path.GetTempPath(), "test_proxy_logs_" + Guid.NewGuid().ToString("N")),
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr,
            cancellationToken: cts.Token);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Bound Port:", output);
        Assert.DoesNotContain("Bound Port:     0", output);
    }

    [Fact]
    public void ProxyCommand_BannerAndHints_ContainsPowerShellAndBashCommands()
    {
        using var sw = new StringWriter();
        var options = new ProxyOptions
        {
            Port = 8888,
            LogsDirectory = "./.agylogs/requests"
        };

        ProxyCommandRunner.PrintBannerAndHints(sw, options, 8888);
        var output = sw.ToString();

        Assert.Contains("GOOGLE_GEMINI_BASE_URL", output);
        Assert.Contains("HTTPS_PROXY", output);
        Assert.Contains("$env:GOOGLE_GEMINI_BASE_URL=\"http://127.0.0.1:8888\"", output);
        Assert.Contains("export GOOGLE_GEMINI_BASE_URL=\"http://127.0.0.1:8888\"", output);
        Assert.Contains("set GOOGLE_GEMINI_BASE_URL=http://127.0.0.1:8888", output);
    }

    [Fact]
    public async Task ProxyCommand_InvalidReverseTargetUrl_ReturnsExitCodeOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: 8888,
            reverseTarget: "ftp://invalid-scheme",
            logsDir: "./test",
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Fact]
    public async Task ProxyCommand_CancellationToken_StopsServerCleanly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ProxyCommandRunner.RunAsync(
            port: 0,
            reverseTarget: null,
            logsDir: Path.Combine(Path.GetTempPath(), "test_proxy_logs_" + Guid.NewGuid().ToString("N")),
            filter: null,
            rejectWs: true,
            stdout: stdout,
            stderr: stderr,
            cancellationToken: cts.Token);

        Assert.Equal(0, exitCode);
        var output = stdout.ToString();
        Assert.Contains("Proxy server stopped cleanly", output);
    }
}