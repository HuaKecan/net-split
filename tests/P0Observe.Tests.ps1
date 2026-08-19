$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root "scripts\p0-observe.ps1"
$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "p0-observe.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

$content = Get-Content -LiteralPath $scriptPath -Raw
foreach ($pattern in @(
    'scriptDirectoryName',
    'lib\\NetSplit-Rpc\.ps1',
    'lib\\NetSplit-Startup\.ps1',
    'Send-NetSplitRpc -Command "get-status"',
    'Send-NetSplitRpc -Command "get-diagnostics"',
    'Get-NetSplitStartupSnapshot',
    'Get-NetAdapterStatistics',
    'Get-NetTCPConnection',
    'Get-NetUDPEndpoint',
    'Get-FileHash',
    'HashMatchesExpected',
    'BindingEvidenceObserved',
    'ReadOnlyCapture = \$true',
    'UTF8Encoding',
    'UTF8Encoding\]::new\(\$true\)'
)) {
    if ($content -notmatch $pattern) {
        throw "p0-observe.ps1 is missing required evidence behavior: $pattern"
    }
}

foreach ($forbiddenPattern in @(
    'Send-NetSplitRpc -Command "enable"',
    'Send-NetSplitRpc -Command "disable"',
    'Disable-NetAdapter',
    'Enable-NetAdapter',
    'Set-NetRoute',
    'Set-DnsClientServerAddress',
    'Stop-Process -Id \$processId',
    'pktmon start'
)) {
    if ($content -match $forbiddenPattern) {
        throw "p0-observe.ps1 contains a mutating operation: $forbiddenPattern"
    }
}

Write-Host "p0-observe.ps1 read-only evidence checks passed."
