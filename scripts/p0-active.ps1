param(
    [Parameter(Mandatory = $true)]
    [string]$DirectAdapterName,

    [Parameter(Mandatory = $true)]
    [string]$ProxyAdapterName,

    [ValidateRange(30, 600)]
    [int]$WatchdogSeconds = 120
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path $root "artifacts\p0"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDirectory "p0-active-$timestamp.json"
$watchdogMarker = Join-Path $reportDirectory "p0-watchdog-$timestamp.txt"
$watchdogScript = Join-Path $PSScriptRoot "p0-watchdog.ps1"
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")

Wait-NetSplitServiceReady -TimeoutSeconds 60 | Out-Null

function Invoke-CurlProbe {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [string]$InterfaceAddress = "",
        [ValidateSet("4", "6")]
        [string]$AddressFamily = "4",
        [string]$Resolve = ""
    )

    $arguments = @(
        "-$AddressFamily",
        "--connect-timeout", "10",
        "--max-time", "25",
        "--silent",
        "--show-error",
        "--write-out", "`nNETSPLIT_HTTP_CODE:%{http_code}"
    )
    if ($InterfaceAddress) {
        $arguments += @("--interface", $InterfaceAddress)
    }

    if ($Resolve) {
        $arguments += @("--resolve", $Resolve)
    }

    $arguments += $Url
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = & curl.exe @arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $text = ($output | Out-String).Trim()
    $httpCode = [regex]::Match($text, "NETSPLIT_HTTP_CODE:(\d{3})").Groups[1].Value
    $body = $text -replace "\s*NETSPLIT_HTTP_CODE:\d{3}\s*$", ""
    $ip = if ($AddressFamily -eq "6") {
        $candidate = $body.Trim()
        $parsed = $null
        if ([Net.IPAddress]::TryParse($candidate, [ref]$parsed) -and $parsed.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) {
            $candidate
        }
        else {
            ""
        }
    }
    else {
        [regex]::Match(
            $body,
            "(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)").Value
    }

    return [pscustomobject]@{
        Url = $Url
        InterfaceAddress = $InterfaceAddress
        AddressFamily = $AddressFamily
        Resolve = $Resolve
        ExitCode = $exitCode
        HttpCode = $httpCode
        Body = $body
        IpAddress = $ip
        Succeeded = $exitCode -eq 0 -and $httpCode -match "^2|^3"
    }
}

function Get-AdapterSample {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $statistics = Get-NetAdapterStatistics -Name $Name
    return [pscustomobject]@{
        Name = $Name
        ReceivedBytes = [long]$statistics.ReceivedBytes
        SentBytes = [long]$statistics.SentBytes
        ReceivedUnicastPackets = [long]$statistics.ReceivedUnicastPackets
        SentUnicastPackets = [long]$statistics.SentUnicastPackets
    }
}

function Get-SampleDelta {
    param(
        [Parameter(Mandatory)]
        $Before,
        [Parameter(Mandatory)]
        $After
    )

    return [pscustomobject]@{
        Name = $Before.Name
        ReceivedBytes = $After.ReceivedBytes - $Before.ReceivedBytes
        SentBytes = $After.SentBytes - $Before.SentBytes
        ReceivedUnicastPackets =
            $After.ReceivedUnicastPackets - $Before.ReceivedUnicastPackets
        SentUnicastPackets =
            $After.SentUnicastPackets - $Before.SentUnicastPackets
    }
}

function Get-MihomoConnections {
    $process = Get-Process -Name "mihomo" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $process) {
        return @()
    }

    return @(
        Get-NetTCPConnection -OwningProcess $process.Id -State Established `
            -ErrorAction SilentlyContinue |
            Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort, OwningProcess
    )
}

function Invoke-ConnectionPhase {
    param(
        [Parameter(Mandatory)]
        [string]$Url,
        [ValidateRange(1, 50)]
        [int]$Iterations = 12
    )

    $job = Start-Job -ScriptBlock {
        param($ProbeUrl, $Count)
        $results = @()
        for ($index = 0; $index -lt $Count; $index++) {
            $output = & curl.exe -4 --connect-timeout 5 --max-time 15 `
                --silent --show-error $ProbeUrl 2>&1
            $results += [pscustomobject]@{
                ExitCode = $LASTEXITCODE
                Output = ($output | Out-String).Trim()
            }
        }

        return $results
    } -ArgumentList $Url, $Iterations

    $connections = @()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(35)
        do {
            $connections += Get-MihomoConnections
            Start-Sleep -Milliseconds 100
            $state = (Get-Job -Id $job.Id).State
        }
        while (($state -in @("NotStarted", "Running")) -and ([DateTime]::UtcNow -lt $deadline))

        if ($state -in @("NotStarted", "Running")) {
            Stop-Job -Id $job.Id
        }

        $results = @(Receive-Job -Id $job.Id -ErrorAction SilentlyContinue)
        return [pscustomobject]@{
            Url = $Url
            Results = $results
            Connections = @(
                $connections |
                    Sort-Object LocalAddress, LocalPort, RemoteAddress, RemotePort -Unique
            )
        }
    }
    finally {
        Remove-Job -Id $job.Id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-EdgeQuicProbe {
    param(
        [Parameter(Mandatory)]
        [string]$OutputDirectory,
        [Parameter(Mandatory)]
        [string]$RunId
    )

    $edgePath = @(
        "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        "C:\Program Files\Microsoft\Edge\Application\msedge.exe"
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if (-not $edgePath) {
        return [pscustomobject]@{
            Available = $false
            ExitCode = $null
            TimedOut = $false
            QuicObserved = $false
            QuicEvents = @()
            NetLogPath = ""
            StandardError = "Microsoft Edge was not found."
        }
    }

    $profileDirectory = Join-Path $env:TEMP "net-split-edge-$RunId"
    $netLogPath = Join-Path $OutputDirectory "edge-quic-$RunId.json"
    $stdoutPath = Join-Path $OutputDirectory "edge-quic-$RunId.stdout.txt"
    $stderrPath = Join-Path $OutputDirectory "edge-quic-$RunId.stderr.txt"
    New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
    $arguments = @(
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--enable-quic",
        "--origin-to-force-quic-on=www.google.com:443",
        "--user-data-dir=`"$profileDirectory`"",
        "--log-net-log=`"$netLogPath`"",
        "--net-log-capture-mode=Everything",
        "--dump-dom",
        "https://www.google.com/")

    $process = Start-Process `
        -FilePath $edgePath `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath `
        -PassThru
    $timedOut = -not $process.WaitForExit(35000)
    if ($timedOut -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" |
        Where-Object { $_.CommandLine -like "*$profileDirectory*" } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Seconds 1
    $netLog = if (Test-Path -LiteralPath $netLogPath -PathType Leaf) {
        $stream = New-Object IO.FileStream(
            $netLogPath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::Read,
            [IO.FileShare]::ReadWrite)
        try {
            $reader = New-Object IO.StreamReader(
                $stream,
                [Text.UTF8Encoding]::new($false))
            try {
                $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
    }
    else {
        ""
    }
    $quicEvents = @()
    try {
        $netLogJson = $netLog | ConvertFrom-Json
        $usedEventIds = @{}
        foreach ($event in @($netLogJson.events)) {
            $usedEventIds[[string]$event.type] = $true
        }

        foreach ($eventType in $netLogJson.constants.logEventTypes.PSObject.Properties) {
            if ($eventType.Name.Contains("QUIC") -and $usedEventIds.ContainsKey([string]$eventType.Value)) {
                $quicEvents += $eventType.Name
            }
        }
    }
    catch {
        $quicEvents = @()
    }

    return [pscustomobject]@{
        Available = $true
        ExitCode = $exitCode
        TimedOut = $timedOut
        QuicObserved = @($quicEvents).Count -gt 0
        QuicEvents = @($quicEvents | Sort-Object -Unique)
        NetLogPath = $netLogPath
        StandardError = if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
            [IO.File]::ReadAllText($stderrPath, [Text.UTF8Encoding]::new($false))
        }
        else {
            ""
        }
    }
}

function Wait-ForTunReady {
    param(
        [ValidateRange(5, 120)]
        [int]$TimeoutSeconds = 60
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $status = Send-NetSplitRpc -Command "get-status"
        $adapter = Get-NetAdapter -Name "NetSplit" -IncludeHidden `
            -ErrorAction SilentlyContinue
        if ((Test-NetSplitHealthyStatus $status) -and $adapter -and [string]$adapter.Status -eq "Up") {
            return $status
        }

        if ([string]$status.mode -in @("Misconfigured", "Degraded")) {
            throw "net-split failed while waiting for TUN: $($status.lastError)"
        }

        Start-Sleep -Seconds 1
    }
    while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for the NetSplit TUN adapter."
}

$direct = Get-NetIPConfiguration -InterfaceAlias $DirectAdapterName -ErrorAction Stop
$proxy = Get-NetIPConfiguration -InterfaceAlias $ProxyAdapterName -ErrorAction Stop
$directAddress = $direct.IPv4Address.IPAddress | Select-Object -First 1
$proxyAddress = $proxy.IPv4Address.IPAddress | Select-Object -First 1
if (-not $directAddress -or -not $proxyAddress) {
    throw "Both P0 adapters must have IPv4 addresses."
}

$initialStatus = Send-NetSplitRpc -Command "get-status"
if ([bool]$initialStatus.enabled -or [bool]$initialStatus.tunEnabled -or [bool]$initialStatus.dnsEnabled) {
    throw "Disable net-split before starting the active P0 test."
}

$validation = Send-NetSplitRpc -Command "validate"
if (-not [bool]$validation.isValid) {
    throw "Offline validation failed: $($validation.errors -join '; ')"
}

$baseline = [pscustomobject]@{
    CapturedAt = [DateTimeOffset]::Now
    DirectAddress = $directAddress
    ProxyAddress = $proxyAddress
    DirectPublicIp = Invoke-CurlProbe `
        -Url "http://myip.ipip.net" `
        -InterfaceAddress $directAddress
    ProxyPublicIp = Invoke-CurlProbe `
        -Url "http://myip.ipip.net" `
        -InterfaceAddress $proxyAddress
    DirectStatistics = Get-AdapterSample -Name $DirectAdapterName
    ProxyStatistics = Get-AdapterSample -Name $ProxyAdapterName
    DefaultRoutes = @(
        Get-NetRoute -AddressFamily IPv4 |
            Where-Object { $_.DestinationPrefix -eq "0.0.0.0/0" } |
            Select-Object DestinationPrefix, NextHop, InterfaceAlias,
                InterfaceIndex, RouteMetric
    )
    DnsServers = @(
        Get-DnsClientServerAddress -AddressFamily IPv4 |
            Select-Object InterfaceAlias, InterfaceIndex, ServerAddresses
    )
    EstablishedIpv6Connections = @(
        Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalAddress.Contains(":") -and $_.LocalAddress -notin @("::", "::1") } |
            Select-Object LocalAddress, LocalPort, RemoteAddress,
                RemotePort, OwningProcess
    )
    Ipv6TestAddresses = @(
        Resolve-DnsName "api64.ipify.org" -Type AAAA -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress } |
            Select-Object -ExpandProperty IPAddress
    )
    Ipv6PublicIp = Invoke-CurlProbe `
        -Url "https://api64.ipify.org" `
        -AddressFamily "6"
}

$watchdog = Start-Process `
    -FilePath "powershell.exe" `
    -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$watchdogScript`"",
        "-DelaySeconds", $WatchdogSeconds,
        "-MarkerPath", "`"$watchdogMarker`"") `
    -WindowStyle Hidden `
    -PassThru

$report = [ordered]@{
    StartedAt = [DateTimeOffset]::Now
    CompletedAt = $null
    Baseline = $baseline
    EnabledStatus = $null
    DomesticProbe = $null
    ForeignIpProbe = $null
    GoogleProbe = $null
    GitHubProbe = $null
    QuicProbe = $null
    DomesticDns = @()
    ForeignDns = @()
    DomesticPhase = $null
    ForeignPhase = $null
    DomesticTrafficDelta = $null
    ForeignTrafficDelta = $null
    IPv6BypassProbe = $null
    Ipv6ConnectionsWhileEnabled = @()
    DisabledStatus = $null
    IPv4StagePassed = $false
    FullP0Passed = $false
    Failure = ""
}

$disableSucceeded = $false
try {
    Send-NetSplitRpc -Command "enable" | Out-Null
    $enabledStatus = Wait-ForTunReady -TimeoutSeconds 60
    $report.EnabledStatus = $enabledStatus
    Start-Sleep -Seconds 2

    $domesticBeforeDirect = Get-AdapterSample -Name $DirectAdapterName
    $domesticBeforeProxy = Get-AdapterSample -Name $ProxyAdapterName
    $report.DomesticProbe = Invoke-CurlProbe -Url "http://myip.ipip.net"
    $report.DomesticDns = @(
        Resolve-DnsName "www.baidu.com" -Type A -ErrorAction SilentlyContinue |
            Select-Object Name, Type, IPAddress
    )
    $report.DomesticPhase = Invoke-ConnectionPhase `
        -Url "http://myip.ipip.net" `
        -Iterations 15
    $domesticAfterDirect = Get-AdapterSample -Name $DirectAdapterName
    $domesticAfterProxy = Get-AdapterSample -Name $ProxyAdapterName
    $report.DomesticTrafficDelta = [pscustomobject]@{
        Direct = Get-SampleDelta $domesticBeforeDirect $domesticAfterDirect
        Proxy = Get-SampleDelta $domesticBeforeProxy $domesticAfterProxy
    }

    $foreignBeforeDirect = Get-AdapterSample -Name $DirectAdapterName
    $foreignBeforeProxy = Get-AdapterSample -Name $ProxyAdapterName
    $report.ForeignIpProbe = Invoke-CurlProbe -Url "https://api.ipify.org"
    $report.GoogleProbe = Invoke-CurlProbe -Url "https://www.google.com/generate_204"
    $report.GitHubProbe = Invoke-CurlProbe -Url "https://github.com/robots.txt"
    $report.QuicProbe = Invoke-EdgeQuicProbe `
        -OutputDirectory $reportDirectory `
        -RunId $timestamp
    $report.ForeignDns = @(
        Resolve-DnsName "www.google.com" -Type A -ErrorAction SilentlyContinue |
            Select-Object Name, Type, IPAddress
    )
    $report.ForeignPhase = Invoke-ConnectionPhase `
        -Url "https://api.ipify.org" `
        -Iterations 15
    $foreignAfterDirect = Get-AdapterSample -Name $DirectAdapterName
    $foreignAfterProxy = Get-AdapterSample -Name $ProxyAdapterName
    $report.ForeignTrafficDelta = [pscustomobject]@{
        Direct = Get-SampleDelta $foreignBeforeDirect $foreignAfterDirect
        Proxy = Get-SampleDelta $foreignBeforeProxy $foreignAfterProxy
    }

    $report.Ipv6ConnectionsWhileEnabled = @(
        Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalAddress.Contains(":") -and $_.LocalAddress -notin @("::", "::1") } |
            Select-Object LocalAddress, LocalPort, RemoteAddress,
                RemotePort, OwningProcess
    )
    $ipv6TestAddress = $baseline.Ipv6TestAddresses | Select-Object -First 1
    if ($ipv6TestAddress) {
        $report.IPv6BypassProbe = Invoke-CurlProbe `
            -Url "https://api64.ipify.org" `
            -AddressFamily "6" `
            -Resolve "api64.ipify.org:443:[$ipv6TestAddress]"
    }

    $domesticIp = [string]$report.DomesticProbe.IpAddress
    $directIp = [string]$baseline.DirectPublicIp.IpAddress
    $foreignIp = [string]$report.ForeignIpProbe.IpAddress
    $proxyPhysicalIp = [string]$baseline.ProxyPublicIp.IpAddress
    $domesticUsesNic1 = $domesticIp -and $domesticIp -eq $directIp
    $foreignUsesProxy =
        [bool]$foreignIp -and $foreignIp -ne $directIp -and $foreignIp -ne $proxyPhysicalIp
    $domesticConnectionObserved = @(
        $report.DomesticPhase.Connections |
            Where-Object { $_.LocalAddress -eq $directAddress }
    ).Count -gt 0
    $proxyConnectionObserved = @(
        $report.ForeignPhase.Connections |
            Where-Object { $_.LocalAddress -eq $proxyAddress }
    ).Count -gt 0

    $stageChecks = @(
        $domesticUsesNic1,
        $foreignUsesProxy,
        $domesticConnectionObserved,
        $proxyConnectionObserved,
        [bool]$report.GoogleProbe.Succeeded,
        [bool]$report.GitHubProbe.Succeeded)
    $report.IPv4StagePassed = $stageChecks -notcontains $false
    $ipv6BypassObserved =
        $report.IPv6BypassProbe -and [bool]$report.IPv6BypassProbe.Succeeded
    $noIpv6ConnectionsObserved =
        @($report.Ipv6ConnectionsWhileEnabled).Count -eq 0
    $fullChecks = @(
        [bool]$report.IPv4StagePassed,
        [bool](-not $ipv6BypassObserved),
        [bool]$noIpv6ConnectionsObserved,
        [bool]$report.QuicProbe.Available,
        [bool]$report.QuicProbe.QuicObserved,
        [bool](-not $report.QuicProbe.TimedOut))
    $report.FullP0Passed = $fullChecks -notcontains $false
}
catch {
    $report.Failure = $_.Exception.ToString()
}
finally {
    try {
        Send-NetSplitRpc -Command "disable" | Out-Null
        $report.DisabledStatus = Wait-NetSplitStatus `
            -Description "Disabled mode after active P0" `
            -TimeoutSeconds 45 `
            -Predicate {
                param($status)
                Test-NetSplitDisabledStatus $status
            }
        $disableSucceeded = -not [bool]$report.DisabledStatus.tunEnabled
    }
    catch {
        if (-not $report.Failure) {
            $report.Failure = $_.Exception.ToString()
        }
        else {
            $report.Failure += [Environment]::NewLine + $_.Exception.ToString()
        }
    }

    if ($disableSucceeded -and $watchdog -and -not $watchdog.HasExited) {
        Stop-Process -Id $watchdog.Id -Force -ErrorAction SilentlyContinue
    }

    $report.CompletedAt = [DateTimeOffset]::Now
    $json = $report | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $reportPath,
        $json,
        [Text.UTF8Encoding]::new($false))
}

Write-Host "P0 report: $reportPath"
Write-Host "IPv4 stage passed: $($report.IPv4StagePassed)"
Write-Host "Full P0 passed: $($report.FullP0Passed)"
Write-Host "Split routing disabled after test: $disableSucceeded"
if ($report.Failure) {
    Write-Host "Failure: $($report.Failure)"
}

if (-not $disableSucceeded) {
    exit 3
}

if (-not $report.IPv4StagePassed) {
    exit 2
}

exit 0
