namespace AgyLogger.Cli.Tests.Proxy;

using System.Text.Json;

using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class GeminiPayloadParserTests
{
    [Fact]
    public void UnwrapRequest_DirectPayload_ReturnsRoot()
    {
        using var doc = JsonDocument.Parse("{\"contents\":[{\"role\":\"user\"}]}");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);

        Assert.True(unwrapped.TryGetProperty("contents", out _));
        Assert.False(unwrapped.TryGetProperty("request", out _));
    }

    [Fact]
    public void UnwrapRequest_WrappedEnvelope_ReturnsInnerRequest()
    {
        using var doc = JsonDocument.Parse("{\"model\":\"gemini-2.5-pro\",\"request\":{\"contents\":[{\"role\":\"user\"}]}}");
        var unwrapped = GeminiPayloadParser.UnwrapRequest(doc.RootElement);

        Assert.True(unwrapped.TryGetProperty("contents", out _));
        Assert.False(unwrapped.TryGetProperty("model", out _));
    }

    [Fact]
    public void UnwrapResponse_DirectPayload_ReturnsRoot()
    {
        using var doc = JsonDocument.Parse("{\"candidates\":[{\"content\":{\"parts\":[]}}]}");
        var unwrapped = GeminiPayloadParser.UnwrapResponse(doc.RootElement);

        Assert.True(unwrapped.TryGetProperty("candidates", out _));
    }

    [Fact]
    public void UnwrapResponse_WrappedEnvelope_ReturnsInnerResponse()
    {
        using var doc = JsonDocument.Parse("{\"response\":{\"candidates\":[{\"content\":{\"parts\":[]}}]}}");
        var unwrapped = GeminiPayloadParser.UnwrapResponse(doc.RootElement);

        Assert.True(unwrapped.TryGetProperty("candidates", out _));
        Assert.False(unwrapped.TryGetProperty("response", out _));
    }

    [Fact]
    public void FindModel_FromUrlPath_ExtractsCorrectModel()
    {
        var model = GeminiPayloadParser.FindModel(null, "/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse");
        Assert.Equal("gemini-2.5-pro", model);
    }

    [Fact]
    public void FindModel_FromBodyProperty_ExtractsCorrectModel()
    {
        using var doc = JsonDocument.Parse("{\"model\":\"gemini-1.5-flash\"}");
        var model = GeminiPayloadParser.FindModel(doc.RootElement, "/api");
        Assert.Equal("gemini-1.5-flash", model);
    }

    [Fact]
    public void Render_SamplingParams_FormatsParamsBlock()
    {
        using var doc = JsonDocument.Parse("{\"generationConfig\":{\"temperature\":0,\"thinkingConfig\":{\"includeThoughts\":true}}}");
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<params>", rendered);
        Assert.Contains("</params>", rendered);
        Assert.Contains("- **temperature**: 0", rendered);
        Assert.Contains("- **thinkingConfig**: {\"includeThoughts\":true}", rendered);
    }

    [Fact]
    public void Render_SystemInstruction_FormatsSystemPromptBlock()
    {
        using var doc = JsonDocument.Parse("{\"systemInstruction\":{\"parts\":[{\"text\":\"You are a test assistant.\"}]}}");
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<system-prompt>", rendered);
        Assert.Contains("You are a test assistant.", rendered);
        Assert.Contains("</system-prompt>", rendered);
    }

    [Fact]
    public void Render_Tools_FormatsFunctionDeclarationsWithSchema()
    {
        var json = """
        {
          "tools": [
            {
              "functionDeclarations": [
                {
                  "name": "calc",
                  "description": "Calculate math",
                  "parametersJsonSchema": {
                    "type": "object",
                    "properties": { "expr": { "type": "string" } }
                  }
                }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<tools>", rendered);
        Assert.Contains("### calc", rendered);
        Assert.Contains("Calculate math", rendered);
        Assert.Contains("```json", rendered);
        Assert.Contains("\"expr\"", rendered);
        Assert.Contains("</tools>", rendered);
    }

    [Fact]
    public void Render_Tools_FormatsBareBuiltinTools()
    {
        var json = "{\"tools\":[{\"googleSearch\":{}}]}";
        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<tools>", rendered);
        Assert.Contains("### googleSearch", rendered);
        Assert.Contains("```json", rendered);
        Assert.Contains("</tools>", rendered);
    }

    [Fact]
    public void Render_Messages_PreservesUserAndModelRoles()
    {
        var json = """
        {
          "contents": [
            { "role": "user", "parts": [{ "text": "ping" }] },
            { "role": "model", "parts": [{ "text": "pong" }] }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<message index=\"1\" role=\"user\">", rendered);
        Assert.Contains("ping", rendered);
        Assert.Contains("<message index=\"2\" role=\"model\">", rendered);
        Assert.Contains("pong", rendered);
    }

    [Fact]
    public void Render_Messages_FormatsThinkingPart()
    {
        var json = """
        {
          "contents": [
            {
              "role": "model",
              "parts": [
                { "text": "Internal thoughts...", "thought": true },
                { "text": "Final reply" }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<thinking>", rendered);
        Assert.Contains("Internal thoughts...", rendered);
        Assert.Contains("</thinking>", rendered);
        Assert.Contains("Final reply", rendered);
    }

    [Fact]
    public void Render_Messages_FormatsToolCallAndToolResult()
    {
        var json = """
        {
          "contents": [
            {
              "role": "model",
              "parts": [
                {
                  "functionCall": {
                    "name": "run_cmd",
                    "args": { "cmd": "ls" }
                  }
                }
              ]
            },
            {
              "role": "user",
              "parts": [
                {
                  "functionResponse": {
                    "name": "run_cmd",
                    "response": {
                      "output": "file.txt"
                    }
                  }
                }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<tool-use name=\"run_cmd\" id=\"\">", rendered);
        Assert.Contains("\"cmd\": \"ls\"", rendered);
        Assert.Contains("<tool-result name=\"run_cmd\" id=\"\">", rendered);
        Assert.Contains("file.txt", rendered);
    }

    [Fact]
    public void Render_Messages_FormatsInlineDataPlaceholder()
    {
        var json = """
        {
          "contents": [
            {
              "role": "user",
              "parts": [
                {
                  "inlineData": {
                    "mimeType": "image/png",
                    "data": "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="
                  }
                }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("`[inline data: image/png,", rendered);
        Assert.Contains("base64 chars \u2014 full data in .request.txt]`", rendered);
    }

    [Fact]
    public void Parse_ValidDirectPayload_PopulatesParsedRequestRecord()
    {
        var json = """
        {
          "systemInstruction": { "parts": [{ "text": "system" }] },
          "generationConfig": { "temperature": 0.7 },
          "contents": [{ "role": "user", "parts": [{ "text": "hello" }] }]
        }
        """;

        var parsed = GeminiPayloadParser.Parse(json, "/v1beta/models/gemini-2.5-pro:generateContent");

        Assert.Equal("gemini-2.5-pro", parsed.Model);
        Assert.Equal("system", parsed.SystemPrompt);
        Assert.Single(parsed.Parameters);
        Assert.Equal("temperature", parsed.Parameters[0].Key);
        Assert.Equal("0.7", parsed.Parameters[0].Value);
        Assert.Single(parsed.Messages);
        Assert.Equal("user", parsed.Messages[0].Role);
        Assert.Equal("hello", parsed.Messages[0].Parts[0].Text);
    }

    [Theory]
    [InlineData("{\"thought\":true}", true)]
    [InlineData("{\"thought\":false}", false)]
    [InlineData("{\"thought\":\"true\"}", true)]
    [InlineData("{\"thought\":\"True\"}", true)]
    [InlineData("{\"thought\":\"false\"}", false)]
    [InlineData("{\"thought\":1}", true)]
    [InlineData("{\"thought\":0}", false)]
    [InlineData("{\"other\":true}", false)]
    public void IsThought_VariousValueKinds_EvaluatedSafely(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);
        var result = GeminiPayloadParser.IsThought(doc.RootElement);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsThought_NonObjectElement_ReturnsFalse()
    {
        using var doc = JsonDocument.Parse("\"not an object\"");
        var result = GeminiPayloadParser.IsThought(doc.RootElement);
        Assert.False(result);
    }

    [Fact]
    public void Render_PartsWithStringPrimitives_RendersTextWithoutThrowing()
    {
        var json = """
        {
          "contents": [
            {
              "role": "user",
              "parts": [
                "raw string line 1",
                "raw string line 2"
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("raw string line 1", rendered);
        Assert.Contains("raw string line 2", rendered);
    }

    [Fact]
    public void ExtractMessages_PartsWithStringPrimitives_PopulatesMessages()
    {
        var json = "{\"contents\":[{\"role\":\"user\",\"parts\":[\"hello world\"]}]}";
        var parsed = GeminiPayloadParser.Parse(json);

        Assert.Single(parsed.Messages);
        Assert.Single(parsed.Messages[0].Parts);
        Assert.Equal("hello world", parsed.Messages[0].Parts[0].Text);
    }

    [Fact]
    public void Render_TopLevelArrayRequest_EmptyArray_DoesNotThrow()
    {
        using var doc = JsonDocument.Parse("[]");
        var rendered = GeminiPayloadParser.Render(doc.RootElement);
        Assert.Empty(rendered);
    }

    [Fact]
    public void Render_TopLevelArrayWrappingObject_RendersCorrectly()
    {
        var json = """
        [
          {
            "generationConfig": { "temperature": 0.5 },
            "contents": [
              { "role": "user", "parts": [{ "text": "array wrapped user" }] }
            ]
          }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<params>", rendered);
        Assert.Contains("- **temperature**: 0.5", rendered);
        Assert.Contains("array wrapped user", rendered);
    }

    [Fact]
    public void Render_TopLevelArrayRequest_RendersMessagesWithoutThrowing()
    {
        var json = """
        [
          {
            "role": "user",
            "parts": [
              { "text": "hi from top-level array" }
            ]
          }
        ]
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<messages>", rendered);
        Assert.Contains("<message index=\"1\" role=\"user\">", rendered);
        Assert.Contains("hi from top-level array", rendered);
        Assert.Contains("</messages>", rendered);
    }

    [Fact]
    public void Parse_TopLevelArrayRequest_ParsesMessagesWithoutThrowing()
    {
        var json = """
        [
          {
            "role": "user",
            "parts": [
              { "text": "hi from array" }
            ]
          }
        ]
        """;

        var parsed = GeminiPayloadParser.Parse(json, "/v1beta/models/gemini-2.5-pro:generateContent");

        Assert.Single(parsed.Messages);
        Assert.Equal("user", parsed.Messages[0].Role);
        Assert.Single(parsed.Messages[0].Parts);
        Assert.Equal("hi from array", parsed.Messages[0].Parts[0].Text);
        Assert.Null(parsed.ParseErrorWarning);
    }

    [Fact]
    public void Render_TopLevelArbitraryArray_FallsBackToCodeBlockWithoutThrowing()
    {
        var json = "[1, 2, 3]";

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("```json", rendered);
        Assert.Contains("1", rendered);
        Assert.Contains("2", rendered);
        Assert.Contains("3", rendered);
    }

    [Fact]
    public void Render_SystemInstructionAsString_DoesNotThrow()
    {
        var json = "{\"systemInstruction\":\"You are a bot.\",\"contents\":[]}";
        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);
        Assert.Contains("<system-prompt>", rendered);
        Assert.Contains("You are a bot.", rendered);
    }

    [Fact]
    public void Render_Messages_HandlesStringAndNumericThoughtInParts()
    {
        var json = """
        {
          "contents": [
            {
              "role": "model",
              "parts": [
                { "text": "Thought as string", "thought": "true" },
                { "text": "Thought as number", "thought": 1 },
                { "text": "Normal text", "thought": false }
              ]
            }
          ]
        }
        """;

        using var doc = JsonDocument.Parse(json);
        var rendered = GeminiPayloadParser.Render(doc.RootElement);

        Assert.Contains("<thinking>\n\nThought as string\n\n</thinking>", rendered);
        Assert.Contains("<thinking>\n\nThought as number\n\n</thinking>", rendered);
        Assert.Contains("Normal text", rendered);
    }
}