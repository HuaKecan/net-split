param(
    [Parameter(Mandatory = $true)]
    [string]$DirectAdapterName,

    [Parameter(Mandatory = $true)]
    [string]$ProxyAdapterName
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $argumentList += " -DirectAdapterName `"$($DirectAdapterName.Replace('"', '\"'))`""
    $argumentList += " -ProxyAdapterName `"$($ProxyAdapterName.Replace('"', '\"'))`""
    $elevated = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

$reportDirectory = Join-Path $root "artifacts\p0"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$etlPath = Join-Path $reportDirectory "pktmon-$timestamp.etl"
$textPath = Join-Path $reportDirectory "pktmon-$timestamp.txt"
$pcapPath = Join-Path $reportDirectory "pktmon-$timestamp.pcapng"
$errorPath = Join-Path $reportDirectory "pktmon-$timestamp.error.txt"
$activeScript = Join-Path $PSScriptRoot "p0-active.ps1"
$activeExitCode = 1

trap {
    [IO.File]::WriteAllText(
        $errorPath,
        $_.Exception.ToString(),
        [Text.UTF8Encoding]::new($false))
    exit 1
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
pktmon stop 2>&1 | Out-Null
pktmon filter remove 2>&1 | Out-Null
$ErrorActionPreference = $previousErrorActionPreference
try {
    pktmon start --capture --comp nics --pkt-size 0 `
        --file-name $etlPath --file-size 256 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "PktMon failed to start with exit code $LASTEXITCODE."
    }

    & $activeScript `
        -DirectAdapterName $DirectAdapterName `
        -ProxyAdapterName $ProxyAdapterName `
        -WatchdogSeconds 150
    $activeExitCode = $LASTEXITCODE
}
finally {
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    pktmon stop 2>&1 | Out-Null
    $ErrorActionPreference = $previousErrorActionPreference
    if (Test-Path -LiteralPath $etlPath -PathType Leaf) {
        pktmon etl2txt $etlPath --out $textPath --brief | Out-Null
        pktmon etl2pcap $etlPath --out $pcapPath | Out-Null
    }
}

Write-Host "PktMon ETL: $etlPath"
Write-Host "PktMon text: $textPath"
Write-Host "PktMon pcapng: $pcapPath"
exit $activeExitCode
