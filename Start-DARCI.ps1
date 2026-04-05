# Start-DARCI.ps1
# Double-click this (or run in PowerShell) to start DARCI.
# Prerequisites: .NET 8 SDK, Ollama running with gemma4:e4b + nomic-embed-text pulled.

param(
    [switch]$NoBrowser  # pass -NoBrowser to suppress auto-opening the web UI
)

$ErrorActionPreference = "Stop"
$ApiDir = Join-Path $PSScriptRoot "DARCI-v4\Darci.Api"
$ApiUrl = "http://localhost:5081"
$AppUrl = "$ApiUrl/app/"

function Test-UrlReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,
        [int]$Attempts = 1,
        [int]$DelaySeconds = 1
    )

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $resp = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 500) {
                return $true
            }
        } catch {}

        if ($attempt -lt $Attempts) {
            Start-Sleep -Seconds $DelaySeconds
        }
    }

    return $false
}

function Normalize-BaseUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url
    )

    $trimmed = $Url.Trim().TrimEnd('/')
    if ($trimmed -notmatch '^https?://') {
        return "http://$trimmed"
    }

    return $trimmed
}

function Get-OllamaBaseUrls {
    $candidates = New-Object System.Collections.Generic.List[string]

    foreach ($candidate in @(
        $env:DARCI_OLLAMA_BASE_URL,
        $env:OLLAMA_HOST,
        "http://localhost:11434",
        "http://127.0.0.1:11434"
    )) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        $normalized = Normalize-BaseUrl -Url $candidate
        if (-not $candidates.Contains($normalized)) {
            $null = $candidates.Add($normalized)
        }
    }

    return $candidates
}

function Find-ReadyBaseUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$BaseUrls
    )

    foreach ($baseUrl in $BaseUrls) {
        foreach ($probeUrl in @("$baseUrl/api/tags", $baseUrl)) {
            if (Test-UrlReady -Url $probeUrl -Attempts 2 -DelaySeconds 1) {
                return $baseUrl
            }
        }
    }

    return $null
}

# ── Check .NET ────────────────────────────────────────────────────────────────
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Error ".NET SDK not found. Install from https://dotnet.microsoft.com/download"
    exit 1
}

$dotnetVer = & {
    Push-Location $ApiDir
    try {
        dotnet --version 2>$null
    } finally {
        Pop-Location
    }
}

if (-not $dotnetVer.StartsWith("8.")) {
    Write-Warning ".NET 8 recommended (found: $dotnetVer). Continuing anyway..."
}

# ── Check Ollama ──────────────────────────────────────────────────────────────
$ollamaCandidates = Get-OllamaBaseUrls
$ollamaReadyUrl = Find-ReadyBaseUrl -BaseUrls $ollamaCandidates
if ($ollamaReadyUrl) {
    $env:DARCI_OLLAMA_BASE_URL = $ollamaReadyUrl
} else {
    Write-Warning "Ollama doesn't appear to be reachable at any of: $($ollamaCandidates -join ', ')."
    Write-Warning "If 'ollama serve' says the socket is already in use, Ollama is probably already running but bound to a different host."
    Write-Warning "DARCI's language features will be degraded until Ollama responds."
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
Write-Host "  Starting on $ApiUrl" -ForegroundColor Cyan
Write-Host "  Web UI:  $AppUrl" -ForegroundColor Cyan
Write-Host "  Swagger: $ApiUrl/swagger" -ForegroundColor DarkGray
if ($ollamaReadyUrl) {
    Write-Host "  Ollama:  $ollamaReadyUrl" -ForegroundColor DarkGray
}
Write-Host ""

# Open browser after the server is actually reachable
if (-not $NoBrowser) {
    Start-Job -ArgumentList $ApiUrl, $AppUrl -ScriptBlock {
        param($ReadyUrl, $BrowserUrl)

        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try {
                $resp = Invoke-WebRequest -Uri $ReadyUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
                if ($resp.StatusCode -ge 200 -and $resp.StatusCode -lt 500) {
                    Start-Process $BrowserUrl
                    return
                }
            } catch {}

            Start-Sleep -Seconds 1
        }
    } | Out-Null
}

# Run — this blocks until you press Ctrl+C
Push-Location $ApiDir
try {
    dotnet run --no-launch-profile -- --urls $ApiUrl
} finally {
    Pop-Location
}
