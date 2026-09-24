namespace AgyLogger.Cli.Tests.Proxy;

using System.Text.Json;

using AgyLogger.Cli.Models.Proxy;
using AgyLogger.Cli.Services.Proxy;

using Xunit;

public sealed class SseChunkReassemblerTests
{
    [Fact]
    public void ReassembleGemini_JoinsMultipleTextChunks()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":[{"text":"Hello "}]}}]}
        data: {"candidates":[{"content":{"parts":[{"text":"world!"}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Hello world!", res.AssistantText);
        Assert.Null(res.Thinking);
    }

    [Fact]
    public void ReassembleGemini_SeparatesThinkingFromAnswer()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":[{"text":"Thinking step 1. ","thought":true}]}}]}
        data: {"candidates":[{"content":{"parts":[{"text":"Thinking step 2.","thought":true}]}}]}
        data: {"candidates":[{"content":{"parts":[{"text":"Direct answer."}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Thinking step 1. Thinking step 2.", res.Thinking);
        Assert.Equal("Direct answer.", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_ExtractsToolCallsWithArgs()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":[{"functionCall":{"name":"read_file","args":{"path":"test.txt"}}}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Single(res.ToolCalls);
        Assert.Equal("read_file", res.ToolCalls[0].Name);
        Assert.Contains("\"path\": \"test.txt\"", res.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void ReassembleGemini_CapturesFinishReason()
    {
        var sse = """
        data: {"candidates":[{"finishReason":"STOP","content":{"parts":[]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("STOP", res.FinishReason);
    }

    [Fact]
    public void ReassembleGemini_CapturesUsageMetadata()
    {
        var sse = """
        data: {"candidates":[],"usageMetadata":{"totalTokenCount":42}}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("{\"totalTokenCount\":42}", res.UsageJson);
    }

    [Fact]
    public void ReassembleGemini_UnwrapsOAuthResponseEvents()
    {
        var sse = """
        data: {"response":{"candidates":[{"content":{"parts":[{"text":"OAuth answer"}]}}]}}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("OAuth answer", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_SkipsDoneAndKeepAliveLines()
    {
        var sse = """
        : keep-alive comment
        data: {"candidates":[{"content":{"parts":[{"text":"real"}]}}]}
        data: [DONE]
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("real", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_ToleratesMalformedSseLines()
    {
        var sse = """
        data: { broken json
        data: {"candidates":[{"content":{"parts":[{"text":"recovered"}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("recovered", res.AssistantText);
    }

    [Fact]
    public void RenderResponse_EmptyStream_EmitsEmptyResponseOrNoContent()
    {
        var empty = SseChunkReassembler.RenderResponse(WireFormat.Gemini, "");
        Assert.Equal("_(empty response)_", empty);

        var ws = SseChunkReassembler.RenderResponse(WireFormat.Gemini, "   ");
        Assert.Equal("_(empty response)_", ws);
    }

    [Fact]
    public void RenderResponse_NonStreamingJson_PrettyPrints()
    {
        var json = "{\"reply\":\"ok\"}";
        var rendered = SseChunkReassembler.RenderResponse(WireFormat.Gemini, json);

        Assert.Contains("```json", rendered);
        Assert.Contains("\"reply\": \"ok\"", rendered);
    }

    [Fact]
    public void RenderResponse_AnthropicStream_ReconstructsDeltas()
    {
        var sse = """
        data: {"type":"message_start","message":{"usage":{"input_tokens":10}}}
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Plan: check status"}}
        data: {"type":"content_block_stop","index":0}
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Status is clean."}}
        data: {"type":"content_block_stop","index":1}
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":5}}
        data: {"type":"message_stop"}
        """;

        var rendered = SseChunkReassembler.RenderResponse(WireFormat.Anthropic, sse);

        Assert.Contains("- **finish reason**: end_turn", rendered);
        Assert.Contains("- **usage**: {\"input_tokens\":10,\"output_tokens\":5}", rendered);
        Assert.Contains("<thinking>", rendered);
        Assert.Contains("Plan: check status", rendered);
        Assert.Contains("</thinking>", rendered);
        Assert.Contains("<assistant-text>", rendered);
        Assert.Contains("Status is clean.", rendered);
        Assert.Contains("</assistant-text>", rendered);
    }

    [Fact]
    public void RenderResponse_OpenAiStream_ReconstructsDeltas()
    {
        var sse = """
        data: {"type":"response.output_text.delta","delta":"Hello "}
        data: {"type":"response.output_text.delta","delta":"user!"}
        data: {"type":"response.completed","response":{"status":"completed"}}
        """;

        var rendered = SseChunkReassembler.RenderResponse(WireFormat.OpenAi, sse);

        Assert.Contains("- **finish reason**: completed", rendered);
        Assert.Contains("<assistant-text>", rendered);
        Assert.Contains("Hello user!", rendered);
        Assert.Contains("</assistant-text>", rendered);
    }

    [Fact]
    public void ParseSseEvents_MultiLineEvent_BuffersAndParsesSingleJsonEvent()
    {
        var multiLineSse = """
        data: {
        data:   "candidates": [
        data:     {
        data:       "content": {
        data:         "parts": [
        data:           {"text": "Hello from multi-line SSE!"}
        data:         ]
        data:       }
        data:     }
        data:   ]
        data: }

        """;

        var events = SseChunkReassembler.ParseSseEvents(multiLineSse);

        Assert.Single(events);
        var candidateText = events[0]
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();
        Assert.Equal("Hello from multi-line SSE!", candidateText);
    }

    [Fact]
    public void ParseSseEvents_MixedCrlfAndLf_ParsesCorrectly()
    {
        var sse = "data: {\r\ndata:   \"value\": 1\r\ndata: }\r\n\r\ndata: {\"value\": 2}\n\n";
        var events = SseChunkReassembler.ParseSseEvents(sse);
        Assert.Equal(2, events.Count);
        Assert.Equal(1, events[0].GetProperty("value").GetInt32());
        Assert.Equal(2, events[1].GetProperty("value").GetInt32());
    }

    [Fact]
    public void ParseSseEvents_CommentsInsideAndBetweenEvents_Ignored()
    {
        var sse = """
        : initial keep-alive
        data: {
        : comment inside event
        data:   "answer": 42
        data: }

        : trailing ping
        """;

        var events = SseChunkReassembler.ParseSseEvents(sse);
        Assert.Single(events);
        Assert.Equal(42, events[0].GetProperty("answer").GetInt32());
    }

    [Fact]
    public void ParseSseEvents_TrailingEventWithoutTrailingBlankLine_DoesNotDropData()
    {
        var sse = "data: {\"trailing\": true}";
        var events = SseChunkReassembler.ParseSseEvents(sse);
        Assert.Single(events);
        Assert.True(events[0].GetProperty("trailing").GetBoolean());
    }

    [Fact]
    public void ReassembleGemini_MultiLineSseStream_ExtractsAssistantText()
    {
        var multiLineSse = """
        data: {
        data:   "candidates": [
        data:     {
        data:       "content": {
        data:         "parts": [
        data:           {"text": "Multi-line reconstructed text"}
        data:         ]
        data:       }
        data:     }
        data:   ]
        data: }

        """;

        var res = SseChunkReassembler.ReassembleGemini(multiLineSse);

        Assert.Equal("Multi-line reconstructed text", res.AssistantText);
        Assert.Null(res.Thinking);
    }

    [Fact]
    public void ReassembleGemini_ThoughtAsStringToken_SeparatesThinkingFromAnswer()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":[{"text":"Thinking as string token. ","thought":"true"}]}}]}
        data: {"candidates":[{"content":{"parts":[{"text":"Final answer."}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Thinking as string token. ", res.Thinking);
        Assert.Equal("Final answer.", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_ThoughtAsNumberToken_SeparatesThinkingFromAnswer()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":[{"text":"Thinking as number 1. ","thought":1}]}}]}
        data: {"candidates":[{"content":{"parts":[{"text":"Final answer.","thought":0}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("Thinking as number 1. ", res.Thinking);
        Assert.Equal("Final answer.", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_PartsWithStringPrimitives_ExtractsAssistantText()
    {
        var sse = """
        data: {"candidates":[{"content":{"parts":["streamed plain text string"]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("streamed plain text string", res.AssistantText);
    }

    [Fact]
    public void ReassembleGemini_NonObjectEventInStream_DoesNotThrow()
    {
        var sse = """
        data: "plain string event"
        data: {"candidates":[{"content":{"parts":[{"text":"recovered answer"}]}}]}
        """;

        var res = SseChunkReassembler.ReassembleGemini(sse);
        Assert.Equal("recovered answer", res.AssistantText);
    }
}