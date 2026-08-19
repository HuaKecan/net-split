param(
    [ValidateRange(15, 600)]
    [int]$DelaySeconds = 120,

    [string]$MarkerPath = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

function Send-Disable {
    Send-NetSplitRpc -Command "disable" -ConnectTimeoutMs 10000 | Out-Null
}

Start-Sleep -Seconds $DelaySeconds
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Send-Disable
        if ($MarkerPath) {
            [IO.File]::WriteAllText(
                $MarkerPath,
                "watchdog-disabled",
                [Text.UTF8Encoding]::new($false))
        }

        exit 0
    }
    catch {
        if ($attempt -eq 5) {
            if ($MarkerPath) {
                [IO.File]::WriteAllText(
                    $MarkerPath,
                    "watchdog-failed: $($_.Exception.Message)",
                    [Text.UTF8Encoding]::new($false))
            }

            exit 1
        }

        Start-Sleep -Seconds 5
    }
}
