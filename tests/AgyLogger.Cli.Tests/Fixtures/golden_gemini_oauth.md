<meta>

- **timestamp**: 2026-09-20T19:00:00.000Z
- **agent**: Antigravity CLI
- **wire format**: gemini
- **model**: gemini-2.5-pro
- **endpoint**: POST /v1internal:streamGenerateContent
- **upstream status**: 200

</meta>

<headers>

```
accept: text/event-stream
authorization: [REDACTED]
content-type: application/json
host: cloudcode-pa.googleapis.com
x-goog-api-key: [REDACTED]
```

</headers>

<request>

<params>

- **temperature**: 0.2

</params>

<system-prompt>

You are the Antigravity CLI assistant running in Code Assist mode.

</system-prompt>

<tools>

### view_file

View the contents of a file

```json
{
  "type": "object",
  "properties": {
    "AbsolutePath": {
      "type": "string"
    }
  }
}
```

</tools>

<messages>

<message index="1" role="user">

refactor the test harness

</message>

</messages>

</request>

<response>

- **finish reason**: STOP
- **usage**: {"totalTokenCount":88}

<thinking>

Inspecting test harness structure...

</thinking>

<tool-use name="view_file" id="">

```json
{
  "AbsolutePath": "tests/Harness.cs"
}
```

</tool-use>

</response>
