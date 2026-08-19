$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root "scripts\p0-control.ps1"
$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null

if ($parseErrors.Count -gt 0) {
    throw "p0-control.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

$content = Get-Content -LiteralPath $scriptPath -Raw
foreach ($pattern in @(
    '"diagnostics"',
    'get-diagnostics',
    '"validate"',
    'Send-NetSplitRpc -Command "validate"',
    'ConvertTo-Json -Depth 30',
    'UTF8Encoding'
)) {
    if ($content -notmatch $pattern) {
        throw "p0-control.ps1 is missing diagnostics export behavior: $pattern"
    }
}

Write-Host "p0-control.ps1 diagnostics checks passed."
