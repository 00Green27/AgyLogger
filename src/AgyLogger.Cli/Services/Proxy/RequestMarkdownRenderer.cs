namespace AgyLogger.Cli.Services.Proxy;

using System.Text;
using System.Text.Json;

using AgyLogger.Cli.Models.Proxy;

/// <summary>
/// Orchestrates rendering of captured HTTP exchanges into standardized Markdown documents
/// demarcated by root XML structural tags (<meta>, <headers>, <request>, <response>)
/// matching the ai-coding-crash-course/request-logger specification.
/// </summary>
public static class RequestMarkdownRenderer
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Renders a captured exchange into a complete Markdown document string.
    /// </summary>
    public static string Render(CapturedExchange exchange)
    {
        var requestText = exchange.GetEffectiveRequestBodyText();
        JsonElement? reqJson = null;

        if (!string.IsNullOrEmpty(requestText) && string.IsNullOrEmpty(exchange.RequestDecodingWarning))
        {
            try
            {
                using var doc = JsonDocument.Parse(requestText);
                reqJson = doc.RootElement.Clone();
            }
            catch
            {
                // Leave null; fallback to raw text in request renderer
            }
        }

        // Determine effective model name
        var model = !string.IsNullOrWhiteSpace(exchange.ModelName)
            ? exchange.ModelName
            : (exchange.WireFormat == WireFormat.Gemini
                ? GeminiPayloadParser.FindModel(reqJson, exchange.Url)
                : FindGenericModel(reqJson));

        var sb = new StringBuilder();

        // 1. <meta>
        sb.Append("<meta>\n\n");
        sb.Append($"- **timestamp**: {exchange.FormattedTimestamp}\n");
        sb.Append($"- **agent**: {exchange.AgentName}\n");
        sb.Append($"- **wire format**: {exchange.WireFormatTag}\n");
        sb.Append($"- **model**: {model}\n");
        sb.Append($"- **endpoint**: {exchange.Endpoint}\n");
        sb.Append($"- **upstream status**: {exchange.StatusCode}\n\n");
        sb.Append("</meta>\n\n");

        // 2. <headers>
        var headersBlock = SensitiveDataRedactor.RenderHeaders(exchange.RedactedRequestHeaders);
        sb.Append($"{headersBlock}\n\n");

        // 3. <request>
        sb.Append("<request>\n\n");
        sb.Append(RenderRequest(exchange.WireFormat, reqJson, requestText, exchange.RequestDecodingWarning));
        sb.Append("\n\n</request>\n\n");

        // 4. <response>
        sb.Append("<response>\n\n");
        if (!string.IsNullOrEmpty(exchange.ResponseDecodingWarning))
        {
            sb.Append(exchange.GetEffectiveResponseBodyText());
        }
        else
        {
            var responseText = exchange.GetEffectiveResponseBodyText();
            sb.Append(SseChunkReassembler.RenderResponse(exchange.WireFormat, responseText, reqJson));
        }
        sb.Append("\n\n</response>\n");

        return sb.ToString();
    }

    /// <summary>
    /// Writes the rendered Markdown log file to disk in the specified output directory,
    /// optionally saving raw companion files (.request.txt and .response.txt).
    /// </summary>
    public static async Task<string> WriteToFileAsync(
        CapturedExchange exchange,
        string outputDirectory,
        bool saveRawCompanionFiles = false,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var markdown = Render(exchange);
        var fileName = exchange.BuildLogFileName();
        var filePath = Path.Combine(outputDirectory, fileName);

        await File.WriteAllTextAsync(filePath, markdown, Encoding.UTF8, cancellationToken);

        if (saveRawCompanionFiles)
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var reqPath = Path.Combine(outputDirectory, $"{baseName}.request.txt");
            var respPath = Path.Combine(outputDirectory, $"{baseName}.response.txt");

            if (exchange.RawRequestBody.Length > 0)
            {
                await File.WriteAllBytesAsync(reqPath, exchange.RawRequestBody, cancellationToken);
            }
            if (exchange.RawResponseBody.Length > 0)
            {
                await File.WriteAllBytesAsync(respPath, exchange.RawResponseBody, cancellationToken);
            }
        }

        return filePath;
    }

    private static string FindGenericModel(JsonElement? root)
    {
        if (root.HasValue && root.Value.ValueKind == JsonValueKind.Object)
        {
            if (root.Value.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var s = m.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return "unknown";
    }

    private static string RenderRequest(
        WireFormat format,
        JsonElement? reqJson,
        string requestText,
        string? decodingWarning)
    {
        if (!string.IsNullOrEmpty(decodingWarning))
        {
            return requestText;
        }

        if (format == WireFormat.Raw || !reqJson.HasValue)
        {
            return reqJson.HasValue
                ? $"```json\n{JsonSerializer.Serialize(reqJson.Value, IndentedJson)}\n```"
                : $"```\n{requestText}\n```";
        }

        try
        {
            if (format == WireFormat.Gemini)
            {
                return GeminiPayloadParser.Render(reqJson.Value);
            }

            if (format == WireFormat.Anthropic)
            {
                return RenderAnthropicRequest(reqJson.Value);
            }

            if (format == WireFormat.OpenAi)
            {
                return RenderOpenAIRequest(reqJson.Value);
            }

            return $"```json\n{JsonSerializer.Serialize(reqJson.Value, IndentedJson)}\n```";
        }
        catch (Exception ex)
        {
            return $"<!-- renderer error, showing raw JSON: {ex.Message} -->\n\n```json\n{JsonSerializer.Serialize(reqJson.Value, IndentedJson)}\n```";
        }
    }

    private static string RenderAnthropicRequest(JsonElement root)
    {
        var parts = new List<string>();

        // System prompt
        if (root.TryGetProperty("system", out var sysElem))
        {
            var sysStr = sysElem.ValueKind == JsonValueKind.String
                ? sysElem.GetString() ?? ""
                : JsonSerializer.Serialize(sysElem, IndentedJson);
            if (!string.IsNullOrWhiteSpace(sysStr))
            {
                parts.Add($"<system-prompt>\n\n{sysStr}\n\n</system-prompt>");
            }
        }

        // Tools
        if (root.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
        {
            var toolParts = new List<string>();
            foreach (var tool in toolsElem.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
                var lines = new List<string> { $"### {name}" };
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    lines.Add("");
                    lines.Add(desc);
                }
                if (tool.TryGetProperty("input_schema", out var schema))
                {
                    lines.Add("");
                    lines.Add($"```json\n{JsonSerializer.Serialize(schema, IndentedJson)}\n```");
                }
                toolParts.Add(string.Join("\n", lines));
            }
            if (toolParts.Count > 0)
            {
                parts.Add($"<tools>\n\n{string.Join("\n\n", toolParts)}\n\n</tools>");
            }
        }

        // Messages
        if (root.TryGetProperty("messages", out var msgsElem) && msgsElem.ValueKind == JsonValueKind.Array)
        {
            var msgParts = new List<string>();
            var index = 1;
            foreach (var msg in msgsElem.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                string content = "";
                if (msg.TryGetProperty("content", out var cElem))
                {
                    content = cElem.ValueKind == JsonValueKind.String
                        ? cElem.GetString() ?? ""
                        : JsonSerializer.Serialize(cElem, IndentedJson);
                }
                msgParts.Add($"<message index=\"{index++}\" role=\"{role}\">\n\n{content}\n\n</message>");
            }
            if (msgParts.Count > 0)
            {
                parts.Add($"<messages>\n\n{string.Join("\n\n", msgParts)}\n\n</messages>");
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : $"```json\n{JsonSerializer.Serialize(root, IndentedJson)}\n```";
    }

    private static string RenderOpenAIRequest(JsonElement root)
    {
        var parts = new List<string>();

        // System prompt / instructions
        if (root.TryGetProperty("instructions", out var instElem) && instElem.ValueKind == JsonValueKind.String)
        {
            var instStr = instElem.GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(instStr))
            {
                parts.Add($"<system-prompt>\n\n{instStr}\n\n</system-prompt>");
            }
        }

        // Tools
        if (root.TryGetProperty("tools", out var toolsElem) && toolsElem.ValueKind == JsonValueKind.Array)
        {
            var toolParts = new List<string>();
            foreach (var tool in toolsElem.EnumerateArray())
            {
                var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
                var desc = tool.TryGetProperty("description", out var d) ? d.GetString() : null;
                JsonElement schema = default;
                var hasSchema = tool.TryGetProperty("parameters", out schema);

                if (tool.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                {
                    if (fn.TryGetProperty("name", out var fnName)) name = fnName.GetString();
                    if (fn.TryGetProperty("description", out var fnDesc)) desc = fnDesc.GetString();
                    if (fn.TryGetProperty("parameters", out var fnParams)) { schema = fnParams; hasSchema = true; }
                }

                var lines = new List<string> { $"### {name ?? "(unnamed tool)"}" };
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    lines.Add("");
                    lines.Add(desc);
                }
                if (hasSchema)
                {
                    lines.Add("");
                    lines.Add($"```json\n{JsonSerializer.Serialize(schema, IndentedJson)}\n```");
                }
                toolParts.Add(string.Join("\n", lines));
            }
            if (toolParts.Count > 0)
            {
                parts.Add($"<tools>\n\n{string.Join("\n\n", toolParts)}\n\n</tools>");
            }
        }

        // Messages / input
        JsonElement messagesElem = default;
        var hasMsgs = root.TryGetProperty("messages", out messagesElem) && messagesElem.ValueKind == JsonValueKind.Array;
        if (!hasMsgs && root.TryGetProperty("input", out var inputElem) && inputElem.ValueKind == JsonValueKind.Array)
        {
            messagesElem = inputElem;
            hasMsgs = true;
        }

        if (hasMsgs)
        {
            var msgParts = new List<string>();
            var index = 1;
            foreach (var msg in messagesElem.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() ?? "user" : "user";
                string content = "";
                if (msg.TryGetProperty("content", out var cElem))
                {
                    content = cElem.ValueKind == JsonValueKind.String
                        ? cElem.GetString() ?? ""
                        : JsonSerializer.Serialize(cElem, IndentedJson);
                }
                msgParts.Add($"<message index=\"{index++}\" role=\"{role}\">\n\n{content}\n\n</message>");
            }
            if (msgParts.Count > 0)
            {
                parts.Add($"<messages>\n\n{string.Join("\n\n", msgParts)}\n\n</messages>");
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : $"```json\n{JsonSerializer.Serialize(root, IndentedJson)}\n```";
    }
}