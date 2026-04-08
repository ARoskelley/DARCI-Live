# DARCI-Live

This repository contains multiple DARCI generations, but the current live app is:

- `Start-DARCI.ps1`
- `DARCI-v4/Darci.Api`

The older `DARCI-v3` folder still has useful historical notes, but its startup instructions are not the current live path.

## Dependencies

- `.NET 8 SDK`
- `Ollama`
- Ollama models:
  - `gemma4:e4b`
  - `nomic-embed-text`

## Fastest Startup on Windows

Open PowerShell in the repo root and run:

```powershell
ollama pull gemma4:e4b
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
- Lizzy NLP
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

## Troubleshooting

- If the app says Ollama is unreachable, make sure Ollama is running on `http://localhost:11434`.
- If the browser does not open automatically, go to `http://localhost:5081/app/` manually.
- If PowerShell blocks the script, use the explicit `-ExecutionPolicy Bypass` command shown above.
- If you are only trying to show the live app, ignore the older `DARCI-v3` startup docs.
