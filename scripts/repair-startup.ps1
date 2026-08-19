param(
    [string]$InstallRoot = "",
    [switch]$StartService
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib\NetSplit-Startup.ps1")

if (-not $InstallRoot) {
    $InstallRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split"
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$programFiles = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFiles)
if (-not (Test-NetSplitPathWithin -Path $InstallRoot -Root $programFiles)) {
    throw "InstallRoot must be inside Program Files."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $argumentList += " -InstallRoot `"$($InstallRoot.Replace('"', '\"'))`""
    if ($StartService) {
        $argumentList += " -StartService"
    }

    $elevated = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

$serviceExe = Join-Path $InstallRoot "service\NetSplit.Service.exe"
$trayExe = Join-Path $InstallRoot "tray\NetSplit.Tray.exe"
$marker = Join-Path $env:ProgramData "net-split\runtime\startup.force-disabled"
foreach ($path in @($InstallRoot, $serviceExe, $trayExe)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required net-split path was not found: $path"
    }
}

$installItem = Get-Item -LiteralPath $InstallRoot -Force
if ($installItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
    throw "InstallRoot must not be a reparse point."
}

Set-NetSplitServiceStartup `
    -ServiceName $script:NetSplitDefaultServiceName `
    -ServiceExecutable $serviceExe
Register-NetSplitTrayTask `
    -TaskName $script:NetSplitDefaultTaskName `
    -TrayExecutable $trayExe `
    -UserName $identity.Name

if ($StartService) {
    $service = Get-Service `
        -Name $script:NetSplitDefaultServiceName `
        -ErrorAction Stop
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name $script:NetSplitDefaultServiceName
    }
}

$task = Get-ScheduledTask `
    -TaskName $script:NetSplitDefaultTaskName `
    -ErrorAction Stop
if ([string]$task.State -ne "Running") {
    Start-ScheduledTask -TaskName $script:NetSplitDefaultTaskName
}

$snapshot = Get-NetSplitStartupSnapshot `
    -ServiceName $script:NetSplitDefaultServiceName `
    -TaskName $script:NetSplitDefaultTaskName `
    -ServiceExecutable $serviceExe `
    -TrayExecutable $trayExe `
    -UserName $identity.Name `
    -StartupDisableMarker $marker
$snapshot | ConvertTo-Json -Depth 12
if (-not $snapshot.RegistrationHealthy) {
    throw "Startup registration repair did not produce a healthy registration."
}

Write-Host "net-split startup registration repaired. Current service state was not changed unless -StartService was supplied."
