# AgyLogger

A lightweight, dependency-free (mostly) .NET CLI tool that reads Antigravity CLI (`agy`) transcript files (`transcript_full.jsonl`) and renders agent sessions as readable Markdown documents.

## Motivation

When working with the Antigravity CLI, agent sessions and actions are logged in JSONL format. `AgyLogger` provides an easy way to view these transcripts as nicely formatted Markdown files, making it simple to review agent behavior, tool calls, and model reasoning.

## Installation

You can run the project directly from the source code:

```bash
cd src/AgyLogger.Cli
dotnet run -- --help
```

Or build the executable:

```bash
dotnet build -c Release
```

## Usage

```text
Usage:
  AgyLogger.Cli [command] [options]

Options:
  --version       Show version information
  -?, -h, --help  Show help and usage information

Commands:
  list               List all available sessions
  sync <session-id>  Sync sessions to Markdown files
  watch              Watch for new/updated sessions and auto-sync
  run <args>         Wrap AGY CLI execution and auto-sync in the background
```

### Examples

**List all available AGY sessions:**
```bash
AgyLogger list
```

**Sync all sessions to Markdown (outputs to `logs/` by default):**
```bash
AgyLogger sync
```

**Sync a specific session:**
```bash
AgyLogger sync <session-id>
```

**Run an AGY agent and automatically sync transcripts in the background:**
```bash
# Important: use '--' before passing arguments to the agy process
AgyLogger run -o my_logs -- -p "my prompt"
```

**Watch the AGY brain directory for changes and continuously sync transcripts:**
```bash
AgyLogger watch
```

## Architecture

- **CLI (`System.CommandLine`)**: Handles commands and options.
- **TranscriptDiscovery**: Finds AGY conversation logs in the user's `~/.gemini/antigravity-cli/brain/` folder.
- **TranscriptReader**: Incrementally reads and parses the raw `JSONL` outputs, resilient to partial writes and incomplete records.
- **MarkdownRenderer**: Deterministically transforms the JSON interactions into a clean Markdown timeline.
- **TranscriptWatcher**: Uses `FileSystemWatcher` to monitor active transcripts and coordinates updates.

## Development Rules

Check out [`AGENTS.md`](./AGENTS.md) for detailed guidelines on the project's technical decisions, architecture, and coding standards.
