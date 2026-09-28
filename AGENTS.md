# Agent Instructions

## Core Principles

Prefer the smallest correct change that fully solves the task.

Preserve existing behavior unless the task explicitly requires a behavior change. Do not introduce abstractions, dependencies, configuration, or architectural layers for hypothetical future requirements.

Before editing, understand the affected execution flow, existing behavior, relevant tests, and callers/consumers of the code being changed.

Prefer existing code and standard .NET APIs over new abstractions or dependencies.

Make routine implementation decisions without asking for approval. Ask only when requirements are genuinely ambiguous or an action has effects outside the repository, such as publishing, destructive operations, real credentials, or external systems.

Do not change unrelated code.

## Workflow

Before editing:

1. Locate the relevant entry point, implementation, and tests.
2. Trace the affected flow and inspect callers/consumers.
3. Check existing tests for the behavior being changed.
4. Check whether existing code or standard .NET APIs already solve the problem.
5. Make the smallest change that satisfies the task.

For bug fixes, inspect all callers of the changed code and check affected sibling paths. Fix the shared root cause rather than only the reported symptom.

After editing:

1. Run focused tests for the affected behavior.
2. Run:

   ```text
   dotnet format --verify-no-changes
   dotnet build
   dotnet test
   ```

3. Inspect `git diff` and `git status`.
4. Review generated output when rendering behavior changed.
5. Review security-sensitive paths when proxy/header/certificate handling changed.
6. If something could not be verified, state what and why.

Do not ask for confirmation for routine implementation or verification steps. Ask before actions that affect external systems, real credentials, publishing, or destructive state.

## Project Constraints

The project targets .NET 10 and intentionally has a minimal dependency surface. `System.CommandLine` is the only runtime dependency.

Do not add a third-party dependency unless the standard library and existing project code cannot reasonably provide the required functionality.

Do not introduce Clean Architecture, CQRS, MediatR, DI frameworks, or additional layers unless the requirements genuinely justify them.

`Program.cs` is the composition root and should remain thin. CLI construction belongs in `Cli/CommandLineBuilder.cs`.

## Transcript Processing

AGY writes `transcript_full.jsonl` while Agy Logger may read it concurrently.

Treat filesystem notifications as hints, not reliable write boundaries. Code must tolerate duplicate notifications, incomplete writes, temporary file-access failures, and partially written final records.

An incomplete final JSONL record may be retryable. A malformed completed record should normally be diagnosed and skipped so subsequent records can still be processed.

Unknown JSON properties must not break parsing.

Prefer incremental processing; do not load an entire transcript into memory when streaming is sufficient.

Keep parsing separate from Markdown rendering.

AGY transcript files are read-only. Never modify the source transcript.

Resolve the user home directory with:

```csharp
Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
```

## Watch Mode

`FileSystemWatcher` events are hints and may be duplicated or arrive before a file is completely written.

Use debouncing/retry where necessary.

`TranscriptWatcher` coordinates detection and processing but must not become a JSON parser or Markdown renderer.

Long-running watch operations must support cancellation.

## Markdown Output

Generated Markdown must be deterministic and preserve the meaningful agent timeline, including user input, model responses, tool calls, tool results, and errors.

Transcript content must be escaped or formatted so it cannot accidentally alter the Markdown structure.

Do not expose raw JSON unless it is useful for understanding an event.

Generated output goes under:

```text
./.agylogs/
```

## Proxy and Security

The proxy handles live HTTP/HTTPS traffic to LLM APIs. Treat intercepted traffic and transcripts derived from it as sensitive.

Never log, print, or render credential-bearing headers or secrets. At minimum redact:

- `Authorization`;
- `x-api-key`;
- cookies;
- bearer/session tokens;
- equivalent authentication material.

Redaction must happen before sensitive data reaches logs, console output, generated Markdown, or test fixtures.

Do not introduce a new unredacted persistence location for API keys, tokens, or session secrets.

Assume intercepted request/response bodies may contain prompts, file contents, proprietary source code, or other sensitive user data.

Do not add telemetry, analytics, or outbound network calls that transmit intercepted data.

The MITM CA private key (`ca.pfx` under `~/.agylogs/ca/`) must be created with owner-only permissions on Unix. Never weaken them.

If a change touches header handling, certificate generation, or the proxy request/response pipeline, mention this explicitly in the commit message or PR description.

When uncertain whether intercepted data is sensitive, treat it as sensitive.

## Architecture Invariants

The project has two main subsystems: transcript processing and proxy interception.

Keep them independent.

`CompositeRunner` is the intended integration point when the `run` command needs both subsystems. Do not introduce additional cross-subsystem coupling unless the requirements genuinely require it.

Keep application logic out of `Program.cs`.

Keep parsing, rendering, filesystem watching, and proxy wire handling as separate responsibilities.

Do not create abstractions merely to move code between files.

## Testing

Test observable behavior, not implementation details.

Prefer real `System.Text.Json`, filesystem behavior, and temporary directories over mocks when practical.

Prioritize tests around behavior that can fail because of concurrency, external input, or security boundaries:

- malformed and incomplete JSONL records;
- unknown properties and empty lines;
- ordering preservation;
- Markdown escaping and rendering;
- missing files/directories;
- concurrent file access;
- duplicate watcher notifications;
- cancellation;
- credential/header redaction.

Do not add tests merely for coverage.

Do not change the process-wide current directory in tests because tests may execute concurrently.

For generated Markdown, prefer golden/snapshot tests when the complete output is a meaningful contract. Review snapshot diffs before updating them.

## Code Quality

Do not suppress compiler warnings or analyzers. Fix the underlying issue.

Do not make parameters optional merely to avoid updating callers. Optional parameters require a meaningful semantic default.

Avoid unnecessary LINQ when a simple loop is clearer or avoids allocations.

Do not optimize prematurely.

Avoid magic values when their meaning is not obvious at the call site.

Keep hand-written source files reasonably focused. Split a file when it contains genuinely unrelated responsibilities, not merely because it is long.

Use `.editorconfig` as the source of truth for formatting and C# style. Do not duplicate its rules here.

## Git

Keep commits scoped to one logical change.

Write commit messages and PR descriptions in the imperative mood and explain what the change does and why.

Squash exploratory/fixup commits before finishing unless granular history is explicitly requested.

## Definition of Done

A task is complete when:

- the requested behavior is implemented;
- relevant tests pass;
- `dotnet format --verify-no-changes` passes;
- `dotnet build` passes;
- `dotnet test` passes;
- the diff contains only intentional changes;
- generated output was reviewed when applicable;
- security-sensitive changes were reviewed when applicable.

If something could not be verified, report it explicitly instead of presenting the task as fully verified.
