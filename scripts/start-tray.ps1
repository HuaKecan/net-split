param(
    [string]$TrayExecutable = "",
    [ValidateRange(5, 300)]
    [int]$StabilitySeconds = 60,
    [ValidateRange(1, 10)]
    [int]$MaximumAttempts = 3,
    [ValidateRange(1, 60)]
    [int]$RetryDelaySeconds = 10
)

$ErrorActionPreference = "Stop"

if (-not $TrayExecutable) {
    $TrayExecutable = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split\tray\NetSplit.Tray.exe"
}

$TrayExecutable = [IO.Path]::GetFullPath($TrayExecutable)
$logDirectory = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    "net-split\logs"
$logPath = Join-Path $logDirectory "startup.log"
$runtimeDirectory = Join-Path `
    ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) `
    "net-split\runtime"
$userExitMarker = Join-Path $runtimeDirectory "tray.exit-requested"

function Write-NetSplitStartupLog {
    param(
        [Parameter(Mandatory)]
        [string]$Event,
        [string]$Detail = ""
    )

    try {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        if ((Test-Path -LiteralPath $logPath -PathType Leaf) `
                -and (Get-Item -LiteralPath $logPath).Length -ge 262144) {
            $previousLog = "$logPath.1"
            Remove-Item -LiteralPath $previousLog -Force -ErrorAction SilentlyContinue
            Move-Item -LiteralPath $logPath -Destination $previousLog -Force
        }

        $line = "{0:o} event={1}" -f [DateTimeOffset]::Now, $Event
        if (-not [string]::IsNullOrWhiteSpace($Detail)) {
            $line += " $Detail"
        }

        [IO.File]::AppendAllText(
            $logPath,
            "$line$([Environment]::NewLine)",
            [Text.UTF8Encoding]::new($false))
    }
    catch {
        # Startup logging is best-effort and must not prevent the tray from starting.
    }
}

function Get-NetSplitTrayProcess {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $expectedPath = [IO.Path]::GetFullPath($ExecutablePath)
    $processName = [IO.Path]::GetFileName($expectedPath).Replace("'", "''")
    return @(
        Get-CimInstance Win32_Process `
            -Filter "Name = '$processName'" `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath `
                    -and [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                        $expectedPath,
                        [StringComparison]::OrdinalIgnoreCase)
            }
    )
}

if (-not (Test-Path -LiteralPath $TrayExecutable -PathType Leaf)) {
    Write-NetSplitStartupLog -Event "missing-executable"
    exit 20
}

Remove-Item `
    -LiteralPath $userExitMarker `
    -Force `
    -ErrorAction SilentlyContinue

if ((Get-NetSplitTrayProcess -ExecutablePath $TrayExecutable).Count -gt 0) {
    Write-NetSplitStartupLog -Event "existing-instance"
    exit 0
}

for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
    try {
        $process = Start-Process `
            -FilePath $TrayExecutable `
            -ArgumentList "--background" `
            -WorkingDirectory (Split-Path -Parent $TrayExecutable) `
            -PassThru
    }
    catch {
        Write-NetSplitStartupLog `
            -Event "launch-failed" `
            -Detail ("attempt={0} type={1} hresult=0x{2:X8}" -f `
                $attempt,
                $_.Exception.GetType().FullName,
                [uint32]$_.Exception.HResult)
        if ($attempt -lt $MaximumAttempts) {
            Start-Sleep -Seconds $RetryDelaySeconds
            continue
        }

        exit 21
    }

    Write-NetSplitStartupLog `
        -Event "launched" `
        -Detail "attempt=$attempt pid=$($process.Id)"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($StabilitySeconds)
    do {
        Start-Sleep -Milliseconds 500
        try {
            $process.Refresh()
        }
        catch {
            # HasExited below remains the source of truth for the launched process.
        }

        if (-not $process.HasExited) {
            continue
        }

        if (Test-Path -LiteralPath $userExitMarker -PathType Leaf) {
            Remove-Item `
                -LiteralPath $userExitMarker `
                -Force `
                -ErrorAction SilentlyContinue
            Write-NetSplitStartupLog -Event "user-exit-requested"
            exit 0
        }

        if ((Get-NetSplitTrayProcess -ExecutablePath $TrayExecutable).Count -gt 0) {
            Write-NetSplitStartupLog -Event "handoff-to-existing-instance"
            exit 0
        }

        Write-NetSplitStartupLog `
            -Event "exited-during-startup" `
            -Detail "attempt=$attempt exitCode=$($process.ExitCode)"
        break
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    if (-not $process.HasExited) {
        Write-NetSplitStartupLog `
            -Event "startup-stable" `
            -Detail "attempt=$attempt pid=$($process.Id)"
        exit 0
    }

    if ($attempt -lt $MaximumAttempts) {
        Write-NetSplitStartupLog `
            -Event "retry-scheduled" `
            -Detail "attempt=$($attempt + 1) delaySeconds=$RetryDelaySeconds"
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}

Write-NetSplitStartupLog `
    -Event "attempts-exhausted" `
    -Detail "attempts=$MaximumAttempts"
exit 22
