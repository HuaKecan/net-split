param(
    [ValidateRange(60, 600)]
    [int]$WatchdogSeconds = 180
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $root "artifacts\p0"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDirectory "p0-dnsleak-$timestamp.json"
$watchdogMarker = Join-Path $reportDirectory "p0-dnsleak-watchdog-$timestamp.txt"
$watchdogScript = Join-Path $PSScriptRoot "p0-watchdog.ps1"
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

Wait-NetSplitServiceReady -TimeoutSeconds 60 | Out-Null

function Invoke-CurlText {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [ValidateRange(3, 60)]
        [int]$TimeoutSeconds = 20
    )

    $bodyPath = [IO.Path]::GetTempFileName()
    $arguments = @(
        "-4",
        "--connect-timeout", "8",
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
                [IO.File]::ReadAllBytes($bodyPath)).Trim()
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
    return [pscustomobject]@{
        Url = $Url
        ExitCode = $exitCode
        HttpCode = $httpCode
        Body = $body
        Error = $text -replace "NETSPLIT_HTTP_CODE:\d{3}", ""
        Succeeded = $exitCode -eq 0 `
            -and $httpCode -match "^[23]\d\d$"
    }
}

$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now
    EnabledStatus = $null
    ExpectedProxyIp = ""
    TestId = ""
    TriggerResults = @()
    RawResults = @()
    PublicIp = $null
    DnsResolvers = @()
    Conclusions = @()
    PotentialLeaks = @()
    Passed = $false
    DisabledStatus = $null
    CleanupSucceeded = $false
    Failure = ""
    CompletedAt = $null
}

$watchdog = $null
$disableSucceeded = $false
try {
    $initialStatus = Send-NetSplitRpc -Command "get-status"
    if ($initialStatus.enabled `
            -or $initialStatus.mihomoRunning `
            -or $initialStatus.tunEnabled `
            -or $initialStatus.dnsEnabled) {
        throw "Disable net-split before starting the DNS leak P0 test."
    }

    $watchdog = Start-Process `
        -FilePath "powershell.exe" `
        -WindowStyle Hidden `
        -ArgumentList @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", "`"$watchdogScript`"",
            "-DelaySeconds", $WatchdogSeconds,
            "-MarkerPath", "`"$watchdogMarker`"") `
        -PassThru

    Send-NetSplitRpc -Command "enable" | Out-Null
    $report.EnabledStatus = Wait-NetSplitStatus `
        -TimeoutSeconds 60 `
        -Description "Healthy mode with TUN enabled" `
        -Predicate {
            param($status)
            Test-NetSplitHealthyStatus $status
        }

    $proxyIpResponse = Invoke-CurlText -Url "https://api.ipify.org"
    if (-not $proxyIpResponse.Succeeded `
            -or $proxyIpResponse.Body -notmatch (
                "^(?:\d{1,3}\.){3}\d{1,3}$")) {
        throw "The proxy public IPv4 probe failed."
    }

    $report.ExpectedProxyIp = $proxyIpResponse.Body
    $idResponse = Invoke-CurlText -Url "https://bash.ws/id"
    if (-not $idResponse.Succeeded `
            -or $idResponse.Body -notmatch "^[A-Za-z0-9]+$") {
        throw "bash.ws did not return a valid DNS leak test ID."
    }

    $report.TestId = $idResponse.Body
    $triggerResults = New-Object Collections.Generic.List[object]
    foreach ($index in 0..9) {
        $hostName = "$index.$($report.TestId).bash.ws"
        $addresses = @()
        $errorMessage = ""
        $connectionExitCode = $null
        try {
            $addresses = @(Resolve-DnsName `
                -Name $hostName `
                -Type A `
                -DnsOnly `
                -ErrorAction Stop |
                Where-Object { $_.IPAddress } |
                Select-Object -ExpandProperty IPAddress)
        }
        catch {
            $errorMessage = $_.Exception.Message
        }

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            & curl.exe `
                -4 `
                --connect-timeout 2 `
                --max-time 3 `
                --silent `
                --output NUL `
                "http://$hostName/" 2>&1 | Out-Null
            $connectionExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        $triggerResults.Add([pscustomobject]@{
            HostName = $hostName
            Addresses = $addresses
            ConnectionExitCode = $connectionExitCode
            Error = $errorMessage
        })
    }

    $report.TriggerResults = $triggerResults
    Start-Sleep -Seconds 4
    $resultResponse = Invoke-CurlText `
        -Url "https://bash.ws/dnsleak/test/$($report.TestId)?json"
    if (-not $resultResponse.Succeeded) {
        throw "bash.ws did not return DNS leak results."
    }

    $parsedResults = $resultResponse.Body | ConvertFrom-Json
    $rawResults = if ($parsedResults.PSObject.Properties["value"]) {
        @($parsedResults.value)
    }
    else {
        @($parsedResults)
    }
    $report.RawResults = $rawResults
    $report.PublicIp = $rawResults |
        Where-Object { $_.type -eq "ip" } |
        Select-Object -First 1
    $report.DnsResolvers = @(
        $rawResults | Where-Object { $_.type -eq "dns" })
    $report.Conclusions = @(
        $rawResults | Where-Object { $_.type -eq "conclusion" })

    $report.PotentialLeaks = @(
        $report.DnsResolvers | Where-Object {
            $_.country -eq "CN" `
                -or $_.country_name -match "(?i)\bChina\b" `
                -or $_.asn -match (
                    "(?i)China Mobile|China Telecom|China Unicom|" +
                    "CHINANET|CMNET|CNCGROUP")
        })
    $triggeredCount = @(
        $report.TriggerResults |
        Where-Object { $_.Addresses.Count -gt 0 }).Count
    $report.Passed =
        $triggeredCount -ge 8 `
        -and $report.PublicIp `
        -and $report.PublicIp.ip -eq $report.ExpectedProxyIp `
        -and $report.DnsResolvers.Count -gt 0 `
        -and $report.PotentialLeaks.Count -eq 0
}
catch {
    $report.Failure = $_.Exception.ToString()
}
finally {
    try {
        Send-NetSplitRpc -Command "disable" | Out-Null
        $report.DisabledStatus = Wait-NetSplitStatus `
            -TimeoutSeconds 60 `
            -Description "Disabled mode after DNS leak test" `
            -Predicate {
                param($status)
                $status.mode -eq "Disabled" `
                    -and -not $status.enabled `
                    -and -not $status.mihomoRunning `
                    -and -not $status.tunEnabled `
                    -and -not $status.dnsEnabled
            }
        $disableSucceeded = $true
    }
    catch {
        if ($report.Failure) {
            $report.Failure += [Environment]::NewLine
        }

        $report.Failure += "Cleanup failed: $($_.Exception)"
    }

    if ($disableSucceeded -and $watchdog -and -not $watchdog.HasExited) {
        Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    }

    $report.CleanupSucceeded = $disableSucceeded
    $report.CompletedAt = [DateTimeOffset]::Now
    [IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 100),
        [Text.UTF8Encoding]::new($false))
}

Write-Host "P0 DNS leak report: $reportPath"
Write-Host "DNS leak API passed: $($report.Passed)"
Write-Host "Observed DNS resolvers: $($report.DnsResolvers.Count)"
Write-Host "Potential ISP leaks: $($report.PotentialLeaks.Count)"
Write-Host "Split routing disabled after test: $disableSucceeded"

if (-not $report.Passed -or -not $disableSucceeded) {
    exit 1
}

exit 0
