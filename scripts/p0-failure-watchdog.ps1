param(
    [Parameter(Mandatory = $true)]
    [string]$ProxyAdapterName,

    [Parameter(Mandatory = $true)]
    [string]$MarkerPath,

    [ValidateRange(60, 900)]
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

function Disable-NetSplit {
    Send-NetSplitRpc -Command "disable" -ConnectTimeoutMs 15000 | Out-Null
}

Start-Sleep -Seconds $TimeoutSeconds
$messages = New-Object Collections.Generic.List[string]

try {
    $adapter = Get-NetAdapter -Name $ProxyAdapterName -ErrorAction Stop
    if ($adapter.Status -ne "Up") {
        Enable-NetAdapter -Name $ProxyAdapterName -Confirm:$false -ErrorAction Stop
        $messages.Add("proxy-adapter-enabled")
    }
    else {
        $messages.Add("proxy-adapter-already-up")
    }
}
catch {
    $messages.Add("proxy-adapter-enable-failed: $($_.Exception.Message)")
}

try {
    Disable-NetSplit
    $messages.Add("split-routing-disabled")
}
catch {
    $messages.Add("split-routing-disable-failed: $($_.Exception.Message)")
}

$messages.Add("completed-at: $([DateTimeOffset]::Now.ToString('O'))")
[IO.File]::WriteAllLines(
    $MarkerPath,
    $messages,
    [Text.UTF8Encoding]::new($false))
