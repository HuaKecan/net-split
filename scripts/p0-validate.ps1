param(
    [Parameter(Mandatory = $true)]
    [string]$DirectAdapterName,

    [Parameter(Mandatory = $true)]
    [string]$ProxyAdapterName,

    [string]$ProfilePath = "",
    [string]$MihomoPath = "C:\Program Files\Clash Verge\verge-mihomo.exe",
    [string]$GeoDataDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $ProfilePath) {
    $base = Join-Path $env:APPDATA "io.github.clash-verge-rev.clash-verge-rev"
    $profiles = Get-Content (Join-Path $base "profiles.yaml") -Raw
    $current = [regex]::Match($profiles, "(?m)^current:\s*(\S+)").Groups[1].Value
    if (-not $current) {
        throw "Unable to identify the current Clash Verge profile."
    }
    $ProfilePath = Join-Path $base "profiles\$current.yaml"
    if (-not $GeoDataDirectory) {
        $GeoDataDirectory = $base
    }
}

$direct = Get-NetAdapter -Name $DirectAdapterName -ErrorAction Stop
$proxy = Get-NetAdapter -Name $ProxyAdapterName -ErrorAction Stop
if ($direct.ifIndex -eq $proxy.ifIndex) {
    throw "The direct and proxy adapters must be different."
}

$env:NETSPLIT_RUN_P0 = "1"
$env:NETSPLIT_P0_DIRECT = $DirectAdapterName
$env:NETSPLIT_P0_PROXY = $ProxyAdapterName
$env:NETSPLIT_P0_PROFILE = (Resolve-Path -LiteralPath $ProfilePath).Path
$env:NETSPLIT_P0_MIHOMO = (Resolve-Path -LiteralPath $MihomoPath).Path
$env:NETSPLIT_P0_GEODATA = (Resolve-Path -LiteralPath $GeoDataDirectory).Path

dotnet test (Join-Path $root "tests\NetSplit.Core.Tests\NetSplit.Core.Tests.csproj") `
    -c Release --filter "FullyQualifiedName~P0LocalValidationTests"

if ($LASTEXITCODE -ne 0) {
    throw "P0 offline validation failed."
}

Write-Host "P0 offline validation passed. TUN was not started and system routes were not modified."
