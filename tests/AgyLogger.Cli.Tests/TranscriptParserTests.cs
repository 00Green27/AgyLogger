using System.Text;

using AgyLogger.Cli.Services;

using Xunit;

namespace AgyLogger.Cli.Tests;

public class TranscriptParserTests
{
    [Fact]
    public void Parse_MissingOrDeletedWorkspace_DoesNotCrash()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        var jsonl = """
        {"step_index":1,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2023-10-01T12:00:00Z","content":"<USER_REQUEST>fix issue</USER_REQUEST>"}
        {"step_index":2,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2023-10-01T12:00:05Z","content":"ok","tool_calls":[{"name":"run_command","args":{"Cwd":"C:\\NonExistentDirectory\\TestProject","CommandLine":"build"}}]}
        """;
        File.WriteAllText(tempFile, jsonl, Encoding.UTF8);

        try
        {
            // Act
            var session = TranscriptParser.Parse(tempFile);

            // Assert
            Assert.NotNull(session);
            Assert.Equal(2, session.Steps.Count);
            // Since the directory doesn't exist, workspace should be inferred as the raw non-existent dir or null depending on ExtractProjectRoot logic
            // The key is that it doesn't crash with DirectoryNotFoundException!
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CleanUserPrompt_StripsMetadata()
    {
        // Arrange
        var raw = """
        <USER_REQUEST>
        hello
        </USER_REQUEST>
        <ADDITIONAL_METADATA>
        time is now
        </ADDITIONAL_METADATA>
        """;

        // Act
        var result = TranscriptParser.CleanUserPrompt(raw);

        // Assert
        Assert.Equal("hello", result);
    }
}