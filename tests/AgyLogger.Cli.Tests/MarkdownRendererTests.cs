using System.Text.Json;
using AgyLogger.Cli.Models;
using AgyLogger.Cli.Services;
using Xunit;

namespace AgyLogger.Cli.Tests;

public class MarkdownRendererTests
{
    [Fact]
    public void RenderMarkdown_ProducesExpectedFormat()
    {
        // Arrange
        var session = new AgSession
        {
            ConversationId = "1234567890",
            Model = "gemini",
            Workspace = "/home/user/code",
            CreatedAt = "2023-10-01T12:00:00Z",
            UpdatedAt = "2023-10-01T12:05:00Z",
            Steps = []
        };

        var exchanges = new List<Exchange>
        {
            new Exchange
            {
                Messages = 
                {
                    new ExchangeMessage { Role = "user", Timestamp = "2023-10-01T12:00:00Z", Text = "do something" },
                    new ExchangeMessage { Role = "assistant", Timestamp = "2023-10-01T12:00:05Z", Text = "done", Thinking = "I should do it" },
                    new ExchangeMessage 
                    { 
                        Role = "tool", 
                        Timestamp = "2023-10-01T12:00:02Z", 
                        Tool = new ToolInfo 
                        {
                            Name = "run_command",
                            ToolAction = "Running cmd",
                            Input = new Dictionary<string, object> { ["CommandLine"] = "echo 'hi'" },
                            Output = "hi"
                        }
                    }
                }
            }
        };

        // Act
        var markdown = MarkdownRenderer.RenderMarkdown(session, exchanges);

        // Assert
        Assert.Contains("# Antigravity CLI Session", markdown);
        Assert.Contains("`1234567890`", markdown);
        Assert.Contains("gemini", markdown);
        Assert.Contains("### 👤 User", markdown);
        Assert.Contains("do something", markdown);
        Assert.Contains("<summary>🧠 Thinking</summary>", markdown);
        Assert.Contains("I should do it", markdown);
        Assert.Contains("### 🤖 Assistant", markdown);
        Assert.Contains("done", markdown);
        Assert.Contains("🔧 Tool: Running cmd (run_command)", markdown);
        Assert.Contains("echo 'hi'", markdown);
        Assert.Contains("**Output:**", markdown);
    }
}
