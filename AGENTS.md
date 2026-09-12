# Agent Instructions

## Project Overview

Agy Logger is a lightweight .NET CLI that reads Antigravity CLI (`agy`) transcript files (`transcript_full.jsonl`) and renders agent sessions as readable Markdown documents.

The project is intentionally small and dependency-free. Prefer simple, idiomatic .NET solutions over introducing abstractions, frameworks, or dependencies without a clear benefit.

## Repository Structure

```text
src/
└── AgyLogger.Cli/
    ├── Models/
    ├── Services/
    └── Program.cs

tests/
└── AgyLogger.Cli.Tests/
```

`Models/` contains strongly typed representations of the relevant transcript data.

`Services/` contains application logic:

* `TranscriptDiscovery` — discovers AGY conversations and transcript files.
* `TranscriptReader` — incrementally reads and parses JSONL.
* `MarkdownRenderer` — renders transcript entries as Markdown.
* `TranscriptWatcher` — monitors active transcripts for changes.

`Program.cs` is the composition root and CLI entry point. Keep application logic out of it.

## Technology

* .NET 10
* Modern C#
* Nullable reference types enabled
* `System.Text.Json`
* `IAsyncEnumerable<T>`
* `FileSystemWatcher`
* No third-party runtime dependencies

Use built-in .NET APIs whenever they are sufficient.

## General Engineering Rules

Make only high-confidence changes. Prefer the smallest change that correctly solves the problem.

Do not introduce abstractions, projects, packages, or configuration solely for theoretical future requirements.

Do not change unrelated code while implementing a feature or fixing a bug.

Do not modify generated files unless the task explicitly requires it.

Do not suppress compiler warnings or analyzers to make the build pass. Fix the underlying issue.

Do not make parameters optional merely to avoid updating call sites. A parameter should be optional only when it has a meaningful semantic default.

Preserve existing behavior unless the task explicitly requires a behavior change.

Before changing an existing implementation, understand its current behavior and verify that the change is actually necessary.

## C# Style

Follow the repository `.editorconfig` when present.

Prefer:

* file-scoped namespaces;
* nullable reference types;
* `is null` / `is not null`;
* pattern matching and switch expressions where they improve clarity;
* `nameof` instead of member-name string literals;
* `CancellationToken` for asynchronous and potentially long-running operations;
* `IAsyncEnumerable<T>` for streaming transcript processing;
* `await using` and `using` for deterministic resource ownership.

Avoid unnecessary LINQ when a simple loop is clearer or avoids unnecessary allocations.

Do not optimize prematurely. Optimize only when the behavior or data volume justifies it.

## Transcript Parsing

The source format is JSON Lines: each non-empty line represents an independent JSON object.

The transcript is written by AGY while the logger may be reading it. Code must therefore tolerate:

* incomplete final lines;
* partially written records;
* duplicate filesystem change notifications;
* transient file access failures;
* unknown JSON properties;
* malformed individual records.

A malformed record should not normally prevent subsequent records from being processed.

Do not load an entire transcript into memory when incremental processing is sufficient.

Keep parsing concerns isolated from rendering concerns.

When parsing loosely structured external data, comments should explain important format assumptions and edge cases rather than merely restating the code.

For example:

```csharp
// AGY may append the record while we are reading the file, so the final
// line can be incomplete. Treat an incomplete final record as retryable
// instead of failing the whole transcript.
```

## AGY Paths

AGY transcripts are located under:

```text
~/.gemini/antigravity-cli/brain/<conversation-id>/.system_generated/logs/transcript_full.jsonl
```

Resolve the user home directory using:

```csharp
Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
```

Do not hard-code platform-specific home-directory paths.

Treat transcript files as read-only. Never modify the AGY source files.

## Watch Mode

`FileSystemWatcher` notifications are hints that the file changed, not a reliable one-to-one representation of writes.

The implementation must account for:

* multiple events for one write;
* events arriving before the file is completely written;
* temporary sharing/locking failures;
* partial final lines.

Use debouncing where appropriate.

`TranscriptWatcher` should detect changes and coordinate processing. It should not contain JSON parsing or Markdown rendering logic.

All long-running watch operations must support cancellation.

## Markdown Rendering

The renderer should produce deterministic Markdown.

Preserve the meaningful agent timeline, including user input, model responses, tool calls, tool results, errors, and other relevant transcript events.

Escape or format content appropriately so transcript data cannot accidentally change the intended Markdown structure.

Do not expose raw JSON unless it is useful for understanding an event.

Generated files are written to:

```text
./.agylogs/
```

Output filenames must be safe on the current operating system.

## Testing

Tests should verify observable behavior rather than implementation details.

Prioritize tests for:

* valid transcript records;
* unknown properties;
* malformed records;
* empty lines;
* incomplete final lines;
* ordering preservation;
* Markdown rendering;
* Markdown escaping;
* missing AGY directories;
* missing transcript files;
* concurrent file access;
* duplicate watcher notifications;
* cancellation.

Use real `System.Text.Json` and filesystem behavior in tests where practical.

Avoid mocks when a small in-memory or temporary filesystem-based test is simpler and more representative.

Do not change the process-wide current directory in tests because tests may execute concurrently.

## Generated Output Verification

Markdown is a generated artifact and should have strong regression coverage.

Prefer snapshot/golden-file testing for complete rendered Markdown rather than asserting a few individual substrings.

When changing the Markdown format:

1. Run the relevant tests.
2. Inspect the generated diff.
3. Verify that every changed section is intentional.
4. Update the expected snapshot only after reviewing the output.

A formatting change should not silently modify unrelated parts of the generated document.

## Build and Verification

After making code changes, verify the result rather than assuming compilation succeeds.

At minimum:

```text
dotnet build
dotnet test
```

For focused changes, run the relevant test project first, then run the full test suite when practical.

Before considering a change complete, verify:

1. The solution builds without warnings introduced by the change.
2. Relevant tests pass.
3. The full test suite passes when practical.
4. Generated Markdown output is reviewed when rendering behavior changed.
5. `git diff` contains only intentional changes.
6. No temporary files, debug output, credentials, or generated artifacts were accidentally added.

Do not use `--no-build` for tests unless the current binaries are known to correspond exactly to the current source tree.

If verification cannot be performed, state precisely what was not verified and why.

## Change Discipline

Keep changes scoped to the requested task.

Before finishing, inspect:

```text
git status
git diff
```

Do not revert or overwrite unrelated user changes.

Do not reformat unrelated files.

Do not add dependencies unless the standard library cannot reasonably provide the required functionality.

When a dependency appears necessary, first verify whether the requirement can be satisfied with existing .NET APIs.

## Comments

Comments should explain **why**, not what the code obviously does.

Good:

```csharp
// FileSystemWatcher can emit several events for a single append,
// so processing is debounced to avoid rendering the same transcript
// repeatedly.
```

Bad:

```csharp
// Create a timer.
var timer = new Timer(...);
```

For compatibility assumptions or behavior inferred from AGY's external format, document the assumption and, when possible, link to the relevant upstream documentation or issue.

Keep workaround comments next to the workaround and document the condition under which the workaround can be removed.

## Architecture

Keep the dependency flow simple:

```text
CLI
 │
 ├── TranscriptDiscovery
 │
 ├── TranscriptReader
 │       ↓
 │   TranscriptEntry
 │       ↓
 └── MarkdownRenderer

TranscriptWatcher
 └── coordinates re-processing when the transcript changes
```

Do not introduce Clean Architecture, CQRS, MediatR, dependency-injection frameworks, or additional layers unless the project's requirements genuinely justify them.

The primary design goal is a small, understandable CLI whose behavior can be verified easily.
