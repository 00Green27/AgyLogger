namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AgyLogger.Cli.Services;

using Xunit;

public sealed class CompositeRunnerTests
{
    [Fact]
    public void FormatEnvironmentVariables_ProducesAllRequiredProxyVariables()
    {
        var port = 8888;
        var caPath = Path.Combine(Path.GetTempPath(), "test_ca.crt");
        var env = CompositeRunner.FormatEnvironmentVariables(port, caPath);

        Assert.Equal("http://127.0.0.1:8888", env["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", env["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:8888", env["http_proxy"]);
        Assert.Equal("http://127.0.0.1:8888", env["https_proxy"]);
        Assert.Equal(Path.GetFullPath(caPath), env["NODE_EXTRA_CA_CERTS"]);
        Assert.Equal("http://127.0.0.1:8888", env["GOOGLE_GEMINI_BASE_URL"]);
    }

    [Fact]
    public void GenerateExportCommands_ContainsPowerShellBashAndCmd()
    {
        var port = 8888;
        var caPath = Path.Combine(Path.GetTempPath(), "test_ca.crt");
        var exportText = CompositeRunner.GenerateExportCommands(port, caPath);

        Assert.Contains("$env:HTTP_PROXY = \"http://127.0.0.1:8888\"", exportText);
        Assert.Contains("export HTTP_PROXY=\"http://127.0.0.1:8888\"", exportText);
        Assert.Contains("set HTTP_PROXY=http://127.0.0.1:8888", exportText);
        Assert.Contains("GOOGLE_GEMINI_BASE_URL", exportText);
        Assert.Contains("NODE_EXTRA_CA_CERTS", exportText);
    }

    [Fact]
    public async Task RunAsync_WithCancellationToken_StopsCleanlyWithoutBlocking()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var tempDir = Path.Combine(Path.GetTempPath(), "agy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await CompositeRunner.RunAsync(
                childArgs: [],
                outputDir: tempDir,
                port: 0,
                stdout: stdout,
                stderr: stderr,
                cancellationToken: cts.Token);

            Assert.Equal(0, exitCode);
            var output = stdout.ToString();
            Assert.Contains("Composite Runner", output);
            Assert.Contains("Cleanly stopped", output);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task ExecuteChildProcessAsync_ExecutesProcessAndReturnsExitCode()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var env = CompositeRunner.FormatEnvironmentVariables(8888, "dummy.crt");
        var exitCode = await CompositeRunner.ExecuteChildProcessAsync(
            childArgs: ["dotnet", "--version"],
            envVars: env,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_InvalidReverseTargetUrl_ReturnsExitCodeOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await CompositeRunner.RunAsync(
            childArgs: [],
            reverseTarget: "ftp://not-supported",
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Configuration error", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WithPreCancelledToken_ExitsImmediatelyWithZero()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var tempDir = Path.Combine(Path.GetTempPath(), "agy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var exitCode = await CompositeRunner.RunAsync(
                childArgs: [],
                outputDir: tempDir,
                port: 0,
                stdout: stdout,
                stderr: stderr,
                cancellationToken: cts.Token);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public async Task RunAsync_RapidCancellationCycles_CleansUpPortEveryTime()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "agy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            for (var i = 0; i < 5; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
                using var stdout = new StringWriter();
                using var stderr = new StringWriter();

                var exitCode = await CompositeRunner.RunAsync(
                    childArgs: [],
                    outputDir: tempDir,
                    port: 0,
                    stdout: stdout,
                    stderr: stderr,
                    cancellationToken: cts.Token);

                Assert.Equal(0, exitCode);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void GenerateExportCommands_ValidatesExactShellSyntax()
    {
        var port = 9090;
        var caPath = @"C:\TestPath\ca.crt";
        var exportText = CompositeRunner.GenerateExportCommands(port, caPath, "127.0.0.1");

        // PowerShell syntax check
        Assert.Contains("$env:HTTP_PROXY = \"http://127.0.0.1:9090\"", exportText);
        Assert.Contains("$env:HTTPS_PROXY = \"http://127.0.0.1:9090\"", exportText);
        Assert.Contains("$env:GOOGLE_GEMINI_BASE_URL = \"http://127.0.0.1:9090\"", exportText);
        Assert.Contains(@"$env:NODE_EXTRA_CA_CERTS = ""C:\TestPath\ca.crt""", exportText);

        // Bash syntax check: forward slashes for path
        Assert.Contains("export HTTP_PROXY=\"http://127.0.0.1:9090\"", exportText);
        Assert.Contains("export HTTPS_PROXY=\"http://127.0.0.1:9090\"", exportText);
        Assert.Contains("export GOOGLE_GEMINI_BASE_URL=\"http://127.0.0.1:9090\"", exportText);
        Assert.Contains("export NODE_EXTRA_CA_CERTS=\"C:/TestPath/ca.crt\"", exportText);

        // Windows CMD syntax check: unquoted set commands
        Assert.Contains("set HTTP_PROXY=http://127.0.0.1:9090", exportText);
        Assert.Contains("set HTTPS_PROXY=http://127.0.0.1:9090", exportText);
        Assert.Contains("set GOOGLE_GEMINI_BASE_URL=http://127.0.0.1:9090", exportText);
        Assert.Contains(@"set NODE_EXTRA_CA_CERTS=C:\TestPath\ca.crt", exportText);
    }

    [Fact]
    public async Task ExecuteChildProcessAsync_NonExistentBinary_ReturnsExitCodeOne()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var env = CompositeRunner.FormatEnvironmentVariables(8888, "dummy.crt");
        var exitCode = await CompositeRunner.ExecuteChildProcessAsync(
            childArgs: ["nonexistent_binary_xyza_12345"],
            envVars: env,
            stdout: stdout,
            stderr: stderr);

        Assert.Equal(1, exitCode);
        Assert.Contains("Failed to start child process", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_WithChildProcess_ExecutesAndShutsDownCleanly()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var tempDir = Path.Combine(Path.GetTempPath(), "agy_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var exitCode = await CompositeRunner.RunAsync(
                childArgs: ["dotnet", "--version"],
                outputDir: tempDir,
                port: 0,
                stdout: stdout,
                stderr: stderr);

            Assert.Equal(0, exitCode);
            var outText = stdout.ToString();
            Assert.Contains("Proxy running on port", outText);
            Assert.Contains("Child process exited with code 0", outText);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }
}