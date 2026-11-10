# Chat MCP: create jobs from a description

Two entry points, one tool set `CadTools` (`be/Cadflow.Api/Mcp/CadTools.cs`):

| Entry point | Use when | How a job is created |
|---|---|---|
| Web `#/chat` -> `POST /api/chat` (SSE) | The user describes it in the browser | Default (`Chat:AutoCreate=true`): the LLM calls `validate_job` then `create_job`, the job enters the queue right away, and the UI shows a job card. With `Chat__AutoCreate=false`: the LLM only **proposes** (`propose_job`) and the job enters the queue only when the user presses **Create job** (`POST /api/chat/confirm/{draftId}`, a single-use draft that expires after 30 minutes) |
| MCP `/mcp` (HTTP) | Claude Desktop, MCP Inspector, other clients | A normal `create_job` tool; the client asks the user for permission itself |

Tools: `list_products`, `get_product_schema`, `list_configs`, `validate_job`, `create_job`, `import_gea`, `get_script_form`, `get_job`, `list_jobs`, `cancel_job`. Units are always mm, degrees, integers.

## Configuration

Set it in `be/Cadflow.Api/.env` (ignored by git; a sample is in `.env.example`; `KEY__SUB=value` maps to `KEY:SUB`; real environment variables take precedence over `.env`). `dotnet user-secrets` and `ANTHROPIC_API_KEY` still work.

```
Anthropic__ApiKey=<key>
Anthropic__Model=claude-sonnet-5-5          # optional
# An Anthropic-compatible provider, for example DeepSeek:
# Anthropic__BaseUrl=https://api.deepseek.com/anthropic
# Anthropic__Model=deepseek-chat
# Chat__AutoCreate=false                    # go back to the wait-for-confirmation mode
# Mcp__Key=<key>                           # the key for /mcp (header X-Api-Key)
```

- `Anthropic:ApiKey` not set: `/api/chat` returns 503, the rest keeps working.
- `Mcp:Key` not set: `/mcp` accepts connections only from the server machine itself (loopback). When exposing it to the network, set a key.
- `/api/chat` and `/api/chat/confirm` are limited to 20 requests/minute/IP. The user API has no authentication yet; add it before going public.
- In auto-create mode, the LLM creates jobs without asking again; the server still re-checks the rules when creating, but a vague description can produce parameters that are not what you meant. Review the parameters on the job page.
- Jobs are stored in PostgreSQL, see `database.md`.

## Quick test

```bash
npx @modelcontextprotocol/inspector   # URL: http://localhost:18080/mcp
```

Example: `validate_job` with `{"code":"gas-turbine","values":{"GT_CanCount":6}}` reports "Can count must be a multiple of 4".
