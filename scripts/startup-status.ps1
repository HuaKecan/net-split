param(
    [string]$InstallRoot = "",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib\NetSplit-Startup.ps1")
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

if (-not $InstallRoot) {
    $InstallRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split"
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$serviceExe = Join-Path $InstallRoot "service\NetSplit.Service.exe"
$trayExe = Join-Path $InstallRoot "tray\NetSplit.Tray.exe"
$trayLauncher = Join-Path $InstallRoot "start-tray.ps1"
$marker = Join-Path $env:ProgramData "net-split\runtime\startup.force-disabled"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$startup = Get-NetSplitStartupSnapshot `
    -ServiceName $script:NetSplitDefaultServiceName `
    -TaskName $script:NetSplitDefaultTaskName `
    -ServiceExecutable $serviceExe `
    -TrayExecutable $trayExe `
    -TrayLauncherScript $trayLauncher `
    -UserName $identity.Name `
    -StartupDisableMarker $marker

$runtime = $null
$rpcError = ""
try {
    $runtime = Send-NetSplitRpc `
        -Command "get-status" `
        -ConnectTimeoutMs 3000
}
catch {
    $rpcError = $_.Exception.Message
}

$report = [ordered]@{
    CapturedAt = [DateTimeOffset]::UtcNow.ToString("o")
    Startup = $startup
    Runtime = if ($runtime) {
        [ordered]@{
            Reachable = $true
            Mode = [string]$runtime.mode
            Enabled = [bool]$runtime.enabled
            MihomoRunning = [bool]$runtime.mihomoRunning
            TunEnabled = [bool]$runtime.tunEnabled
            DnsEnabled = [bool]$runtime.dnsEnabled
            DirectAdapterAvailable = [bool]$runtime.directAdapterAvailable
            ProxyAdapterAvailable = [bool]$runtime.proxyAdapterAvailable
            ProxyRouteAvailable = [bool]$runtime.proxyRouteAvailable
            LastError = [string]$runtime.lastError
            UpdatedAt = $runtime.updatedAt
        }
    }
    else {
        [ordered]@{
            Reachable = $false
            Error = $rpcError
        }
    }
}

$json = $report | ConvertTo-Json -Depth 12
if ($OutputPath) {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    $parent = Split-Path -Parent $OutputPath
    if ($parent) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    [IO.File]::WriteAllText(
        $OutputPath,
        $json,
        [Text.UTF8Encoding]::new($true))
}

Write-Output $json
if (-not $startup.RegistrationHealthy) {
    exit 2
}
if (-not $runtime) {
    exit 3
}
