using System.Text;
using System.Text.Json;

using AgyLogger.Cli.Models;

namespace AgyLogger.Cli.Services;

public static class MarkdownRenderer
{
    private const string OutputSubDir = ".agylogs";

    public static string RenderToFile(AgSession session, List<Exchange> exchanges, string? outputDir = null)
    {
        var dir = outputDir ?? Path.Combine(Directory.GetCurrentDirectory(), OutputSubDir);
        Directory.CreateDirectory(dir);

        var fileName = BuildFileName(session);
        var filePath = Path.Combine(dir, fileName);

        var markdown = RenderMarkdown(session, exchanges);
        File.WriteAllText(filePath, markdown, Encoding.UTF8);

        return filePath;
    }

    public static string RenderMarkdown(AgSession session, List<Exchange> exchanges)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<meta>");
        sb.AppendLine($"- **session_id**: {session.ConversationId}");
        if (!string.IsNullOrEmpty(session.Model))
            sb.AppendLine($"- **model**: {session.Model}");
        if (!string.IsNullOrEmpty(session.Workspace))
            sb.AppendLine($"- **workspace**: `{session.Workspace}`");
        sb.AppendLine($"- **created**: {FormatTimestamp(session.CreatedAt)}");
        sb.AppendLine($"- **updated**: {FormatTimestamp(session.UpdatedAt)}");
        sb.AppendLine("</meta>");
        sb.AppendLine();

        for (var i = 0; i < exchanges.Count; i++)
        {
            var exchange = exchanges[i];
            sb.AppendLine($"<exchange index=\"{i + 1}\">");

            foreach (var msg in exchange.Messages)
            {
                if (msg.Role == "tool")
                {
                    RenderToolMessage(sb, msg);
                }
                else
                {
                    RenderMessage(sb, msg);
                }
            }

            sb.AppendLine("</exchange>");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void RenderMessage(StringBuilder sb, ExchangeMessage msg)
    {
        sb.AppendLine($"<message role=\"{msg.Role}\" timestamp=\"{FormatTimestamp(msg.Timestamp)}\">");

        if (!string.IsNullOrEmpty(msg.Thinking))
        {
            sb.AppendLine("<thinking>");
            sb.AppendLine(msg.Thinking.Trim());
            sb.AppendLine("</thinking>");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(msg.Text))
        {
            if (msg.Role == "assistant") sb.AppendLine("<assistant-text>");
            sb.AppendLine(msg.Text.Trim());
            if (msg.Role == "assistant") sb.AppendLine("</assistant-text>");
        }

        sb.AppendLine("</message>");
        sb.AppendLine();
    }

    private static void RenderToolMessage(StringBuilder sb, ExchangeMessage msg)
    {
        if (msg.Tool is null) return;
        var tool = msg.Tool;
        var status = msg.Status ?? "UNKNOWN";

        sb.AppendLine($"<tool-call name=\"{EscapeHtml(tool.Name)}\" status=\"{status}\" timestamp=\"{FormatTimestamp(msg.Timestamp)}\">");

        sb.AppendLine("<tool-input>");
        RenderToolInput(sb, tool);
        sb.AppendLine("</tool-input>");

        if (tool.Output is not null)
        {
            sb.AppendLine("<tool-output>");
            var output = tool.Output.Trim();
            if (output.Length > 8000)
                output = output[..8000] + "\n... (truncated 8000 chars)";

            if (output.Contains('\n') || (output.StartsWith('{') && output.EndsWith('}')))
            {
                sb.AppendLine("```");
                sb.AppendLine(output);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine(output);
            }
            sb.AppendLine("</tool-output>");
        }

        sb.AppendLine("</tool-call>");
        sb.AppendLine();
    }

    private static void RenderToolInput(StringBuilder sb, ToolInfo tool)
    {
        if (tool.Input is null || tool.Input.Count == 0)
            return;

        var relevantArgs = tool.Input
            .Where(kv => kv.Key is not ("toolAction" or "toolSummary"))
            .ToList();

        if (relevantArgs.Count == 0)
            return;

        switch (tool.Name)
        {
            case "run_command":
                var cmd = GetArg(tool.Input, "CommandLine");
                var cwd = GetArg(tool.Input, "Cwd");
                if (cwd is not null) sb.AppendLine($"- **cwd**: `{cwd}`");
                sb.AppendLine("```bash");
                sb.AppendLine(cmd ?? "");
                sb.AppendLine("```");
                return;

            case "write_to_file":
                var path = GetArg(tool.Input, "TargetFile");
                var code = GetArg(tool.Input, "CodeContent");
                sb.AppendLine($"- **file**: `{path}`");
                if (code is not null)
                {
                    sb.AppendLine("```");
                    sb.AppendLine(code);
                    sb.AppendLine("```");
                }
                return;

            case "replace_file_content":
                var repPath = GetArg(tool.Input, "TargetFile");
                var target = GetArg(tool.Input, "TargetContent");
                var replacement = GetArg(tool.Input, "ReplacementContent");
                sb.AppendLine($"- **file**: `{repPath}`");
                if (target is not null || replacement is not null)
                {
                    sb.AppendLine("```diff");
                    if (target is not null)
                    {
                        foreach (var line in target.Split('\n')) sb.AppendLine($"-{line.TrimEnd()}");
                    }
                    if (replacement is not null)
                    {
                        foreach (var line in replacement.Split('\n')) sb.AppendLine($"+{line.TrimEnd()}");
                    }
                    sb.AppendLine("```");
                }
                return;

            case "manage_task":
                var action = GetArg(tool.Input, "Action");
                var taskId = GetArg(tool.Input, "TaskId");
                var input = GetArg(tool.Input, "Input");
                sb.AppendLine($"- **action**: {action}");
                if (taskId is not null) sb.AppendLine($"- **task**: {taskId}");
                if (input is not null)
                {
                    sb.AppendLine("```");
                    sb.AppendLine(input);
                    sb.AppendLine("```");
                }
                return;

            case "invoke_subagent":
                var subagents = GetArg(tool.Input, "Subagents");
                if (subagents is not null)
                {
                    sb.AppendLine("```json");
                    sb.AppendLine(subagents);
                    sb.AppendLine("```");
                }
                return;
        }

        // Generic fallback
        foreach (var (key, value) in relevantArgs)
        {
            var rendered = value switch
            {
                JsonElement elem => FormatJsonElement(elem),
                _ => value?.ToString() ?? ""
            };
            if (rendered.Contains('\n'))
            {
                sb.AppendLine($"- **{key}**:");
                sb.AppendLine("```");
                sb.AppendLine(rendered);
                sb.AppendLine("```");
            }
            else
            {
                sb.AppendLine($"- **{key}**: {rendered}");
            }
        }
    }

    private static string BuildFileName(AgSession session)
    {
        if (DateTime.TryParse(session.CreatedAt, out var dt))
        {
            var shortId = session.ConversationId.Length >= 8
                ? session.ConversationId[..8]
                : session.ConversationId;
            return $"{dt:yyyy-MM-dd}_{dt:HHmmss}_{shortId}.md";
        }
        return $"{session.ConversationId}.md";
    }

    private static string FormatTimestamp(string isoTimestamp)
    {
        if (DateTime.TryParse(isoTimestamp, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        return isoTimestamp;
    }

    private static string? GetArg(Dictionary<string, object>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var val))
            return null;
        return val switch
        {
            JsonElement { ValueKind: JsonValueKind.String } elem => elem.GetString(),
            JsonElement elem => elem.ToString(),
            string s => s,
            _ => val?.ToString()
        };
    }

    private static string FormatJsonElement(JsonElement elem) => elem.ValueKind switch
    {
        JsonValueKind.String => elem.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => elem.GetRawText(),
        _ => elem.ToString()
    };

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}