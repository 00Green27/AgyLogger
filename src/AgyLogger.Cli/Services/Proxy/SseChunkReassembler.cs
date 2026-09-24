namespace AgyLogger.Cli.Services.Proxy;

using System.Text;
using System.Text.Json;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Reassembles streaming Server-Sent Events (SSE) into structured LLM response models
/// (thinking blocks, assistant text, tool calls, token usage, and finish reasons).
/// </summary>
public static class SseChunkReassembler
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Safely determines if a content part represents a model thought/reasoning token.
    /// Tolerates boolean flags, string tokens ("true"), and numeric tokens (1) without throwing.
    /// </summary>
    public static bool IsThought(JsonElement part) => GeminiPayloadParser.IsThought(part);

    /// <summary>
    /// Extracts and parses JSON elements from raw SSE data lines.
    /// Buffers multi-line events per W3C SSE standard (consecutive data: lines joined by \n,
    /// delimited by empty lines), while supporting trailing unterminated events and resilient
    /// fallback for single-line streams lacking double-newline separators.
    /// Tolerates keep-alive lines (: ping), comments, [DONE], and malformed chunks without failing.
    /// </summary>
    public static List<JsonElement> ParseSseEvents(string rawSse)
    {
        var result = new List<JsonElement>();
        if (string.IsNullOrWhiteSpace(rawSse))
        {
            return result;
        }

        var eventBuffer = new StringBuilder();

        foreach (var line in rawSse.AsSpan().EnumerateLines())
        {
            // Event boundary: an empty line dispatches the accumulated event block
            if (line.Trim().IsEmpty)
            {
                FlushEvent(eventBuffer, result);
                continue;
            }

            // SSE comment: lines starting with ':' are ignored per W3C specification
            if (!line.IsEmpty && line[0] == ':')
            {
                continue;
            }

            // SSE data field: extract value after "data:"
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var dataValue = line.Slice(5);
                // Per W3C SSE standard, if the first character after colon is a single space, strip it
                if (!dataValue.IsEmpty && dataValue[0] == ' ')
                {
                    dataValue = dataValue.Slice(1);
                }

                if (eventBuffer.Length > 0)
                {
                    eventBuffer.Append('\n');
                }
                eventBuffer.Append(dataValue);
            }
            else if (line.SequenceEqual("data"))
            {
                // Per W3C SSE standard, a line with just "data" represents an empty data line
                if (eventBuffer.Length > 0)
                {
                    eventBuffer.Append('\n');
                }
            }
        }

        // Flush any trailing event not terminated by an empty line
        FlushEvent(eventBuffer, result);

        return result;
    }

    private static void FlushEvent(StringBuilder eventBuffer, List<JsonElement> result)
    {
        if (eventBuffer.Length == 0)
        {
            return;
        }

        var payload = eventBuffer.ToString().Trim();
        eventBuffer.Clear();

        if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]")
        {
            return;
        }

        // 1. Primary W3C Parse: Try parsing the combined multi-line payload as a single JSON document
        try
        {
            using var doc = JsonDocument.Parse(payload);
            result.Add(doc.RootElement.Clone());
            return;
        }
        catch
        {
            // If whole-payload parse fails, payload may contain multiple unseparated single-line JSON events
        }

        // 2. Resilient Fallback: If payload spans multiple lines, attempt to parse each line individually
        var subLines = payload.Split('\n');
        if (subLines.Length > 1)
        {
            foreach (var rawSubLine in subLines)
            {
                var subLine = rawSubLine.Trim();
                if (string.IsNullOrWhiteSpace(subLine) || subLine == "[DONE]")
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(subLine);
                    result.Add(doc.RootElement.Clone());
                }
                catch
                {
                    // Silently skip malformed individual sub-lines to maintain streaming resilience
                }
            }
        }
    }

    /// <summary>
    /// Reassembles Gemini streaming chunks (direct or OAuth wrapped) into a structured <see cref="ParsedResponse"/>.
    /// </summary>
    public static ParsedResponse ReassembleGemini(string rawSse)
    {
        if (string.IsNullOrWhiteSpace(rawSse))
        {
            return new ParsedResponse();
        }

        var thinking = new StringBuilder();
        var assistantText = new StringBuilder();
        var toolCalls = new List<ParsedToolCall>();
        string? finishReason = null;
        string? usageJson = null;

        var events = ParseSseEvents(rawSse);
        foreach (var ev in events)
        {
            var root = GeminiPayloadParser.UnwrapResponse(ev);
            if (root.ValueKind != JsonValueKind.Object) continue;

            if (root.TryGetProperty("usageMetadata", out var usageElem))
            {
                usageJson = JsonSerializer.Serialize(usageElem); // Compact JSON
            }

            if (root.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array &&
                candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.ValueKind != JsonValueKind.Object) continue;

                if (candidate.TryGetProperty("finishReason", out var frElem) && frElem.ValueKind == JsonValueKind.String)
                {
                    finishReason = frElem.GetString();
                }

                if (candidate.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Object &&
                    content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String)
                        {
                            assistantText.Append(part.GetString() ?? string.Empty);
                            continue;
                        }

                        if (part.ValueKind != JsonValueKind.Object) continue;

                        if (part.TryGetProperty("text", out var tElem) && tElem.ValueKind == JsonValueKind.String)
                        {
                            var text = tElem.GetString() ?? string.Empty;
                            var isThought = IsThought(part);
                            if (isThought)
                            {
                                thinking.Append(text);
                            }
                            else
                            {
                                assistantText.Append(text);
                            }
                        }

                        if (part.TryGetProperty("functionCall", out var fcElem) && fcElem.ValueKind == JsonValueKind.Object)
                        {
                            var fnName = fcElem.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                            var fnId = fcElem.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                            var fnArgs = fcElem.TryGetProperty("args", out var a) ? a : default;
                            toolCalls.Add(new ParsedToolCall
                            {
                                Name = fnName,
                                Id = fnId,
                                ArgumentsJson = JsonSerializer.Serialize(fnArgs, IndentedJson)
                            });
                        }
                    }
                }
            }
        }

        return new ParsedResponse
        {
            FinishReason = finishReason,
            UsageJson = usageJson,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            AssistantText = assistantText.Length > 0 ? assistantText.ToString() : null,
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// Reassembles Anthropic streaming SSE events into a structured <see cref="ParsedResponse"/>.
    /// </summary>
    public static ParsedResponse ReassembleAnthropic(string rawSse)
    {
        if (string.IsNullOrWhiteSpace(rawSse))
        {
            return new ParsedResponse();
        }

        var thinking = new StringBuilder();
        var assistantText = new StringBuilder();
        var toolCalls = new List<ParsedToolCall>();
        string? finishReason = null;
        int inputTokens = 0;
        int outputTokens = 0;
        var hasUsage = false;

        var blockTypes = new Dictionary<int, string>();
        var toolCallBuilders = new Dictionary<int, (string Name, string Id, StringBuilder Json)>();

        var events = ParseSseEvents(rawSse);
        foreach (var ev in events)
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;
            if (!ev.TryGetProperty("type", out var typeProp)) continue;
            var type = typeProp.GetString();

            switch (type)
            {
                case "message_start":
                    if (ev.TryGetProperty("message", out var msg) && msg.TryGetProperty("usage", out var uStart))
                    {
                        if (uStart.TryGetProperty("input_tokens", out var inTok))
                        {
                            inputTokens = inTok.GetInt32();
                            hasUsage = true;
                        }
                    }
                    break;

                case "content_block_start":
                    if (ev.TryGetProperty("index", out var idxStart) && ev.TryGetProperty("content_block", out var cb))
                    {
                        var blockIndex = idxStart.GetInt32();
                        var cbType = cb.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        blockTypes[blockIndex] = cbType;

                        if (cbType == "tool_use")
                        {
                            var name = cb.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var id = cb.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                            toolCallBuilders[blockIndex] = (name, id, new StringBuilder());
                        }
                        else if (cbType == "thinking" && cb.TryGetProperty("thinking", out var th))
                        {
                            thinking.Append(th.GetString());
                        }
                        else if (cbType == "text" && cb.TryGetProperty("text", out var tx))
                        {
                            assistantText.Append(tx.GetString());
                        }
                    }
                    break;

                case "content_block_delta":
                    if (ev.TryGetProperty("index", out var idxDelta) && ev.TryGetProperty("delta", out var delta))
                    {
                        var blockIndex = idxDelta.GetInt32();
                        var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() ?? "" : "";

                        if (deltaType == "thinking_delta" && delta.TryGetProperty("thinking", out var th))
                        {
                            thinking.Append(th.GetString());
                        }
                        else if (deltaType == "text_delta" && delta.TryGetProperty("text", out var tx))
                        {
                            assistantText.Append(tx.GetString());
                        }
                        else if (deltaType == "input_json_delta" && delta.TryGetProperty("partial_json", out var pj))
                        {
                            if (toolCallBuilders.TryGetValue(blockIndex, out var builder))
                            {
                                builder.Json.Append(pj.GetString());
                            }
                        }
                    }
                    break;

                case "content_block_stop":
                    if (ev.TryGetProperty("index", out var idxStop))
                    {
                        var blockIndex = idxStop.GetInt32();
                        if (toolCallBuilders.TryGetValue(blockIndex, out var builder))
                        {
                            var rawJson = builder.Json.ToString();
                            string formattedJson;
                            try
                            {
                                using var doc = JsonDocument.Parse(rawJson);
                                formattedJson = JsonSerializer.Serialize(doc.RootElement, IndentedJson);
                            }
                            catch
                            {
                                formattedJson = rawJson;
                            }

                            toolCalls.Add(new ParsedToolCall
                            {
                                Name = builder.Name,
                                Id = builder.Id,
                                ArgumentsJson = formattedJson
                            });
                        }
                    }
                    break;

                case "message_delta":
                    if (ev.TryGetProperty("delta", out var msgDelta))
                    {
                        if (msgDelta.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        {
                            finishReason = sr.GetString();
                        }
                    }
                    if (ev.TryGetProperty("usage", out var uDelta) && uDelta.TryGetProperty("output_tokens", out var outTok))
                    {
                        outputTokens = outTok.GetInt32();
                        hasUsage = true;
                    }
                    break;
            }
        }

        string? usageJson = hasUsage
            ? JsonSerializer.Serialize(new { input_tokens = inputTokens, output_tokens = outputTokens })
            : null;

        return new ParsedResponse
        {
            FinishReason = finishReason,
            UsageJson = usageJson,
            Thinking = thinking.Length > 0 ? thinking.ToString() : null,
            AssistantText = assistantText.Length > 0 ? assistantText.ToString() : null,
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// Reassembles OpenAI streaming SSE events (/chat/completions or /responses) into a structured <see cref="ParsedResponse"/>.
    /// </summary>
    public static ParsedResponse ReassembleOpenAI(string rawSse)
    {
        if (string.IsNullOrWhiteSpace(rawSse))
        {
            return new ParsedResponse();
        }

        var assistantText = new StringBuilder();
        var toolCalls = new List<ParsedToolCall>();
        string? finishReason = null;
        string? usageJson = null;

        var events = ParseSseEvents(rawSse);
        foreach (var ev in events)
        {
            if (ev.ValueKind != JsonValueKind.Object) continue;

            // 1. /responses Realtime format
            if (ev.TryGetProperty("type", out var typeProp))
            {
                var type = typeProp.GetString();
                if (type == "response.output_text.delta" && ev.TryGetProperty("delta", out var deltaElem))
                {
                    assistantText.Append(deltaElem.GetString());
                }
                else if (type == "response.completed" && ev.TryGetProperty("response", out var resp))
                {
                    if (resp.TryGetProperty("status", out var st))
                    {
                        finishReason = st.GetString();
                    }
                    if (resp.TryGetProperty("usage", out var u))
                    {
                        usageJson = JsonSerializer.Serialize(u);
                    }
                }
                continue;
            }

            // 2. /chat/completions format
            if (ev.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    var frStr = fr.GetString();
                    if (!string.IsNullOrEmpty(frStr)) finishReason = frStr;
                }

                if (choice.TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.String)
                    {
                        assistantText.Append(contentElem.GetString());
                    }
                }
            }

            if (ev.TryGetProperty("usage", out var usageElem))
            {
                usageJson = JsonSerializer.Serialize(usageElem);
            }
        }

        return new ParsedResponse
        {
            FinishReason = finishReason,
            UsageJson = usageJson,
            AssistantText = assistantText.Length > 0 ? assistantText.ToString() : null,
            ToolCalls = toolCalls
        };
    }

    /// <summary>
    /// Renders an intercepted response body according to wire format and streaming characteristics.
    /// </summary>
    public static string RenderResponse(WireFormat wireFormat, string rawResponse, JsonElement? requestJson = null)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return "_(empty response)_";
        }

        var looksSse = rawResponse.Contains("data:") || rawResponse.Contains("event:");

        if (wireFormat == WireFormat.Raw)
        {
            try
            {
                if (!looksSse)
                {
                    using var doc = JsonDocument.Parse(rawResponse);
                    return $"```json\n{JsonSerializer.Serialize(doc.RootElement, IndentedJson)}\n```";
                }

                var events = ParseSseEvents(rawResponse);
                if (events.Count > 0)
                {
                    return string.Join("\n\n", events.Select(e => $"```json\n{JsonSerializer.Serialize(e, IndentedJson)}\n```"));
                }
                return $"```\n{rawResponse}\n```";
            }
            catch
            {
                return $"```\n{rawResponse}\n```";
            }
        }

        try
        {
            if (!looksSse)
            {
                using var doc = JsonDocument.Parse(rawResponse);
                return $"```json\n{JsonSerializer.Serialize(doc.RootElement, IndentedJson)}\n```";
            }

            ParsedResponse parsed = wireFormat switch
            {
                WireFormat.Gemini => ReassembleGemini(rawResponse),
                WireFormat.Anthropic => ReassembleAnthropic(rawResponse),
                WireFormat.OpenAi => ReassembleOpenAI(rawResponse),
                _ => ReassembleGemini(rawResponse)
            };

            return FormatParsedResponse(parsed);
        }
        catch (Exception ex)
        {
            return $"<!-- response renderer error, showing raw stream: {ex.Message} -->\n\n```\n{rawResponse}\n```";
        }
    }

    /// <summary>
    /// Formats a reconstructed <see cref="ParsedResponse"/> into standard Markdown.
    /// </summary>
    public static string FormatParsedResponse(ParsedResponse response)
    {
        var parts = new List<string>();

        var metaLines = new List<string>();
        if (!string.IsNullOrEmpty(response.FinishReason))
        {
            metaLines.Add($"- **finish reason**: {response.FinishReason}");
        }
        if (!string.IsNullOrEmpty(response.UsageJson))
        {
            metaLines.Add($"- **usage**: {response.UsageJson}");
        }
        if (metaLines.Count > 0)
        {
            parts.Add(string.Join("\n", metaLines));
        }

        if (!string.IsNullOrEmpty(response.Thinking))
        {
            parts.Add($"<thinking>\n\n{response.Thinking}\n\n</thinking>");
        }

        if (!string.IsNullOrEmpty(response.AssistantText))
        {
            parts.Add($"<assistant-text>\n\n{response.AssistantText}\n\n</assistant-text>");
        }

        foreach (var call in response.ToolCalls)
        {
            parts.Add($"<tool-use name=\"{call.Name}\" id=\"{call.Id}\">\n\n```json\n{call.ArgumentsJson}\n```\n\n</tool-use>");
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : "_(no content decoded)_";
    }
}