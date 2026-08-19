param(
    [string]$PublishRoot = ""
)

$ErrorActionPreference = "Stop"
$serviceName = "NetSplitService"
$taskName = "NetSplit Tray"
$firewallRuleName = "NetSplit Mihomo DNS"
$root = Split-Path -Parent $PSScriptRoot

if (-not $PublishRoot) {
    $PublishRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($PublishRoot) {
        $argumentList += " -PublishRoot `"$($PublishRoot.Replace('"', '\"'))`""
    }

    $elevated = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

$recovery = Join-Path $PublishRoot "recovery\NetSplit.Recovery.exe"
if (Test-Path -LiteralPath $recovery) {
    Start-Process -FilePath $recovery -Wait
}

Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Get-Process -Name "NetSplit.Tray" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
}

Get-NetFirewallRule -DisplayName "$firewallRuleName*" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule

Write-Host "net-split service and tray startup were removed. Personal settings remain in ProgramData."
