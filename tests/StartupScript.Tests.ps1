$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $root "scripts\lib\NetSplit-Startup.ps1"
$repairPath = Join-Path $root "scripts\repair-startup.ps1"
$statusPath = Join-Path $root "scripts\startup-status.ps1"
$installPath = Join-Path $root "scripts\install.ps1"

foreach ($path in @($helperPath, $repairPath, $statusPath, $installPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Startup support file was not found: $path"
    }

    $tokens = $null
    $parseErrors = $null
    [Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) {
        throw "$path has parse errors: $($parseErrors.Message -join '; ')"
    }
}

$helper = Get-Content -LiteralPath $helperPath -Raw
foreach ($pattern in @(
    'function Set-NetSplitServiceStartup',
    'start=',
    'delayed-auto',
    'function Register-NetSplitTrayTask',
    '$trigger.Delay = "PT15S"',
    '-StartWhenAvailable',
    '-RestartCount 5',
    '-RestartInterval',
    '-MultipleInstances IgnoreNew',
    'function Get-NetSplitStartupSnapshot',
    'RestartOnFailure',
    'StartupDisableActive'
)) {
    if ($helper -notmatch [regex]::Escape($pattern)) {
        throw "NetSplit-Startup.ps1 is missing startup reliability behavior: $pattern"
    }
}

$repair = Get-Content -LiteralPath $repairPath -Raw
foreach ($pattern in @(
    'Set-NetSplitServiceStartup',
    'Register-NetSplitTrayTask',
    'Get-NetSplitStartupSnapshot',
    'Start-ScheduledTask',
    'State.*Running',
    'Start-Service',
    'StartService',
    'startup\.force-disabled'
)) {
    if ($repair -notmatch $pattern) {
        throw "repair-startup.ps1 is missing expected behavior: $pattern"
    }
}

if ($repair -match 'Disable-RunningNetSplit|Disable-SavedSplitState|Stop-Service') {
    throw "repair-startup.ps1 must not change the saved split state or stop the service."
}

$status = Get-Content -LiteralPath $statusPath -Raw
foreach ($pattern in @(
    'Get-NetSplitStartupSnapshot',
    'Send-NetSplitRpc',
    'get-status',
    'OutputPath',
    'RegistrationHealthy',
    'UTF8Encoding\]::new\(\$true\)',
    'if \(-not \$runtime\)',
    'exit 3'
)) {
    if ($status -notmatch $pattern) {
        throw "startup-status.ps1 is missing observable startup evidence: $pattern"
    }
}

Write-Host "Startup script checks passed."
