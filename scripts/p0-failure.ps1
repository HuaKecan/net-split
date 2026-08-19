param(
    [Parameter(Mandatory = $true)]
    [string]$DirectAdapterName,

    [Parameter(Mandatory = $true)]
    [string]$ProxyAdapterName,

    [ValidateRange(120, 900)]
    [int]$WatchdogSeconds = 300,

    [string]$RunId = "",

    [switch]$Elevated
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $root "artifacts\p0"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
if (-not $RunId) {
    $RunId = Get-Date -Format "yyyyMMdd-HHmmss"
}

$reportPath = Join-Path $reportDirectory "p0-failure-$RunId.json"
$watchdogMarker = Join-Path $reportDirectory "p0-failure-watchdog-$RunId.txt"
$watchdogScript = Join-Path $PSScriptRoot "p0-failure-watchdog.ps1"
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdministrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-DirectAdapterName", "`"$DirectAdapterName`"",
        "-ProxyAdapterName", "`"$ProxyAdapterName`"",
        "-WatchdogSeconds", $WatchdogSeconds,
        "-RunId", $RunId,
        "-Elevated"
    )
    $process = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -WindowStyle Hidden `
        -ArgumentList $arguments `
        -Wait `
        -PassThru

    if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
        $report = [IO.File]::ReadAllText(
            $reportPath,
            [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
        Write-Host "P0 failure report: $reportPath"
        Write-Host "F50 failure/recovery passed: $($report.F50Passed)"
        Write-Host "Mihomo crash recovery passed: $($report.MihomoCrashPassed)"
        Write-Host "Full failure P0 passed: $($report.FullP0Passed)"
        Write-Host "Split routing disabled after test: $($report.Cleanup.SplitDisabled)"
        Write-Host "Proxy adapter restored after test: $($report.Cleanup.ProxyAdapterUp)"
    }
    else {
        Write-Error "The elevated P0 process did not create a report."
    }

    exit $process.ExitCode
}

Wait-NetSplitServiceReady -TimeoutSeconds 60 | Out-Null

function Wait-AdapterUp {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [ValidateRange(1, 180)]
        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $adapter = Get-NetAdapter -Name $Name -ErrorAction SilentlyContinue
        $addresses = Get-NetIPAddress `
            -InterfaceAlias $Name `
            -AddressFamily IPv4 `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.IPAddress -notlike "169.254.*" `
                    -and $_.AddressState -ne "Tentative"
            }
        $gateway = Get-NetRoute `
            -InterfaceAlias $Name `
            -AddressFamily IPv4 `
            -DestinationPrefix "0.0.0.0/0" `
            -ErrorAction SilentlyContinue
        if ($adapter.Status -eq "Up" -and $addresses -and $gateway) {
            return $adapter
        }

        Start-Sleep -Seconds 1
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for adapter '$Name' to regain IPv4 and a default route."
}

function Invoke-CurlProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [ValidateRange(3, 60)]
        [int]$TimeoutSeconds = 15
    )

    $bodyPath = [IO.Path]::GetTempFileName()
    $arguments = @(
        "-4",
        "--connect-timeout", "5",
        "--max-time", $TimeoutSeconds,
        "--silent",
        "--show-error",
        "--output", $bodyPath,
        "--write-out", "NETSPLIT_HTTP_CODE:%{http_code}",
        $Url
    )
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & curl.exe @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $body = if (Test-Path -LiteralPath $bodyPath -PathType Leaf) {
            [Text.Encoding]::UTF8.GetString(
                [IO.File]::ReadAllBytes($bodyPath))
        }
        else {
            ""
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
        Remove-Item -LiteralPath $bodyPath -Force -ErrorAction SilentlyContinue
    }

    $text = ($output | Out-String).Trim()
    $httpCode = [regex]::Match(
        $text,
        "NETSPLIT_HTTP_CODE:(\d{3})").Groups[1].Value
    $ipAddress = [regex]::Match(
        $body,
        "(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)").Value
    return [pscustomobject]@{
        Url = $Url
        ExitCode = $exitCode
        HttpCode = $httpCode
        Body = $body
        IpAddress = $ipAddress
        Succeeded = $exitCode -eq 0 `
            -and $httpCode -match "^[23]\d\d$"
    }
}

function Get-ManagedMihomoProcess {
    return Get-CimInstance Win32_Process -Filter "Name='mihomo.exe'" |
        Where-Object {
            $_.ExecutablePath -like "C:\Program Files\net-split\service\mihomo.exe"
        } |
        Select-Object -First 1
}

function Wait-MihomoRestart {
    param(
        [Parameter(Mandatory)]
        [int]$PreviousProcessId,
        [ValidateRange(10, 180)]
        [int]$TimeoutSeconds = 90
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $process = Get-ManagedMihomoProcess
        $status = Get-NetSplitStatus
        if ($process `
                -and $process.ProcessId -ne $PreviousProcessId `
                -and (Test-NetSplitHealthyStatus $status)) {
            return [pscustomobject]@{
                Process = $process
                Status = $status
            }
        }

        Start-Sleep -Milliseconds 500
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for Mihomo to restart with TUN enabled."
}

$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now
    InitialStatus = $null
    InitialDomesticProbe = $null
    InitialForeignProbe = $null
    ProxyAdapterDisabledAt = $null
    ProxyUnavailableStatus = $null
    DomesticWhileProxyDown = $null
    ForeignWhileProxyDown = $null
    ProxyAdapterEnabledAt = $null
    RecoveredStatus = $null
    DomesticAfterProxyRecovery = $null
    ForeignAfterProxyRecovery = $null
    MihomoOldProcessId = $null
    CoreUnavailableObserved = $false
    MihomoNewProcessId = $null
    MihomoRestartStatus = $null
    DomesticAfterMihomoRestart = $null
    ForeignAfterMihomoRestart = $null
    F50Passed = $false
    MihomoCrashPassed = $false
    FullP0Passed = $false
    Cleanup = [ordered]@{
        ProxyAdapterUp = $false
        SplitDisabled = $false
        Status = $null
        Error = ""
    }
    Failure = ""
    CompletedAt = $null
}

$watchdog = $null
$cleanupSucceeded = $false
try {
    $directAdapter = Get-NetAdapter -Name $DirectAdapterName -ErrorAction Stop
    $proxyAdapter = Get-NetAdapter -Name $ProxyAdapterName -ErrorAction Stop
    if ($directAdapter.Status -ne "Up" -or $proxyAdapter.Status -ne "Up") {
        throw "Both P0 adapters must be Up before the failure test starts."
    }

    $disabledStatus = Get-NetSplitStatus
    if ($disabledStatus.enabled `
            -or $disabledStatus.mihomoRunning `
            -or $disabledStatus.tunEnabled) {
        throw "Disable net-split before starting the P0 failure test."
    }

    $watchdogArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$watchdogScript`"",
        "-ProxyAdapterName", "`"$ProxyAdapterName`"",
        "-MarkerPath", "`"$watchdogMarker`"",
        "-TimeoutSeconds", $WatchdogSeconds
    )
    $watchdog = Start-Process `
        -FilePath "powershell.exe" `
        -WindowStyle Hidden `
        -ArgumentList $watchdogArguments `
        -PassThru

    Send-NetSplitRpc -Command "enable" | Out-Null
    $report.InitialStatus = Wait-NetSplitStatus `
        -TimeoutSeconds 60 `
        -Description "Healthy mode with TUN enabled" `
        -Predicate {
            param($status)
            Test-NetSplitHealthyStatus $status
        }
    $report.InitialDomesticProbe = Invoke-CurlProbe `
        -Url "http://myip.ipip.net"
    $report.InitialForeignProbe = Invoke-CurlProbe `
        -Url "https://api.ipify.org"
    if (-not $report.InitialDomesticProbe.Succeeded `
            -or -not $report.InitialForeignProbe.Succeeded) {
        throw "Initial domestic or foreign probe failed."
    }

    Disable-NetAdapter -Name $ProxyAdapterName -Confirm:$false
    $report.ProxyAdapterDisabledAt = [DateTimeOffset]::Now
    $report.ProxyUnavailableStatus = Wait-NetSplitStatus `
        -TimeoutSeconds 30 `
        -Description "ProxyUnavailable after disabling F50" `
        -Predicate {
            param($status)
            $status.mode -eq "ProxyUnavailable" `
                -and $status.directAdapterAvailable `
                -and -not $status.proxyAdapterAvailable `
                -and $status.mihomoRunning `
                -and $status.tunEnabled `
                -and $status.dnsEnabled
        }
    $report.DomesticWhileProxyDown = Invoke-CurlProbe `
        -Url "http://myip.ipip.net"
    $report.ForeignWhileProxyDown = Invoke-CurlProbe `
        -Url "https://api.ipify.org" `
        -TimeoutSeconds 10

    Enable-NetAdapter -Name $ProxyAdapterName -Confirm:$false
    $report.ProxyAdapterEnabledAt = [DateTimeOffset]::Now
    Wait-AdapterUp -Name $ProxyAdapterName -TimeoutSeconds 60 | Out-Null
    $report.RecoveredStatus = Wait-NetSplitStatus `
        -TimeoutSeconds 90 `
        -Description "Healthy mode after F50 recovery" `
        -Predicate {
            param($status)
            $status.mode -eq "Healthy" `
                -and $status.directAdapterAvailable `
                -and $status.proxyAdapterAvailable `
                -and (Test-NetSplitHealthyStatus $status)
        }
    Start-Sleep -Seconds 12
    $report.DomesticAfterProxyRecovery = Invoke-CurlProbe `
        -Url "http://myip.ipip.net"
    $report.ForeignAfterProxyRecovery = Invoke-CurlProbe `
        -Url "https://api.ipify.org"

    $report.F50Passed =
        $report.DomesticWhileProxyDown.Succeeded `
        -and $report.DomesticWhileProxyDown.IpAddress `
            -eq $report.InitialDomesticProbe.IpAddress `
        -and -not $report.ForeignWhileProxyDown.Succeeded `
        -and $report.DomesticAfterProxyRecovery.Succeeded `
        -and $report.ForeignAfterProxyRecovery.Succeeded

    $mihomo = Get-ManagedMihomoProcess
    if (-not $mihomo) {
        throw "The managed Mihomo process was not found."
    }

    $oldProcessId = [int]$mihomo.ProcessId
    $report.MihomoOldProcessId = $oldProcessId
    Stop-Process -Id $oldProcessId -Force
    $coreUnavailableDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    do {
        $status = Get-NetSplitStatus
        if ($status.mode -eq "CoreUnavailable" `
                -and -not $status.mihomoRunning `
                -and -not $status.tunEnabled `
                -and -not $status.dnsEnabled) {
            $report.CoreUnavailableObserved = $true
            break
        }

        Start-Sleep -Milliseconds 200
    }
    while ([DateTimeOffset]::UtcNow -lt $coreUnavailableDeadline)

    $restart = Wait-MihomoRestart `
        -PreviousProcessId $oldProcessId `
        -TimeoutSeconds 90
    $report.MihomoNewProcessId = [int]$restart.Process.ProcessId
    $report.MihomoRestartStatus = $restart.Status
    $report.DomesticAfterMihomoRestart = Invoke-CurlProbe `
        -Url "http://myip.ipip.net"
    $report.ForeignAfterMihomoRestart = Invoke-CurlProbe `
        -Url "https://api.ipify.org"
    $report.MihomoCrashPassed =
        $report.CoreUnavailableObserved `
        -and $report.MihomoNewProcessId -ne $report.MihomoOldProcessId `
        -and $report.DomesticAfterMihomoRestart.Succeeded `
        -and $report.ForeignAfterMihomoRestart.Succeeded
    $report.FullP0Passed = $report.F50Passed -and $report.MihomoCrashPassed
}
catch {
    $report.Failure = $_.Exception.ToString()
}
finally {
    try {
        $adapter = Get-NetAdapter -Name $ProxyAdapterName -ErrorAction SilentlyContinue
        if ($adapter -and $adapter.Status -ne "Up") {
            Enable-NetAdapter -Name $ProxyAdapterName -Confirm:$false
        }

        Wait-AdapterUp -Name $ProxyAdapterName -TimeoutSeconds 60 | Out-Null
        $report.Cleanup.ProxyAdapterUp = $true
    }
    catch {
        $report.Cleanup.Error = "Proxy adapter cleanup failed: $($_.Exception.Message)"
    }

    try {
        Send-NetSplitRpc -Command "disable" | Out-Null
        $report.Cleanup.Status = Wait-NetSplitStatus `
            -TimeoutSeconds 60 `
            -Description "Disabled mode after failure test" `
            -Predicate {
                param($status)
                $status.mode -eq "Disabled" `
                    -and -not $status.enabled `
                    -and -not $status.mihomoRunning `
                    -and -not $status.tunEnabled `
                    -and -not $status.dnsEnabled
            }
        $report.Cleanup.SplitDisabled = $true
    }
    catch {
        if ($report.Cleanup.Error) {
            $report.Cleanup.Error += " | "
        }

        $report.Cleanup.Error += "Split cleanup failed: $($_.Exception.Message)"
    }

    $cleanupSucceeded =
        $report.Cleanup.ProxyAdapterUp -and $report.Cleanup.SplitDisabled
    if ($cleanupSucceeded -and $watchdog -and -not $watchdog.HasExited) {
        Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    }

    $report.CompletedAt = [DateTimeOffset]::Now
    [IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 100),
        [Text.UTF8Encoding]::new($false))
}

if (-not $report.FullP0Passed -or -not $cleanupSucceeded) {
    exit 1
}

exit 0
