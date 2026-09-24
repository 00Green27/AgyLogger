namespace AgyLogger.Cli.Tests.Cli;

using System;
using System.CommandLine;

using AgyLogger.Cli.Cli;

using Xunit;

public sealed class CliCommandParsingAndValidationTests
{
    [Fact]
    public void Parse_RunWithoutArgs_HasZeroErrors()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("run");

        Assert.Empty(result.Errors);
        Assert.Equal("run", result.CommandResult.Command.Name);
    }

    [Fact]
    public void Parse_RunWithChildArguments_CapturesChildArgs()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("run --port 9000 -- agy -p \"test prompt\"");

        Assert.Empty(result.Errors);
        Assert.Equal("run", result.CommandResult.Command.Name);
    }

    [Fact]
    public void Parse_ProxyWithCustomOptions_ParsesCorrectly()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("proxy --port 8989 --logs-dir ./test-logs --reject-ws false");

        Assert.Empty(result.Errors);
        Assert.Equal("proxy", result.CommandResult.Command.Name);
    }

    [Fact]
    public void Parse_CaExportWithCustomOutput_ParsesCorrectly()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("ca export --output ./certs/custom.crt");

        Assert.Empty(result.Errors);
        Assert.Equal("export", result.CommandResult.Command.Name);
    }

    [Fact]
    public void Parse_InvalidCommand_ProducesError()
    {
        var root = CommandLineBuilder.BuildRootCommand();
        var result = root.Parse("nonexistent-command");

        Assert.NotEmpty(result.Errors);
    }
}