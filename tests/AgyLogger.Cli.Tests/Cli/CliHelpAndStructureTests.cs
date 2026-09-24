namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;

using AgyLogger.Cli.Cli;

using Xunit;

public sealed class CliHelpAndStructureTests
{
    [Fact]
    public async Task RootCommand_Help_ReturnsSuccessAndShowsAllCommands()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(sw);
            var result = await root.InvokeAsync(["--help"]);
            Assert.Equal(0, result);

            var output = sw.ToString();
            Assert.Contains("list", output);
            Assert.Contains("sync", output);
            Assert.Contains("watch", output);
            Assert.Contains("run", output);
            Assert.Contains("proxy", output);
            Assert.Contains("ca", output);
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    [Fact]
    public async Task RunCommand_Help_ReturnsSuccessAndShowsCompositeOptions()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(sw);
            var result = await root.InvokeAsync(["run", "--help"]);
            Assert.Equal(0, result);

            var output = sw.ToString();
            Assert.Contains("--output", output);
            Assert.Contains("--port", output);
            Assert.Contains("--reverse-target", output);
            Assert.Contains("--requests-dir", output);
            Assert.Contains("--reject-ws", output);
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    [Fact]
    public async Task ProxyCommand_Help_ReturnsSuccessAndShowsProxyOptions()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(sw);
            var result = await root.InvokeAsync(["proxy", "--help"]);
            Assert.Equal(0, result);

            var output = sw.ToString();
            Assert.Contains("interception proxy server", output);
            Assert.Contains("--port", output);
            Assert.Contains("--reverse-target", output);
            Assert.Contains("--logs-dir", output);
            Assert.Contains("--reject-ws", output);
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    [Fact]
    public async Task CaCommand_Help_ReturnsSuccessAndShowsSubcommands()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(sw);
            var result = await root.InvokeAsync(["ca", "--help"]);
            Assert.Equal(0, result);

            var output = sw.ToString();
            Assert.Contains("status", output);
            Assert.Contains("trust", output);
            Assert.Contains("untrust", output);
            Assert.Contains("export", output);
        }
        finally
        {
            Console.SetOut(orig);
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("trust")]
    [InlineData("untrust")]
    [InlineData("export")]
    public async Task CaSubcommands_Help_ReturnsSuccess(string subcommand)
    {
        var root = CommandLineBuilder.BuildRootCommand();
        using var sw = new StringWriter();
        var orig = Console.Out;
        try
        {
            Console.SetOut(sw);
            var result = await root.InvokeAsync(["ca", subcommand, "--help"]);
            Assert.Equal(0, result);
        }
        finally
        {
            Console.SetOut(orig);
        }
    }
}