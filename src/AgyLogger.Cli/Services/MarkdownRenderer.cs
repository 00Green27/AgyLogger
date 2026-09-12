using System.Text;
using System.Text.Json;

using AgyLogger.Cli.Models;

namespace AgyLogger.Cli.Services;

/// <summary>
/// Renders parsed AGY sessions as readable Markdown documents.
/// </summary>
public static class MarkdownRenderer
{
    private const string OutputSubDir = "logs";

    /// <summary>
    /// Renders a session to a Markdown file in the output directory.
    /// Returns the path of the written file.
    /// </summary>
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

    /// <summary>
    /// Renders a session as a Markdown string.
    /// </summary>
    public static string RenderMarkdown(AgSession session, List<Exchange> exchanges)
    {
        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"# Antigravity CLI Session");
        sb.AppendLine();
        sb.AppendLine($"**Session ID:** `{session.ConversationId}`");
        if (!string.IsNullOrEmpty(session.Model))
            sb.AppendLine($"**Model:** {session.Model}");
        if (!string.IsNullOrEmpty(session.Workspace))
            sb.AppendLine($"**Workspace:** `{session.Workspace}`");
        sb.AppendLine($"**Created:** {FormatTimestamp(session.CreatedAt)}");
        sb.AppendLine($"**Updated:** {FormatTimestamp(session.UpdatedAt)}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Exchanges
        for (var i = 0; i < exchanges.Count; i++)
        {
            var exchange = exchanges[i];
            sb.AppendLine($"## Turn {i + 1}");
            sb.AppendLine();

            foreach (var msg in exchange.Messages)
            {
                switch (msg.Role)
                {
                    case "user":
                        RenderUserMessage(sb, msg);
                        break;
                    case "assistant":
                        RenderAssistantMessage(sb, msg);
                        break;
                    case "tool":
                        RenderToolMessage(sb, msg);
                        break;
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void RenderUserMessage(StringBuilder sb, ExchangeMessage msg)
    {
        sb.AppendLine("### 👤 User");
        sb.AppendLine();
        sb.AppendLine(msg.Text ?? "");
        sb.AppendLine();
    }

    private static void RenderAssistantMessage(StringBuilder sb, ExchangeMessage msg)
    {
        // Thinking (collapsed)
        if (!string.IsNullOrEmpty(msg.Thinking))
        {
            sb.AppendLine("<details>");
            sb.AppendLine("<summary>🧠 Thinking</summary>");
            sb.AppendLine();
            sb.AppendLine(msg.Thinking);
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        // Response text
        if (!string.IsNullOrEmpty(msg.Text))
        {
            sb.AppendLine("### 🤖 Assistant");
            sb.AppendLine();
            sb.AppendLine(msg.Text);
            sb.AppendLine();
        }
    }

    private static void RenderToolMessage(StringBuilder sb, ExchangeMessage msg)
    {
        if (msg.Tool is null) return;

        var tool = msg.Tool;
        var summary = tool.ToolSummary ?? tool.ToolAction ?? tool.Name;

        sb.AppendLine("<details>");
        sb.AppendLine($"<summary>🔧 Tool: {EscapeHtml(summary)} ({EscapeHtml(tool.Name)})</summary>");
        sb.AppendLine();

        // Render input
        RenderToolInput(sb, tool);

        // Render output
        if (!string.IsNullOrEmpty(tool.Output))
        {
            sb.AppendLine("**Output:**");
            sb.AppendLine();
            var output = tool.Output.Trim();
            if (output.Length > 2000)
                output = output[..2000] + "\n... (truncated)";
            sb.AppendLine("```");
            sb.AppendLine(output);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("</details>");
        sb.AppendLine();
    }

    private static void RenderToolInput(StringBuilder sb, ToolInfo tool)
    {
        if (tool.Input is null || tool.Input.Count == 0)
            return;

        // Filter out meta args
        var relevantArgs = tool.Input
            .Where(kv => kv.Key is not ("toolAction" or "toolSummary"))
            .ToList();

        if (relevantArgs.Count == 0)
            return;

        // Specialized renderers for common tools
        switch (tool.Name)
        {
            case "run_command":
                RenderCommandInput(sb, tool.Input);
                return;
            case "view_file":
                RenderViewFileInput(sb, tool.Input);
                return;
            case "grep_search":
                RenderGrepInput(sb, tool.Input);
                return;
            case "write_to_file":
                RenderWriteFileInput(sb, tool.Input);
                return;
            case "replace_file_content":
                RenderReplaceInput(sb, tool.Input);
                return;
            case "list_dir":
                RenderListDirInput(sb, tool.Input);
                return;
            case "find_by_name":
                RenderFindInput(sb, tool.Input);
                return;
            case "read_url_content":
                RenderUrlInput(sb, tool.Input);
                return;
            case "search_web":
                RenderSearchWebInput(sb, tool.Input);
                return;
        }

        // Generic fallback: show all args as key-value
        foreach (var (key, value) in relevantArgs)
        {
            var rendered = value switch
            {
                JsonElement elem => FormatJsonElement(elem),
                _ => value?.ToString() ?? ""
            };
            sb.AppendLine($"- **{key}:** {rendered}");
        }
        sb.AppendLine();
    }

    private static void RenderCommandInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var cmd = GetArg(args, "CommandLine");
        var cwd = GetArg(args, "Cwd");
        if (cwd is not null)
            sb.AppendLine($"**Directory:** `{cwd}`");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine(cmd ?? "(unknown)");
        sb.AppendLine("```");
        sb.AppendLine();
    }

    private static void RenderViewFileInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var path = GetArg(args, "AbsolutePath");
        var start = GetArg(args, "StartLine");
        var end = GetArg(args, "EndLine");
        sb.Append($"**File:** `{path}`");
        if (start is not null || end is not null)
            sb.Append($" (lines {start ?? "1"}–{end ?? "end"})");
        sb.AppendLine();
        sb.AppendLine();
    }

    private static void RenderGrepInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var query = GetArg(args, "Query");
        var searchPath = GetArg(args, "SearchPath");
        var includes = GetArg(args, "Includes");
        sb.AppendLine($"**Query:** `{query}`");
        sb.AppendLine($"**Path:** `{searchPath}`");
        if (includes is not null)
            sb.AppendLine($"**Includes:** {includes}");
        sb.AppendLine();
    }

    private static void RenderWriteFileInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var path = GetArg(args, "TargetFile");
        var desc = GetArg(args, "Description");
        sb.AppendLine($"**File:** `{path}`");
        if (desc is not null)
            sb.AppendLine($"**Description:** {desc}");
        sb.AppendLine();
    }

    private static void RenderReplaceInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var path = GetArg(args, "TargetFile");
        var instruction = GetArg(args, "Instruction");
        var desc = GetArg(args, "Description");
        sb.AppendLine($"**File:** `{path}`");
        if (instruction is not null)
            sb.AppendLine($"**Instruction:** {instruction}");
        if (desc is not null)
            sb.AppendLine($"**Description:** {desc}");
        sb.AppendLine();
    }

    private static void RenderListDirInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var path = GetArg(args, "DirectoryPath");
        sb.AppendLine($"**Directory:** `{path}`");
        sb.AppendLine();
    }

    private static void RenderFindInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var pattern = GetArg(args, "Pattern");
        var dir = GetArg(args, "SearchDirectory");
        sb.AppendLine($"**Pattern:** `{pattern}`");
        sb.AppendLine($"**Directory:** `{dir}`");
        sb.AppendLine();
    }

    private static void RenderUrlInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var url = GetArg(args, "Url");
        sb.AppendLine($"**URL:** {url}");
        sb.AppendLine();
    }

    private static void RenderSearchWebInput(StringBuilder sb, Dictionary<string, object> args)
    {
        var query = GetArg(args, "query");
        sb.AppendLine($"**Query:** {query}");
        sb.AppendLine();
    }

    private static string BuildFileName(AgSession session)
    {
        // Format: YYYY-MM-DD_HHmmss_<short-id>.md
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
        _ => $"`{elem.GetRawText()}`"
    };

    private static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
