using System.Text.Json;
using System.Text.RegularExpressions;

using AgyLogger.Cli.Models;

namespace AgyLogger.Cli.Services;

/// <summary>
/// Parses transcript_full.jsonl files into structured AgSession objects.
/// </summary>
public static partial class TranscriptParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [GeneratedRegex(@"<USER_REQUEST>\s*(.*?)\s*</USER_REQUEST>", RegexOptions.Singleline)]
    private static partial Regex UserRequestRegex();

    [GeneratedRegex(@"<ADDITIONAL_METADATA>.*?</ADDITIONAL_METADATA>|<USER_SETTINGS_CHANGE>.*?</USER_SETTINGS_CHANGE>|<SYSTEM_MESSAGE>.*?</SYSTEM_MESSAGE>", RegexOptions.Singleline)]
    private static partial Regex MetadataBlockRegex();

    [GeneratedRegex(@"Model Selection.{0,4}from .+? to (.+?)\.(?:\s|$)")]
    private static partial Regex ModelRegex();

    /// <summary>
    /// Parses a transcript_full.jsonl file into an AgSession.
    /// </summary>
    public static AgSession Parse(string transcriptPath)
    {
        var conversationId = GetConversationId(transcriptPath);
        var steps = new List<TranscriptStep>();
        string? model = null;

        foreach (var line in File.ReadLines(transcriptPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var step = JsonSerializer.Deserialize<TranscriptStep>(line, JsonOptions);
                if (step is null)
                    continue;

                steps.Add(step);

                // Extract model from the first USER_SETTINGS_CHANGE block
                if (model is null && step.Type == StepType.UserInput && step.Content.Contains("USER_SETTINGS_CHANGE"))
                {
                    var match = ModelRegex().Match(step.Content);
                    if (match.Success)
                        model = match.Groups[1].Value.Trim();
                }
            }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"Warning: skipping malformed line in {transcriptPath}: {ex.Message}");
            }
        }

        // Sort by step_index (agy writes async results out of order)
        steps.Sort((a, b) => a.StepIndex.CompareTo(b.StepIndex));

        var session = new AgSession
        {
            ConversationId = conversationId,
            Steps = steps,
            TranscriptPath = transcriptPath,
            Model = model,
        };

        if (steps.Count > 0)
        {
            session.CreatedAt = steps[0].CreatedAt;
            session.UpdatedAt = steps[^1].CreatedAt;
        }

        // Try to infer workspace from tool call paths
        session.Workspace = InferWorkspace(steps);

        return session;
    }

    /// <summary>
    /// Builds exchanges from a parsed session.
    /// Each exchange starts with a USER_INPUT and contains all subsequent agent responses.
    /// </summary>
    public static List<Exchange> BuildExchanges(AgSession session)
    {
        var exchanges = new List<Exchange>();
        Exchange? current = null;

        void Flush()
        {
            if (current is { Messages.Count: > 0 })
                exchanges.Add(current);
        }

        // Track pending tool calls from PLANNER_RESPONSE to match with results
        var pendingTools = new Dictionary<int, ToolInfo>();
        var toolCallStepIndex = -1;
        foreach (var step in session.Steps)
        {
            switch (step)
            {
                case { Type: StepType.UserInput, Source: StepSource.UserExplicit }:
                    Flush();
                    current = new Exchange();
                    var userText = CleanUserPrompt(step.Content ?? "");
                    if (!string.IsNullOrWhiteSpace(userText))
                    {
                        current.Messages.Add(new ExchangeMessage
                        {
                            Role = "user",
                            Timestamp = step.CreatedAt,
                            Text = userText,
                            Status = step.Status,
                        });
                        current.StartTime = step.CreatedAt;
                        current.EndTime = step.CreatedAt;
                    }
                    break;

                case { Type: StepType.PlannerResponse }:
                    current ??= new Exchange();

                    // Add agent text/thinking message if present
                    var thinking = step.Thinking?.Trim();
                    var content = step.Content?.Trim();
                    if (!string.IsNullOrEmpty(thinking) || !string.IsNullOrEmpty(content))
                    {
                        current.Messages.Add(new ExchangeMessage
                        {
                            Role = "assistant",
                            Timestamp = step.CreatedAt,
                            Text = content,
                            Status = step.Status,
                            TruncatedFields = step.TruncatedFields,
                            Thinking = thinking,
                        });
                    }

                    // Add tool call messages
                    if (step.ToolCalls is { Count: > 0 })
                    {
                        toolCallStepIndex = step.StepIndex;
                        var resultStepIdx = step.StepIndex + 1;
                        foreach (var tc in step.ToolCalls)
                        {
                            var toolInfo = new ToolInfo
                            {
                                Name = tc.Name,
                                ToolAction = GetStringArg(tc.Args, "toolAction"),
                                ToolSummary = GetStringArg(tc.Args, "toolSummary"),
                                Input = tc.Args,
                            };
                            current.Messages.Add(new ExchangeMessage
                            {
                                Role = "tool",
                                Timestamp = step.CreatedAt,
                                Tool = toolInfo,
                                Status = step.Status,
                            });
                            pendingTools[resultStepIdx] = toolInfo;
                            resultStepIdx++;
                        }
                    }

                    current.EndTime = step.CreatedAt;
                    break;

                case var s when StepType.IsToolResult(s.Type):
                    // Attach result to the matching pending tool
                    if (current is not null)
                    {
                        if (pendingTools.TryGetValue(step.StepIndex, out var tool))
                        {
                            tool.Output = step.Content;
                            pendingTools.Remove(step.StepIndex);
                        }
                        else
                        {
                            // Fallback: attach to last tool message without output
                            var lastTool = current.Messages
                                .LastOrDefault(m => m.Role == "tool" && m.Tool?.Output is null);
                            if (lastTool?.Tool is not null)
                                lastTool.Tool.Output = step.Content;
                        }
                        current.EndTime = step.CreatedAt;
                    }
                    break;

                case { Type: StepType.ConversationHistory }:
                    // Skip history replays
                    continue;

                case { Type: StepType.SystemMessage }:
                    current ??= new Exchange();
                    current.Messages.Add(new ExchangeMessage
                    {
                        Role = "system",
                        Timestamp = step.CreatedAt,
                        Text = step.Content,
                    });
                    break;

                case { Type: StepType.Checkpoint }:
                    current ??= new Exchange();
                    current.Messages.Add(new ExchangeMessage
                    {
                        Role = "checkpoint",
                        Timestamp = step.CreatedAt,
                        Text = step.Content,
                    });
                    break;

                case { Type: StepType.ErrorMessage }:
                    current ??= new Exchange();
                    current.Messages.Add(new ExchangeMessage
                    {
                        Role = "error",
                        Timestamp = step.CreatedAt,
                        Text = step.Content,
                    });
                    break;

                case { Type: StepType.UserInput }:
                    // Non-explicit user input (replayed/synthesized) — skip
                    continue;
            }
        }

        Flush();

        // Assign exchange IDs
        for (var i = 0; i < exchanges.Count; i++)
        {
            exchanges[i].ExchangeId = $"{session.ConversationId}:{i}";
            if (string.IsNullOrEmpty(exchanges[i].StartTime))
                exchanges[i].StartTime = session.CreatedAt;
            if (string.IsNullOrEmpty(exchanges[i].EndTime))
                exchanges[i].EndTime = exchanges[i].StartTime;
        }

        return exchanges;
    }

    /// <summary>
    /// Cleans a user prompt: extracts content from USER_REQUEST wrapper,
    /// strips metadata blocks.
    /// </summary>
    public static string CleanUserPrompt(string raw)
    {
        // Extract from <USER_REQUEST> wrapper if present
        var match = UserRequestRegex().Match(raw);
        var text = match.Success ? match.Groups[1].Value : raw;

        // Strip metadata blocks
        text = MetadataBlockRegex().Replace(text, "");

        return text.Trim();
    }

    private static string GetConversationId(string transcriptPath)
    {
        // Path: .../brain/<conv-id>/.system_generated/logs/transcript_full.jsonl
        var dir = Path.GetDirectoryName(transcriptPath) ?? "";
        // Go up: logs -> .system_generated -> <conv-id>
        var systemGen = Path.GetDirectoryName(dir) ?? "";
        var convDir = Path.GetDirectoryName(systemGen) ?? "";
        return Path.GetFileName(convDir);
    }

    private static string? InferWorkspace(List<TranscriptStep> steps)
    {
        string[] pathArgKeys = ["Cwd", "AbsolutePath", "TargetFile", "SearchPath", "DirectoryPath", "SearchDirectory"];

        foreach (var step in steps)
        {
            if (step.ToolCalls is null) continue;
            foreach (var tc in step.ToolCalls)
            {
                if (tc.Args is null) continue;
                foreach (var key in pathArgKeys)
                {
                    if (tc.Args.TryGetValue(key, out var val) && val is JsonElement elem
                        && elem.ValueKind == JsonValueKind.String)
                    {
                        var path = elem.GetString();
                        if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path))
                        {
                            // Return the root project directory (heuristic: first 3 segments)
                            return ExtractProjectRoot(path);
                        }
                    }
                }
            }
        }

        return null;
    }

    private static string ExtractProjectRoot(string absolutePath)
    {
        // Simple heuristic: walk up until we find a reasonable root
        // For paths like D:\Dev\personal\Project\src\file.cs -> D:\Dev\personal\Project
        var dir = Path.GetDirectoryName(absolutePath) ?? absolutePath;
        while (!string.IsNullOrEmpty(dir))
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    // Check if this looks like a project root (has .git, .csproj, etc.)
                    if (Directory.Exists(Path.Combine(dir, ".git")) ||
                        Directory.EnumerateFiles(dir, "*.csproj").Any() ||
                        Directory.EnumerateFiles(dir, "*.slnx").Any() ||
                        Directory.EnumerateFiles(dir, "*.sln").Any() ||
                        File.Exists(Path.Combine(dir, "package.json")))
                    {
                        return dir;
                    }
                }
            }
            catch (Exception)
            {
                // Ignore I/O errors (e.g., unauthorized access, deleted directories)
            }

            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break; // reached root
            dir = parent;
        }
        return Path.GetDirectoryName(absolutePath) ?? absolutePath;
    }

    private static string? GetStringArg(Dictionary<string, object>? args, string key)
    {
        if (args is null) return null;
        if (!args.TryGetValue(key, out var val)) return null;
        return val switch
        {
            JsonElement { ValueKind: JsonValueKind.String } elem => elem.GetString(),
            string s => s,
            _ => val.ToString()
        };
    }
}