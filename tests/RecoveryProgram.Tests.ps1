$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $root "src\NetSplit.Recovery\Program.cs"
$resultPath = Join-Path $root "src\NetSplit.Recovery\RecoveryResult.cs"
foreach ($path in @($programPath, $resultPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Recovery source file was not found: $path"
    }
}

$content = @(
    Get-Content -LiteralPath $programPath -Raw -Encoding utf8
    Get-Content -LiteralPath $resultPath -Raw -Encoding utf8
) -join [Environment]::NewLine

foreach ($pattern in @(
    'var canDeletePidFile = StopManagedMihomo\(paths, settings\)',
    'DeleteRuntimeFiles\(paths, canDeletePidFile\)',
    'string\.IsNullOrWhiteSpace\(settings\.MihomoPath\)',
    'string\.IsNullOrWhiteSpace\(actualPath\)',
    'Path\.GetFullPath\(actualPath\)',
    'if \(deletePidFile\)',
    'var serviceStopped = StopService\(serviceName\)',
    'var runtimeFilesDeleted = DeleteRuntimeFiles\(paths, canDeletePidFile\)',
    'var dnsFlushed = FlushDnsCache\(\)',
    'RecoveryResult\.Evaluate',
    'TransactionJournalFile',
    'TransactionRuntimeBackupFile',
    'WaitForExit\(10000\)',
    'Environment\.ExitCode = 1'
)) {
    if ($content -notmatch $pattern) {
        throw "Recovery program is missing the managed-process safety contract: $pattern"
    }
}

Write-Host "Recovery program safety checks passed."
