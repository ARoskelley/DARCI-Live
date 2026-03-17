# Start-DARCI.ps1
# Double-click this (or run in PowerShell) to start DARCI.
# Prerequisites: .NET 8 SDK, Ollama running with gemma2:9b + nomic-embed-text pulled.

param(
    [switch]$NoBrowser  # pass -NoBrowser to suppress auto-opening the web UI
)

$ErrorActionPreference = "Stop"
$ApiDir = Join-Path $PSScriptRoot "DARCI-v4\Darci.Api"

# ── Check .NET ────────────────────────────────────────────────────────────────
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}

$dotnetVer = (dotnet --version 2>$null)
if (-not $dotnetVer.StartsWith("8.")) {
    Write-Warning ".NET 8 recommended (found: $dotnetVer). Continuing anyway..."
}

# ── Check Ollama ──────────────────────────────────────────────────────────────
$ollamaRunning = $false
try {
    $resp = Invoke-WebRequest -Uri "http://localhost:11434" -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
    $ollamaRunning = $resp.StatusCode -eq 200
} catch {}

if (-not $ollamaRunning) {
    Write-Warning "Ollama doesn't appear to be running at http://localhost:11434."
    Write-Warning "DARCI's language features will be degraded. Start Ollama first if needed."
}

# ── Start DARCI ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "  ██████╗  █████╗ ██████╗  ██████╗██╗" -ForegroundColor Magenta
Write-Host "  ██╔══██╗██╔══██╗██╔══██╗██╔════╝██║" -ForegroundColor Magenta
Write-Host "  ██║  ██║███████║██████╔╝██║     ██║" -ForegroundColor Magenta
Write-Host "  ██║  ██║██╔══██║██╔══██╗██║     ██║" -ForegroundColor Magenta
Write-Host "  ██████╔╝██║  ██║██║  ██║╚██████╗██║" -ForegroundColor Magenta
Write-Host "  ╚═════╝ ╚═╝  ╚═╝╚═╝  ╚═╝ ╚═════╝╚═╝  v4.0" -ForegroundColor Magenta
Write-Host ""
Write-Host "  Starting on http://localhost:5081" -ForegroundColor Cyan
Write-Host "  Web UI:  http://localhost:5081/app/" -ForegroundColor Cyan
Write-Host "  Swagger: http://localhost:5081/swagger" -ForegroundColor DarkGray
Write-Host ""

# Open browser after a short delay (so the server has time to start)
if (-not $NoBrowser) {
    Start-Job -ScriptBlock {
        Start-Sleep 3
        Start-Process "http://localhost:5081/app/"
    } | Out-Null
}

# Run — this blocks until you press Ctrl+C
Push-Location $ApiDir
try {
    dotnet run --no-launch-profile
} finally {
    Pop-Location
}
