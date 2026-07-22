# Practical OpenAI with LangChain

A minimal Python project managed with [uv](https://docs.astral.sh/uv/) that sends a
prompt to an OpenAI chat model through LangChain.

## Setup

Install `uv`, then run:

```powershell
uv sync
Copy-Item .env.example .env
```

Add your OpenAI API key to `.env`, then start the example:

To route requests through another OpenAI-compatible endpoint, set its API base URL
in `.env` (include the provider's `/v1` path when required):

```dotenv
OPENAI_BASE_URL=https://your-endpoint.example.com/v1
```

Then start the example:

```powershell
uv run practical-openai "Explain embeddings in one sentence."
```

If no prompt is supplied, the app uses a small default prompt.

## Development

```powershell
uv run ruff check .
uv run pytest
```
