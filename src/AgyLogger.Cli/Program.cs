using System.CommandLine;

using AgyLogger.Cli.Cli;

var rootCommand = CommandLineBuilder.BuildRootCommand();
return await rootCommand.InvokeAsync(args);