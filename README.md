# DARCI-Live

This repository contains multiple DARCI generations, but the current live app is:

- `Start-DARCI.ps1`
- `DARCI-v4/Darci.Api`

The older `DARCI-v3` folder still has useful historical notes, but its startup instructions are not the current live path.

## Dependencies

- `.NET 8 SDK`
- `Ollama`
- Ollama models:
  - `gemma2:9b`
  - `qwen2.5-coder:7b`
  - `nomic-embed-text`

## Environment Preflight

Before a focused coding session, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\Test-DARCIEnvironment.ps1
```

To include a solution build in the same check:

```powershell
powershell -ExecutionPolicy Bypass -File .\Test-DARCIEnvironment.ps1 -Build
```

## Fastest Startup on Windows

Open PowerShell in the repo root and run:

```powershell
ollama pull gemma2:9b
ollama pull qwen2.5-coder:7b
ollama pull nomic-embed-text
ollama serve
powershell -ExecutionPolicy Bypass -File .\Start-DARCI.ps1
```

Notes:

- If Ollama is already running, `ollama serve` may say the socket is already in use. That is fine.
- `Start-DARCI.ps1` starts the current DARCI v4 API and opens the web UI automatically.
- If you do not want the browser to auto-open, run `powershell -ExecutionPolicy Bypass -File .\Start-DARCI.ps1 -NoBrowser`.

## URLs

Once DARCI is up:

- Web UI: `http://localhost:5081/app/`
- Swagger: `http://localhost:5081/swagger`
- Status: `http://localhost:5081/status`

## Local Configuration

For local secrets and optional integrations, copy `.env.local.example` to `.env.local` and fill in only what you need. DARCI loads `.env.local` automatically when `Darci.Api` starts.

The core app runs without research keys, but live/deep research should use:

- `DARCI_TAVILY_API_KEY`
- `DARCI_FIRECRAWL_API_KEY`
- `DARCI_FIRECRAWL_ENABLED=true`

The default database path is `DARCI-v4/Data/darci.db`. Override it with `DARCI_DB_PATH` if you want to move runtime memory elsewhere.

## Manual Startup Fallback

If the PowerShell launcher is not convenient, you can run the API directly:

```powershell
cd DARCI-v4\Darci.Api
dotnet run --no-launch-profile -- --urls http://localhost:5081
```

Then open `http://localhost:5081/app/`.

## What Is Optional

The core local DARCI demo does not require any of the following:

- AWS / S3 / SQS
- Telegram
- SMTP email
- Tavily / Firecrawl
- Optional NLP adapter
- Python CAD or engineering services
- `.env.local`
- `.env.engineering.local`

Those integrations are only needed if you want to demo extra cloud, research, or engineering features.

## Optional Engineering Setup

If you want to try CAD generation or engineering routes that depend on the Python service, start it separately:

```powershell
cd DARCI-v4\Darci.Python
pip install -r requirements.txt
uvicorn main:app --host 127.0.0.1 --port 8000
```

## Coding Workspace First Pass

DARCI v4 now has a first-pass coding workspace API. It is infrastructure for DARCI v5's programming loop: import a project folder, scan a safe file manifest, build a context package, run allowlisted build/test commands inside the workspace, and track coding task records.

Suggested local project location:

```powershell
mkdir DARCI-v4\Workspaces
```

Primary endpoints:

- `POST /coding/workspaces/import`
- `GET /coding/workspaces`
- `GET /coding/workspaces/{id}/files`
- `GET /coding/workspaces/{id}/context?query=...`
- `POST /coding/workspaces/{id}/commands`
- `GET /coding/workspaces/{id}/commands`
- `POST /coding/tasks`
- `GET /coding/tasks/{id}`

The command runner is intentionally allowlisted in this first pass. It supports common read/build/test commands such as `dotnet build`, `dotnet test`, `npm test`, `npm run build`, `python -m pytest`, `cargo test`, `go test`, and read-only `git status/diff/log`.

Implementation notes and next steps are kept in `DARCI_CODING_ENVIRONMENT_LOG.md`.

## Troubleshooting

- If the app says Ollama is unreachable, make sure Ollama is running on `http://localhost:11434`.
- If the browser does not open automatically, go to `http://localhost:5081/app/` manually.
- If PowerShell blocks the script, use the explicit `-ExecutionPolicy Bypass` command shown above.
- If you are only trying to show the live app, ignore the older `DARCI-v3` startup docs.
