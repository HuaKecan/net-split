$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$helperPath = Join-Path $root "scripts\lib\NetSplit-Rpc.ps1"
if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
    throw "NetSplit RPC helper is missing."
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $helperPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -gt 0) {
    throw "NetSplit-Rpc.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

. $helperPath

$stream = New-Object IO.MemoryStream
Write-NetSplitRpcFrame `
    -Stream $stream `
    -Value '{"ok":true}' `
    -MaximumBytes 1024
$stream.Position = 0
$frame = Read-NetSplitRpcFrame -Stream $stream -MaximumBytes 1024
if ($frame -ne '{"ok":true}') {
    throw "RPC frame round-trip returned unexpected content."
}

$invalidStream = New-Object IO.MemoryStream
$invalidHeader = [BitConverter]::GetBytes(5)
$invalidStream.Write($invalidHeader, 0, $invalidHeader.Length)
$invalidStream.WriteByte(1)
$invalidStream.Position = 0
try {
    Read-NetSplitRpcFrame `
        -Stream $invalidStream `
        -MaximumBytes 4 | Out-Null
    throw "RPC frame size validation did not reject an oversized frame."
}
catch {
    if ($_.Exception.Message -notmatch "invalid frame length") {
        throw
    }
}

$script:rpcAttempts = 0
function Send-NetSplitRpc {
    param(
        [string]$Command,
        [object]$Payload = $null,
        [int]$ConnectTimeoutMs = 5000
    )

    $script:rpcAttempts++
    if ($script:rpcAttempts -eq 1) {
        throw "simulated transient pipe failure"
    }

    return [pscustomobject]@{
        mode = "Disabled"
        enabled = $false
        mihomoRunning = $false
        tunEnabled = $false
        dnsEnabled = $false
        dnsStatusKnown = $true
    }
}

$status = Wait-NetSplitStatus `
    -Description "test disabled state" `
    -TimeoutSeconds 2 `
    -PollIntervalMs 100 `
    -Predicate {
        param($candidate)
        Test-NetSplitDisabledStatus $candidate
    }
if ($script:rpcAttempts -lt 2 -or -not (Test-NetSplitDisabledStatus $status)) {
    throw "Wait-NetSplitStatus did not retry and recognize the disabled state."
}

try {
    Wait-NetSplitStatus `
        -Description "predicate failure propagation" `
        -TimeoutSeconds 1 `
        -PollIntervalMs 100 `
        -Predicate {
            throw "simulated predicate failure"
        } | Out-Null
    throw "Wait-NetSplitStatus swallowed a predicate failure."
}
catch {
    if ($_.Exception.Message -notmatch "simulated predicate failure") {
        throw
    }
}

$script:readinessAttempts = 0
function Send-NetSplitRpc {
    param(
        [string]$Command,
        [object]$Payload = $null,
        [int]$ConnectTimeoutMs = 5000
    )

    if ($Command -eq "get-diagnostics") {
        $script:readinessAttempts++
        return [pscustomobject]@{
            serviceReady = $script:readinessAttempts -ge 2
            readiness = "Starting"
            runtime = [pscustomobject]@{ mode = "Starting" }
        }
    }

    return [pscustomobject]@{
        mode = "Disabled"
        enabled = $false
        mihomoRunning = $false
        tunEnabled = $false
        dnsEnabled = $false
        dnsStatusKnown = $true
    }
}

$diagnostics = Wait-NetSplitServiceReady `
    -Description "test service readiness" `
    -TimeoutSeconds 2 `
    -PollIntervalMs 100
if ($script:readinessAttempts -lt 2 -or -not $diagnostics.serviceReady) {
    throw "Wait-NetSplitServiceReady did not wait for a ready diagnostic snapshot."
}

$script:readinessAttempts = 0
function Send-NetSplitRpc {
    param(
        [string]$Command,
        [object]$Payload = $null,
        [int]$ConnectTimeoutMs = 5000
    )

    return [pscustomobject]@{
        serviceReady = $false
        readiness = "RecoveryRequired"
        runtime = [pscustomobject]@{
            mode = "Misconfigured"
            lastError = "simulated initialization failure"
        }
    }
}

try {
    Wait-NetSplitServiceReady `
        -Description "terminal readiness failure" `
        -TimeoutSeconds 2 `
        -PollIntervalMs 100 | Out-Null
    throw "Wait-NetSplitServiceReady did not fail fast on Misconfigured."
}
catch {
    if ($_.Exception.Message -notmatch "simulated initialization failure") {
        throw
    }
}

$healthy = [pscustomobject]@{
    mode = "Healthy"
    enabled = $true
    mihomoRunning = $true
    tunEnabled = $true
    dnsEnabled = $true
    dnsStatusKnown = $true
    directAdapterAvailable = $true
    proxyAdapterAvailable = $true
    proxyRouteAvailable = $true
}
if (-not (Test-NetSplitHealthyStatus $healthy)) {
    throw "Healthy status predicate failed."
}

$unhealthyProxyRoute = $healthy | Select-Object *
$unhealthyProxyRoute.proxyRouteAvailable = $false
if (Test-NetSplitHealthyStatus $unhealthyProxyRoute) {
    throw "Healthy status predicate accepted an unavailable proxy route."
}

$proxyUnavailable = $healthy | Select-Object *
$proxyUnavailable.mode = "ProxyUnavailable"
$proxyUnavailable.proxyRouteAvailable = $false
if (-not (Test-NetSplitEnabledStatus $proxyUnavailable)) {
    throw "Enabled status predicate rejected a running degraded proxy state."
}

$directUnavailable = $healthy | Select-Object *
$directUnavailable.mode = "DirectUnavailable"
$directUnavailable.directAdapterAvailable = $false
if (-not (Test-NetSplitEnabledStatus $directUnavailable)) {
    throw "Enabled status predicate rejected a running degraded direct state."
}

$starting = $healthy | Select-Object *
$starting.mode = "Starting"
if (Test-NetSplitEnabledStatus $starting) {
    throw "Enabled status predicate accepted a transitional state."
}

$dnsUnavailable = $healthy | Select-Object *
$dnsUnavailable.dnsEnabled = $false
if (Test-NetSplitEnabledStatus $dnsUnavailable) {
    throw "Enabled status predicate accepted a disabled DNS listener."
}

$scriptFiles = @(
    "scripts\install.ps1",
    "scripts\p0-active.ps1",
    "scripts\p0-control.ps1",
    "scripts\p0-dnsleak.ps1",
    "scripts\p0-failure.ps1",
    "scripts\p0-watchdog.ps1",
    "scripts\p0-failure-watchdog.ps1"
)
foreach ($relativePath in $scriptFiles) {
    $scriptPath = Join-Path $root $relativePath
    $content = Get-Content -LiteralPath $scriptPath -Raw
    if ($content -notmatch [regex]::Escape('lib\NetSplit-Rpc.ps1')) {
        throw "$relativePath does not load the shared RPC helper."
    }

    if ($content -match 'function\s+(Read-ExactBytes|Send-NetSplitRpc)\s*\{') {
        throw "$relativePath still contains a private Named Pipe RPC implementation."
    }
}

$helperContent = Get-Content -LiteralPath $helperPath -Raw
foreach ($pattern in @(
    'responseId',
    'requestId',
    'MaximumResponseBytes',
    'Wait-NetSplitStatus',
    'Wait-NetSplitServiceReady',
    'Test-NetSplitEnabledStatus',
    'Last RPC error'
)) {
    if ($helperContent -notmatch [regex]::Escape($pattern)) {
        throw "Shared RPC helper is missing required behavior: $pattern"
    }
}

Write-Host "Shared NetSplit RPC checks passed."
