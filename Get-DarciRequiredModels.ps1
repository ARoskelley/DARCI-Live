# Get-DarciRequiredModels.ps1
#
# THE single source of truth for "which Ollama models does this host actually need".
#
# host-profile.json already decides what every model CLASS resolves to on this machine — the model broker
# reads it at startup and fails there, by name, if a class cannot be satisfied. Anything else that names
# models is a copy, and copies drift: `gemma4:e4b` survived in three separate files long after it stopped
# being a real tag, so every prerequisite check was verifying a model nothing would ever load.
#
# So nothing hardcodes a model list any more. Derive it from the profile, and the drift cannot recur.
#
# Usage:
#   $models = & "$PSScriptRoot\Get-DarciRequiredModels.ps1"
#   $models = & "$PSScriptRoot\Get-DarciRequiredModels.ps1" -ProfilePath "C:\somewhere\host-profile.json"

[CmdletBinding()]
param(
    [string]$ProfilePath
)

function Resolve-ProfilePath {
    param([string]$Explicit)

    if ($Explicit) { return $Explicit }

    # DARCI_HOST_PROFILE wins, exactly as the core resolves it.
    if ($env:DARCI_HOST_PROFILE) { return $env:DARCI_HOST_PROFILE }

    $candidates = @(
        (Join-Path $PSScriptRoot "DARCI-v4\host-profile.json"),   # repo layout
        (Join-Path $PSScriptRoot "host-profile.json")             # packaged zip layout
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$path = Resolve-ProfilePath -Explicit $ProfilePath

if (-not $path -or -not (Test-Path $path)) {
    # No profile means the core falls back to its env-compat profile, whose defaults are these. Keeping the
    # fallback here honest matters: a wrong list is worse than no list, because it reports confident nonsense.
    Write-Verbose "No host-profile.json found; using the core's env-compat defaults."
    return @("gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b")
}

try {
    $profileJson = Get-Content $path -Raw | ConvertFrom-Json
} catch {
    Write-Warning "host-profile.json at '$path' could not be parsed: $($_.Exception.Message)"
    return @("gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b")
}

$models = @()
foreach ($class in $profileJson.classes.PSObject.Properties) {
    # Only Ollama classes are a local pull concern; a hosted provider needs an API key, not a model file.
    $provider = $profileJson.providers.PSObject.Properties[$class.Value.provider]
    if ($provider -and $provider.Value.kind -ne "ollama") { continue }

    $model = $class.Value.model
    if ($model -and $models -notcontains $model) { $models += $model }
}

if ($models.Count -eq 0) {
    Write-Warning "host-profile.json at '$path' declared no Ollama models; falling back to defaults."
    return @("gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b")
}

return $models
