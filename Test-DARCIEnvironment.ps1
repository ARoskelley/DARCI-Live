param(
    [string]$ApiUrl = "http://localhost:5081",
    [switch]$Build
)

$ErrorActionPreference = "Continue"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$DarciRoot = Join-Path $RepoRoot "DARCI-v4"
$ApiDir = Join-Path $DarciRoot "Darci.Api"
$script:Checks = @()
$script:PythonKind = $null

function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet("OK", "WARN", "FAIL")][string]$Status,
        [string]$Details = ""
    )

    $script:Checks += [pscustomobject]@{
        Name = $Name
        Status = $Status
        Details = $Details
    }
}

function Write-Check {
    param([pscustomobject]$Check)

    $color = "Gray"
    if ($Check.Status -eq "OK") { $color = "Green" }
    if ($Check.Status -eq "WARN") { $color = "Yellow" }
    if ($Check.Status -eq "FAIL") { $color = "Red" }

    $line = "[{0}] {1}" -f $Check.Status, $Check.Name
    if (-not [string]::IsNullOrWhiteSpace($Check.Details)) {
        $line = "$line - $($Check.Details)"
    }

    Write-Host $line -ForegroundColor $color
}

function Normalize-Url {
    param([string]$Url)

    $trimmed = $Url.Trim().TrimEnd("/")
    if ($trimmed -notmatch "^https?://") {
        return "http://$trimmed"
    }

    return $trimmed
}

function Get-OllamaCandidates {
    $items = New-Object System.Collections.Generic.List[string]
    foreach ($raw in @(
        $env:DARCI_OLLAMA_BASE_URL,
        $env:OLLAMA_HOST,
        "http://localhost:11434",
        "http://127.0.0.1:11434"
    )) {
        if ([string]::IsNullOrWhiteSpace($raw)) {
            continue
        }

        $normalized = Normalize-Url -Url $raw
        if (-not $items.Contains($normalized)) {
            [void]$items.Add($normalized)
        }
    }

    return $items
}

function Invoke-PythonCode {
    param([string]$Code)

    if ($script:PythonKind -eq "python") {
        $output = & python -c $Code 2>&1
    } else {
        $output = & py -3 -c $Code 2>&1
    }

    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output -join "`n")
    }
}

function Test-OllamaModelAvailable {
    param(
        [string]$ModelName,
        [string[]]$AvailableModels
    )

    if ($AvailableModels -contains $ModelName) {
        return $true
    }

    if ($ModelName -notmatch ":") {
        foreach ($available in $AvailableModels) {
            if ($available -eq "${ModelName}:latest" -or $available.StartsWith("${ModelName}:")) {
                return $true
            }
        }
    }

    return $false
}

if (-not (Test-Path $DarciRoot)) {
    Add-Check "DARCI-v4 folder" "FAIL" "Missing at $DarciRoot"
} else {
    Add-Check "DARCI-v4 folder" "OK" $DarciRoot
}

if (Test-Path (Join-Path $ApiDir ".env.local")) {
    Add-Check ".env.local" "OK" "Found Darci.Api/.env.local. It loads before parent env files."
} elseif (Test-Path (Join-Path $RepoRoot ".env.local")) {
    Add-Check ".env.local" "OK" "Found repo-root .env.local. It will be loaded by Darci.Api."
} else {
    Add-Check ".env.local" "WARN" "Not found. Copy .env.local.example when adding research/API keys."
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    Push-Location $ApiDir
    try {
        $dotnetVersion = (dotnet --version 2>$null)
    } finally {
        Pop-Location
    }

    if ($dotnetVersion -like "8.*") {
        Add-Check ".NET SDK" "OK" $dotnetVersion
    } else {
        Add-Check ".NET SDK" "WARN" "Found $dotnetVersion; .NET 8 is the tested target."
    }
} else {
    Add-Check ".NET SDK" "FAIL" "dotnet was not found on PATH."
}

if (Get-Command ollama -ErrorAction SilentlyContinue) {
    Add-Check "Ollama CLI" "OK" "ollama command is available"
} else {
    Add-Check "Ollama CLI" "WARN" "Install Ollama or ensure it is on PATH."
}

$ollamaReady = $false
$ollamaModels = @()
foreach ($baseUrl in Get-OllamaCandidates) {
    try {
        $tags = Invoke-RestMethod -Uri "$baseUrl/api/tags" -TimeoutSec 3 -ErrorAction Stop
        foreach ($model in $tags.models) {
            if ($model.name) {
                $ollamaModels += [string]$model.name
            }
        }

        $ollamaReady = $true
        Add-Check "Ollama server" "OK" $baseUrl
        break
    } catch {
        continue
    }
}

if (-not $ollamaReady) {
    Add-Check "Ollama server" "WARN" "No response on the usual local URLs. Start with: ollama serve"
} else {
    foreach ($requiredModel in @("gemma4:e4b", "nomic-embed-text")) {
        if (Test-OllamaModelAvailable -ModelName $requiredModel -AvailableModels $ollamaModels) {
            Add-Check "Ollama model $requiredModel" "OK" "available"
        } else {
            Add-Check "Ollama model $requiredModel" "WARN" "Run: ollama pull $requiredModel"
        }
    }
}

$pythonCommand = Get-Command python -ErrorAction SilentlyContinue
$pyCommand = Get-Command py -ErrorAction SilentlyContinue
if ($pythonCommand) {
    $script:PythonKind = "python"
} elseif ($pyCommand) {
    $script:PythonKind = "py"
}

if ($script:PythonKind) {
    $version = Invoke-PythonCode "import sys; print(sys.version.split()[0])"
    if ($version["ExitCode"] -eq 0) {
        Add-Check "Python" "OK" $version["Output"]
    } else {
        Add-Check "Python" "WARN" $version["Output"]
    }

    foreach ($module in @("fastapi", "uvicorn", "torch", "onnxruntime", "cadquery")) {
        $result = Invoke-PythonCode "import $module; print(getattr($module, '__version__', 'ok'))"
        if ($result["ExitCode"] -eq 0) {
            Add-Check "Python module $module" "OK" $result["Output"]
        } else {
            Add-Check "Python module $module" "WARN" "Missing or broken. Needed for optional training/engineering paths."
        }
    }
} else {
    Add-Check "Python" "WARN" "Not found. Core API can run, but training/engineering tools need Python."
}

$brainPolicy = Join-Path $DarciRoot "Darci.Brain.Training\models\darci_policy.onnx"
if (Test-Path $brainPolicy) {
    Add-Check "Brain policy model" "OK" $brainPolicy
} else {
    Add-Check "Brain policy model" "WARN" "Missing darci_policy.onnx; decision network will degrade."
}

$geometryPolicy = Join-Path $DarciRoot "Darci.Engineering.Training\models\geometry_policy.onnx"
if (Test-Path $geometryPolicy) {
    Add-Check "Engineering geometry policy" "OK" $geometryPolicy
} else {
    Add-Check "Engineering geometry policy" "WARN" "Optional. Train/copy geometry_policy.onnx before relying on neural engineering endpoints."
}

try {
    $status = Invoke-RestMethod -Uri "$($ApiUrl.TrimEnd('/'))/status" -TimeoutSec 3 -ErrorAction Stop
    Add-Check "Running API status" "OK" "Responded at $ApiUrl/status"
} catch {
    Add-Check "Running API status" "WARN" "API is not running yet. Start with .\Start-DARCI.ps1"
}

if ($Build) {
    Push-Location $DarciRoot
    try {
        $buildOutput = dotnet build DARCI.sln -v:minimal 2>&1
        if ($LASTEXITCODE -eq 0) {
            Add-Check "Solution build" "OK" "dotnet build DARCI.sln"
        } else {
            Add-Check "Solution build" "FAIL" (($buildOutput | Select-Object -Last 8) -join "`n")
        }
    } finally {
        Pop-Location
    }
} else {
    Add-Check "Solution build" "WARN" "Skipped. Run .\Test-DARCIEnvironment.ps1 -Build to compile."
}

Write-Host ""
Write-Host "DARCI environment preflight" -ForegroundColor Cyan
Write-Host "--------------------------" -ForegroundColor Cyan
foreach ($check in $script:Checks) {
    Write-Check $check
}

$failures = @($script:Checks | Where-Object { $_.Status -eq "FAIL" })
if ($failures.Count -gt 0) {
    exit 1
}

exit 0
