param(
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "status",
        "settings",
        "diagnostics",
        "validate",
        "enable",
        "disable",
        "add-direct-domain")]
    [string]$Action,

    [string]$Domain = "",
    [string]$OutputDirectory = "",

    [ValidateRange(1, 180)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

if ($Action -eq "settings") {
    Send-NetSplitRpc -Command "get-settings" |
        ConvertTo-Json -Depth 30
    exit 0
}

if ($Action -eq "diagnostics") {
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $root "artifacts\diagnostics"
    }

    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $reportPath = Join-Path $OutputDirectory "net-split-diagnostics-$timestamp.json"
    $diagnostics = Send-NetSplitRpc -Command "get-diagnostics"
    $json = $diagnostics | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText(
        $reportPath,
        $json,
        [Text.UTF8Encoding]::new($false))
    Write-Host "Diagnostics report: $reportPath"
    exit 0
}

if ($Action -eq "validate") {
    Wait-NetSplitServiceReady -TimeoutSeconds $TimeoutSeconds | Out-Null
    $validation = Send-NetSplitRpc -Command "validate"
    $validation | ConvertTo-Json -Depth 30
    if ($validation.isValid -ne $true) {
        exit 2
    }

    exit 0
}

if ($Action -eq "add-direct-domain") {
    Wait-NetSplitServiceReady -TimeoutSeconds $TimeoutSeconds | Out-Null
    $normalizedDomain = $Domain.Trim().TrimEnd(".").ToLowerInvariant()
    if ($normalizedDomain -notmatch (
            "^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}" +
            "[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) {
        throw "Domain must be a valid DNS name."
    }

    $settings = Send-NetSplitRpc -Command "get-settings"
    $existing = $settings.rules |
        Where-Object {
            $_.matchType -eq "Domain" `
                -and $_.action -eq "Direct" `
                -and $_.value.Trim().TrimEnd(".").Equals(
                    $normalizedDomain,
                    [StringComparison]::OrdinalIgnoreCase)
        } |
        Select-Object -First 1
    if (-not $existing) {
        Send-NetSplitRpc `
            -Command "add-rule" `
            -Payload @{
                id = [guid]::NewGuid()
                matchType = "Domain"
                action = "Direct"
                value = $normalizedDomain
                enabled = $true
            } | Out-Null
        $settings = Send-NetSplitRpc -Command "get-settings"
        $existing = $settings.rules |
            Where-Object {
                $_.matchType -eq "Domain" `
                    -and $_.action -eq "Direct" `
                    -and $_.value.Trim().TrimEnd(".").Equals(
                        $normalizedDomain,
                        [StringComparison]::OrdinalIgnoreCase)
            } |
            Select-Object -First 1
    }

    $existing | ConvertTo-Json -Depth 10
    exit 0
}

Wait-NetSplitServiceReady -TimeoutSeconds $TimeoutSeconds | Out-Null
$command = switch ($Action) {
    "status" { "get-status" }
    "enable" { "enable" }
    "disable" { "disable" }
}

Send-NetSplitRpc -Command $command | Out-Null
if ($Action -eq "status") {
    Get-NetSplitStatus | ConvertTo-Json -Depth 20
    exit 0
}

$predicate = switch ($Action) {
    "enable" {
        {
            param($status)
            Test-NetSplitHealthyStatus $status
        }
    }
    "disable" {
        {
            param($status)
            Test-NetSplitDisabledStatus $status
        }
    }
}

$status = Wait-NetSplitStatus `
    -Description "net-split action '$Action'" `
    -TimeoutSeconds $TimeoutSeconds `
    -Predicate $predicate
$status | ConvertTo-Json -Depth 20
