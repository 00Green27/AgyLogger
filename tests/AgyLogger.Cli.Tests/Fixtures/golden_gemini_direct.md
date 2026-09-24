<meta>

- **timestamp**: 2026-09-20T19:00:00.000Z
- **agent**: Antigravity CLI
- **wire format**: gemini
- **model**: gemini-2.5-pro
- **endpoint**: POST /v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse
- **upstream status**: 200

</meta>

<headers>

```
accept: text/event-stream
authorization: [REDACTED]
content-type: application/json
host: generativelanguage.googleapis.com
x-goog-api-key: [REDACTED]
```

</headers>

<request>

<params>

- **temperature**: 0
- **thinkingConfig**: {"includeThoughts":true}

</params>

<system-prompt>

You are the Antigravity CLI assistant.

</system-prompt>

<tools>

### list_directory

List files in a directory

```json
{
  "type": "object",
  "properties": {
    "path": {
      "type": "string"
    }
  },
  "required": [
    "path"
  ]
}
```

</tools>

<messages>

<message index="1" role="user">

list my files

</message>

<message index="2" role="model">

<tool-use name="list_directory" id="">

```json
{
  "path": "."
}
```

</tool-use>

</message>

<message index="3" role="user">

<tool-result name="list_directory" id="">

file1.txt
file2.txt

</tool-result>

</message>

</messages>

</request>

<response>

- **finish reason**: STOP
- **usage**: {"totalTokenCount":42}

<thinking>

Analyzing current workspace directory...

</thinking>

<assistant-text>

Here are the files in your directory.

</assistant-text>

</response>
