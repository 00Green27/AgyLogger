namespace AgyLogger.Cli.Services.Proxy;

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Unwraps and parses Gemini API payloads for both direct API key routes
/// and Google Account / Code Assist wrapped OAuth envelopes.
/// </summary>
public static class GeminiPayloadParser
{
    private static readonly Regex ModelPathRegex = new(@"models\/([^:/?]+)", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string[] ParameterKeys =
    [
        "temperature",
        "topP",
        "topK",
        "maxOutputTokens",
        "candidateCount",
        "stopSequences",
        "responseMimeType",
        "thinkingConfig",
        "toolConfig",
        "safetySettings"
    ];

    /// <summary>
    /// Safely determines if a content part represents a model thought/reasoning token.
    /// Tolerates boolean flags, string tokens ("true"), and numeric tokens (1) without throwing.
    /// </summary>
    public static bool IsThought(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!part.TryGetProperty("thought", out var t))
        {
            return false;
        }

        if (t.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (t.ValueKind == JsonValueKind.String)
        {
            var str = t.GetString();
            return bool.TryParse(str, out var b) ? b : str == "1";
        }

        if (t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var n))
        {
            return n != 0;
        }

        return false;
    }

    /// <summary>
    /// Unwraps the outer envelope of a Code Assist / Google Account request if present.
    /// Also unpacks single-element array wrappers containing request objects safely.
    /// </summary>
    public static JsonElement UnwrapRequest(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("request", out var inner) && inner.ValueKind == JsonValueKind.Object)
                {
                    return inner;
                }
                if (first.TryGetProperty("contents", out _) || first.TryGetProperty("generationConfig", out _))
                {
                    return first;
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("request", out var innerReq) &&
            innerReq.ValueKind == JsonValueKind.Object)
        {
            return innerReq;
        }
        return root;
    }

    /// <summary>
    /// Unwraps the outer envelope of a Code Assist / Google Account response event if present.
    /// Also unpacks single-element array wrappers containing response objects safely.
    /// </summary>
    public static JsonElement UnwrapResponse(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var first = root[0];
            if (first.ValueKind == JsonValueKind.Object)
            {
                if (first.TryGetProperty("response", out var innerFirst) && innerFirst.ValueKind == JsonValueKind.Object)
                {
                    return innerFirst;
                }
                if (first.TryGetProperty("candidates", out _))
                {
                    return first;
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("response", out var innerResp) &&
            innerResp.ValueKind == JsonValueKind.Object)
        {
            return innerResp;
        }
        return root;
    }

    /// <summary>
    /// Resolves the model identifier from request JSON or the request URI path.
    /// </summary>
    public static string FindModel(JsonElement? root, string? path)
    {
        if (root.HasValue)
        {
            var r = root.Value;
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0 && r[0].ValueKind == JsonValueKind.Object)
            {
                r = r[0];
            }

            if (r.ValueKind == JsonValueKind.Object)
            {
                if (r.TryGetProperty("model", out var modelProp) && modelProp.ValueKind == JsonValueKind.String)
                {
                    var val = modelProp.GetString();
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }

                if (r.TryGetProperty("request", out var innerReq) && innerReq.ValueKind == JsonValueKind.Object)
                {
                    if (innerReq.TryGetProperty("model", out var innerModel) && innerModel.ValueKind == JsonValueKind.String)
                    {
                        var val = innerModel.GetString();
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            var match = ModelPathRegex.Match(path);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return "unknown";
    }

    /// <summary>
    /// Parses a Gemini JSON request string into a strongly-typed <see cref="ParsedRequest"/>.
    /// </summary>
    public static ParsedRequest Parse(string requestJson, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return new ParsedRequest
            {
                Model = FindModel(null, path),
                RawFallback = requestJson
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var rawRoot = doc.RootElement;
            var model = FindModel(rawRoot, path);
            var unwrapped = UnwrapRequest(rawRoot);

            var parameters = ExtractParameters(unwrapped);
            var systemPrompt = ExtractSystemPrompt(unwrapped);
            var tools = ExtractTools(unwrapped);
            var messages = ExtractMessages(unwrapped);

            return new ParsedRequest
            {
                Model = model,
                SystemPrompt = systemPrompt,
                Parameters = parameters,
                Tools = tools,
                Messages = messages
            };
        }
        catch (Exception ex)
        {
            return new ParsedRequest
            {
                Model = FindModel(null, path),
                RawFallback = requestJson,
                ParseErrorWarning = ex.Message
            };
        }
    }

    /// <summary>
    /// Renders a Gemini JSON request root element into the standard XML-demarcated Markdown blocks
    /// (<params>, <system-prompt>, <tools>, <messages>).
    /// </summary>
    public static string Render(JsonElement rawRoot)
    {
        var unwrapped = UnwrapRequest(rawRoot);

        if (unwrapped.ValueKind == JsonValueKind.Array)
        {
            if (unwrapped.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var hasMessages = false;
            foreach (var item in unwrapped.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    (item.TryGetProperty("role", out _) || item.TryGetProperty("parts", out _)))
                {
                    hasMessages = true;
                    break;
                }
            }

            if (hasMessages)
            {
                return RenderMessages(unwrapped);
            }

            return $"```json\n{JsonSerializer.Serialize(rawRoot, IndentedJsonOptions)}\n```";
        }

        if (unwrapped.ValueKind != JsonValueKind.Object)
        {
            return $"```json\n{JsonSerializer.Serialize(rawRoot, IndentedJsonOptions)}\n```";
        }

        var parts = new List<string>();

        // 1. <params>
        var paramsBlock = RenderParams(unwrapped);
        if (!string.IsNullOrEmpty(paramsBlock)) parts.Add(paramsBlock);

        // 2. <system-prompt>
        if (unwrapped.TryGetProperty("systemInstruction", out var sysInst))
        {
            var sysText = RenderSystemInstruction(sysInst);
            if (!string.IsNullOrWhiteSpace(sysText))
            {
                parts.Add($"<system-prompt>\n\n{sysText}\n\n</system-prompt>");
            }
        }

        // 3. <tools>
        if (unwrapped.TryGetProperty("tools", out var toolsElem) &&
            toolsElem.ValueKind == JsonValueKind.Array &&
            toolsElem.GetArrayLength() > 0)
        {
            var toolsBlock = RenderTools(toolsElem);
            if (!string.IsNullOrEmpty(toolsBlock)) parts.Add(toolsBlock);
        }

        // 4. <messages>
        if (unwrapped.TryGetProperty("contents", out var contentsElem) &&
            contentsElem.ValueKind == JsonValueKind.Array)
        {
            parts.Add(RenderMessages(contentsElem));
        }

        return string.Join("\n\n", parts);
    }

    private static string RenderSystemInstruction(JsonElement sysInst)
    {
        if (sysInst.ValueKind == JsonValueKind.String)
        {
            return sysInst.GetString() ?? string.Empty;
        }

        if (sysInst.ValueKind == JsonValueKind.Object)
        {
            if (sysInst.TryGetProperty("parts", out var parts))
            {
                return RenderGeminiParts(parts);
            }
            return RenderGeminiParts(sysInst);
        }

        return RenderGeminiParts(sysInst);
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ExtractParameters(JsonElement root)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (root.ValueKind != JsonValueKind.Object) return result;

        if (root.TryGetProperty("generationConfig", out var genConfig) && genConfig.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in genConfig.EnumerateObject())
            {
                result.Add(new KeyValuePair<string, string>(prop.Name, FormatJsonParamValue(prop.Value)));
            }
        }
        else
        {
            foreach (var key in ParameterKeys)
            {
                if (root.TryGetProperty(key, out var val))
                {
                    result.Add(new KeyValuePair<string, string>(key, FormatJsonParamValue(val)));
                }
            }
        }
        return result;
    }

    private static string? ExtractSystemPrompt(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (root.TryGetProperty("systemInstruction", out var sysInst))
        {
            var text = RenderSystemInstruction(sysInst);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        return null;
    }

    private static IReadOnlyList<ParsedToolDeclaration> ExtractTools(JsonElement root)
    {
        var result = new List<ParsedToolDeclaration>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        if (!root.TryGetProperty("tools", out var toolsElem) || toolsElem.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var tool in toolsElem.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;

            if (tool.TryGetProperty("functionDeclarations", out var fns) && fns.ValueKind == JsonValueKind.Array)
            {
                foreach (var fn in fns.EnumerateArray())
                {
                    if (fn.ValueKind != JsonValueKind.Object) continue;

                    var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    var desc = fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    string? schemaJson = null;

                    if (fn.TryGetProperty("parametersJsonSchema", out var schema) ||
                        fn.TryGetProperty("parameters", out schema))
                    {
                        schemaJson = JsonSerializer.Serialize(schema, IndentedJsonOptions);
                    }

                    result.Add(new ParsedToolDeclaration
                    {
                        Name = name,
                        Description = desc,
                        ParameterSchemaJson = schemaJson
                    });
                }
            }
            else
            {
                foreach (var prop in tool.EnumerateObject())
                {
                    result.Add(new ParsedToolDeclaration
                    {
                        Name = prop.Name,
                        ParameterSchemaJson = JsonSerializer.Serialize(prop.Value, IndentedJsonOptions)
                    });
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<ParsedMessage> ExtractMessages(JsonElement root)
    {
        var result = new List<ParsedMessage>();
        JsonElement contentsElem;
        if (root.ValueKind == JsonValueKind.Array)
        {
            contentsElem = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("contents", out var c) &&
                 c.ValueKind == JsonValueKind.Array)
        {
            contentsElem = c;
        }
        else
        {
            return result;
        }

        var index = 1;
        foreach (var msg in contentsElem.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "unknown" : "unknown";
            var parts = new List<ParsedContentPart>();

            if (msg.TryGetProperty("parts", out var partsElem))
            {
                if (partsElem.ValueKind == JsonValueKind.String)
                {
                    parts.Add(new ParsedContentPart { Text = partsElem.GetString() ?? string.Empty });
                }
                else if (partsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in partsElem.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(new ParsedContentPart { Text = part.GetString() ?? string.Empty });
                            continue;
                        }

                        if (part.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (part.TryGetProperty("text", out var textElem))
                        {
                            var text = textElem.ValueKind == JsonValueKind.String
                                ? textElem.GetString()
                                : textElem.GetRawText();
                            var isThought = IsThought(part);
                            parts.Add(new ParsedContentPart { Text = text, IsThinking = isThought });
                            continue;
                        }

                        if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
                        {
                            var name = fc.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                            var id = fc.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                            var args = fc.TryGetProperty("args", out var a) ? a : default;
                            parts.Add(new ParsedContentPart
                            {
                                ToolCall = new ParsedToolCall
                                {
                                    Name = name,
                                    Id = id,
                                    ArgumentsJson = JsonSerializer.Serialize(args, IndentedJsonOptions)
                                }
                            });
                            continue;
                        }

                        if (part.TryGetProperty("functionResponse", out var fr) && fr.ValueKind == JsonValueKind.Object)
                        {
                            var name = fr.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                            var id = fr.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                            var resp = fr.TryGetProperty("response", out var respObj) ? respObj : default;

                            string content;
                            if (resp.ValueKind == JsonValueKind.Object &&
                                resp.TryGetProperty("output", out var outProp) &&
                                outProp.ValueKind == JsonValueKind.String)
                            {
                                content = outProp.GetString()!;
                            }
                            else if (resp.ValueKind == JsonValueKind.String)
                            {
                                content = resp.GetString()!;
                            }
                            else
                            {
                                content = JsonSerializer.Serialize(resp, IndentedJsonOptions);
                            }

                            parts.Add(new ParsedContentPart
                            {
                                ToolResult = new ParsedToolResult
                                {
                                    Name = name,
                                    Id = id,
                                    Content = content
                                }
                            });
                            continue;
                        }

                        if (part.TryGetProperty("inlineData", out var data) && data.ValueKind == JsonValueKind.Object)
                        {
                            var mime = data.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "unknown" : "unknown";
                            var bytes = data.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                                ? d.GetString()?.Length ?? 0
                                : 0;
                            parts.Add(new ParsedContentPart
                            {
                                InlineDataPlaceholder = $"`[inline data: {mime}, {bytes} base64 chars \u2014 full data in .request.txt]`"
                            });
                            continue;
                        }

                        parts.Add(new ParsedContentPart
                        {
                            RawJson = JsonSerializer.Serialize(part, IndentedJsonOptions)
                        });
                    }
                }
            }

            result.Add(new ParsedMessage
            {
                Index = index++,
                Role = role,
                Parts = parts
            });
        }

        return result;
    }

    private static string RenderParams(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;

        var entries = new List<string>();
        if (root.TryGetProperty("generationConfig", out var genConfig) && genConfig.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in genConfig.EnumerateObject())
            {
                entries.Add($"- **{prop.Name}**: {FormatJsonParamValue(prop.Value)}");
            }
        }
        else
        {
            foreach (var key in ParameterKeys)
            {
                if (root.TryGetProperty(key, out var val))
                {
                    entries.Add($"- **{key}**: {FormatJsonParamValue(val)}");
                }
            }
        }

        if (entries.Count == 0) return string.Empty;
        return $"<params>\n\n{string.Join("\n", entries)}\n\n</params>";
    }

    private static string FormatJsonParamValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => element.GetRawText(),
        _ => JsonSerializer.Serialize(element)
    };

    private static string RenderTools(JsonElement toolsElem)
    {
        if (toolsElem.ValueKind != JsonValueKind.Array) return string.Empty;

        var rendered = new List<string>();
        foreach (var tool in toolsElem.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object) continue;

            if (tool.TryGetProperty("functionDeclarations", out var fns) && fns.ValueKind == JsonValueKind.Array)
            {
                foreach (var fn in fns.EnumerateArray())
                {
                    if (fn.ValueKind != JsonValueKind.Object) continue;

                    var name = fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "(unnamed tool)" : "(unnamed tool)";
                    var lines = new List<string> { $"### {name}" };

                    if (fn.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(d.GetString()))
                    {
                        lines.Add("");
                        lines.Add(d.GetString()!);
                    }

                    if (fn.TryGetProperty("parametersJsonSchema", out var schema) ||
                        fn.TryGetProperty("parameters", out schema))
                    {
                        lines.Add("");
                        lines.Add($"```json\n{JsonSerializer.Serialize(schema, IndentedJsonOptions)}\n```");
                    }
                    rendered.Add(string.Join("\n", lines));
                }
                continue;
            }

            // Built-in tools: bare keys e.g. { "googleSearch": {} }
            foreach (var prop in tool.EnumerateObject())
            {
                rendered.Add($"### {prop.Name}\n\n```json\n{JsonSerializer.Serialize(prop.Value, IndentedJsonOptions)}\n```");
            }
        }

        if (rendered.Count == 0) return string.Empty;
        return $"<tools>\n\n{string.Join("\n\n", rendered)}\n\n</tools>";
    }

    private static string RenderMessages(JsonElement contentsElem)
    {
        if (contentsElem.ValueKind != JsonValueKind.Array) return string.Empty;

        var rendered = new List<string>();
        var index = 1;

        foreach (var msg in contentsElem.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Object) continue;

            var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "unknown" : "unknown";
            var inner = msg.TryGetProperty("parts", out var parts) ? RenderGeminiParts(parts) : string.Empty;

            rendered.Add($"<message index=\"{index++}\" role=\"{role}\">\n\n{inner}\n\n</message>");
        }

        return $"<messages>\n\n{string.Join("\n\n", rendered)}\n\n</messages>";
    }

    private static string RenderGeminiParts(JsonElement parts)
    {
        if (parts.ValueKind == JsonValueKind.String)
        {
            return parts.GetString() ?? string.Empty;
        }

        if (parts.ValueKind != JsonValueKind.Array)
        {
            return $"```json\n{JsonSerializer.Serialize(parts, IndentedJsonOptions)}\n```";
        }

        var partStrings = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                partStrings.Add(part.GetString() ?? string.Empty);
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                partStrings.Add($"```json\n{JsonSerializer.Serialize(part, IndentedJsonOptions)}\n```");
                continue;
            }

            if (part.TryGetProperty("text", out var textElem))
            {
                var text = textElem.ValueKind == JsonValueKind.String
                    ? textElem.GetString() ?? string.Empty
                    : textElem.GetRawText();
                var isThought = IsThought(part);
                partStrings.Add(isThought ? $"<thinking>\n\n{text}\n\n</thinking>" : text);
                continue;
            }

            if (part.TryGetProperty("functionCall", out var fc) && fc.ValueKind == JsonValueKind.Object)
            {
                var name = fc.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var id = fc.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                var args = fc.TryGetProperty("args", out var a) ? a : default;
                var argsJson = JsonSerializer.Serialize(args, IndentedJsonOptions);
                partStrings.Add($"<tool-use name=\"{name}\" id=\"{id}\">\n\n```json\n{argsJson}\n```\n\n</tool-use>");
                continue;
            }

            if (part.TryGetProperty("functionResponse", out var fr) && fr.ValueKind == JsonValueKind.Object)
            {
                var name = fr.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                var id = fr.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
                var resp = fr.TryGetProperty("response", out var r) ? r : default;

                string content;
                if (resp.ValueKind == JsonValueKind.Object &&
                    resp.TryGetProperty("output", out var outProp) &&
                    outProp.ValueKind == JsonValueKind.String)
                {
                    content = outProp.GetString()!;
                }
                else if (resp.ValueKind == JsonValueKind.String)
                {
                    content = resp.GetString()!;
                }
                else
                {
                    content = $"```json\n{JsonSerializer.Serialize(resp, IndentedJsonOptions)}\n```";
                }

                partStrings.Add($"<tool-result name=\"{name}\" id=\"{id}\">\n\n{content}\n\n</tool-result>");
                continue;
            }

            if (part.TryGetProperty("inlineData", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var mime = data.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "unknown" : "unknown";
                var bytes = data.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString()?.Length ?? 0
                    : 0;
                partStrings.Add($"`[inline data: {mime}, {bytes} base64 chars \u2014 full data in .request.txt]`");
                continue;
            }

            partStrings.Add($"```json\n{JsonSerializer.Serialize(part, IndentedJsonOptions)}\n```");
        }

        return string.Join("\n\n", partStrings);
    }
}