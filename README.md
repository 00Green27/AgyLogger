# AgyLogger

A lightweight, dependency-free .NET CLI tool that reads Antigravity CLI (`agy`) transcript files and renders agent sessions as readable Markdown documents. It also functions as a full HTTP/HTTPS Reverse Proxy and Forward MitM Proxy to intercept LLM API traffic directly from the CLI.

## Installation

Run the project directly from the source code:

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

Commands:
  run <args>         Wrap AGY CLI execution and auto-sync in the background
  proxy              Start the MitM HTTP/HTTPS intercepting proxy
  ca                 Manage the local Certificate Authority for MitM interception
  sync               Sync transcripts to Markdown files
  list               List all available sessions
  watch              Watch for new/updated sessions and auto-sync
```

### 1. HTTP/HTTPS Interception Engine

AgyLogger can act as a local proxy to intercept traffic between `agy` and upstream LLM providers (e.g., Gemini, OpenAI, Anthropic). It supports both reverse proxying and forward MitM HTTPS proxying (via `CONNECT` tunnels) using a dynamically generated RSA 2048-bit Certificate Authority.

**Start the proxy on the default port (8888):**
```bash
AgyLogger proxy
```

### 2. Trusting the Certificate Authority

To intercept HTTPS traffic, Node.js (which powers `agy`) must trust the proxy's local CA. Node.js ignores OS certificate stores on Windows and macOS, so you must pass the certificate explicitly.

**Export the CA certificate:**
```bash
AgyLogger ca export --out ca.crt
```

**Configure Node.js and Proxy Environment Variables (Bash/Zsh):**
```bash
export HTTP_PROXY="http://127.0.0.1:8888"
export HTTPS_PROXY="http://127.0.0.1:8888"
export NODE_EXTRA_CA_CERTS="/absolute/path/to/ca.crt"
```

**Configure for Windows Command Prompt:**
```cmd
set "HTTP_PROXY=http://127.0.0.1:8888"
set "HTTPS_PROXY=http://127.0.0.1:8888"
set "NODE_EXTRA_CA_CERTS=C:\absolute\path\to\ca.crt"
```

### 3. Log Output Format

Logs are saved to `.agylogs/requests/` as Markdown files. The format uses structured XML tags to avoid collisions with Markdown content inside the model responses:

- `<meta>`: Request metadata (timestamp, model, endpoint, upstream status).
- `<headers>`: Redacted request and response headers.
- `<request>`: The exact prompt/request payload.
- `<response>`: The reconstructed model response, including text and tool calls.

```markdown
<meta>
Timestamp: 2026-09-24T12:00:00Z
Endpoint: api.gemini.com/v1/generateContent
</meta>

<headers>
[REDACTED]
</headers>

<request>
What is 2+2?
</request>

<response>
4
</response>
```

### 4. Legacy Transcript Reader Commands

- `AgyLogger list`: List all logged `agy` sessions.
- `AgyLogger sync`: Render transcripts to Markdown in `.agylogs/`.
- `AgyLogger watch`: Monitor transcripts and re-render on changes.
- `AgyLogger run -- agy <cmd>`: Wrap execution and sync logs automatically.
