param(
    [string]$DirectAdapterName = "",
    [string]$ProxyAdapterName = "",
    [ValidateRange(2, 60)]
    [int]$SampleSeconds = 8,
    [string]$OutputDirectory = "",
    [switch]$RequireBindingEvidence
)

$ErrorActionPreference = "Stop"
$scriptDirectoryName = Split-Path -Leaf $PSScriptRoot
$root = if ($scriptDirectoryName.Equals("scripts", [StringComparison]::OrdinalIgnoreCase)) {
    Split-Path -Parent $PSScriptRoot
}
else {
    $PSScriptRoot
}
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")
. (Join-Path $PSScriptRoot "lib\NetSplit-Startup.ps1")

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList =
        "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" " +
        "-SampleSeconds $SampleSeconds"
    if ($DirectAdapterName) {
        $argumentList += " -DirectAdapterName `"$($DirectAdapterName.Replace('"', '\"'))`""
    }
    if ($ProxyAdapterName) {
        $argumentList += " -ProxyAdapterName `"$($ProxyAdapterName.Replace('"', '\"'))`""
    }
    if ($OutputDirectory) {
        $argumentList += " -OutputDirectory `"$($OutputDirectory.Replace('"', '\"'))`""
    }
    if ($RequireBindingEvidence) {
        $argumentList += " -RequireBindingEvidence"
    }

    $elevated = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root "artifacts\p0"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $OutputDirectory "p0-observe-$timestamp.json"

function Get-AdapterEvidence {
    param(
        [string]$Name
    )

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return [pscustomobject]@{
            Name = ""
            Exists = $false
            Error = "Adapter name is empty."
            InterfaceGuid = ""
            InterfaceIndex = 0
            Status = ""
            Description = ""
            MacAddress = ""
            LinkSpeed = ""
            Ipv4Addresses = @()
            Gateways = @()
            DnsServers = @()
            ReceivedBytes = 0
            SentBytes = 0
        }
    }

    try {
        $adapter = Get-NetAdapter -Name $Name -IncludeHidden -ErrorAction Stop |
            Select-Object -First 1
        $configuration = Get-NetIPConfiguration `
            -InterfaceIndex $adapter.ifIndex `
            -ErrorAction SilentlyContinue
        $dns = Get-DnsClientServerAddress `
            -InterfaceIndex $adapter.ifIndex `
            -AddressFamily IPv4 `
            -ErrorAction SilentlyContinue
        $statistics = Get-NetAdapterStatistics `
            -Name $adapter.Name `
            -ErrorAction SilentlyContinue
        return [pscustomobject]@{
            Name = [string]$adapter.Name
            Exists = $true
            Error = ""
            InterfaceGuid = [string]$adapter.InterfaceGuid
            InterfaceIndex = [int]$adapter.ifIndex
            Status = [string]$adapter.Status
            Description = [string]$adapter.InterfaceDescription
            MacAddress = [string]$adapter.MacAddress
            LinkSpeed = [string]$adapter.LinkSpeed
            Ipv4Addresses = @(
                $configuration.IPv4Address |
                    Where-Object { $_.IPAddress } |
                    Select-Object -ExpandProperty IPAddress)
            Gateways = @(
                $configuration.IPv4DefaultGateway |
                    Where-Object { $_.NextHop } |
                    Select-Object -ExpandProperty NextHop)
            DnsServers = @($dns.ServerAddresses)
            ReceivedBytes = if ($statistics) {
                [long]$statistics.ReceivedBytes
            }
            else {
                0
            }
            SentBytes = if ($statistics) {
                [long]$statistics.SentBytes
            }
            else {
                0
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Name = $Name
            Exists = $false
            Error = $_.Exception.Message
            InterfaceGuid = ""
            InterfaceIndex = 0
            Status = ""
            Description = ""
            MacAddress = ""
            LinkSpeed = ""
            Ipv4Addresses = @()
            Gateways = @()
            DnsServers = @()
            ReceivedBytes = 0
            SentBytes = 0
        }
    }
}

function Get-AdapterDelta {
    param(
        [Parameter(Mandatory)]
        $Before,
        [Parameter(Mandatory)]
        $After
    )

    return [pscustomobject]@{
        Name = $After.Name
        ReceivedBytes = [Math]::Max(
            0,
            [long]$After.ReceivedBytes - [long]$Before.ReceivedBytes)
        SentBytes = [Math]::Max(
            0,
            [long]$After.SentBytes - [long]$Before.SentBytes)
    }
}

function Get-LocalBinding {
    param(
        [string]$Address,
        [string[]]$DirectAddresses,
        [string[]]$ProxyAddresses,
        [string[]]$TunAddresses
    )

    if ($Address -in $DirectAddresses) {
        return "DirectAdapter"
    }
    if ($Address -in $ProxyAddresses) {
        return "ProxyAdapter"
    }
    if ($Address -in $TunAddresses) {
        return "TunAdapter"
    }
    if ($Address -in @("0.0.0.0", "::", "::1", "127.0.0.1")) {
        return "LocalOrWildcard"
    }

    return "Other"
}

$report = [ordered]@{
    SchemaVersion = 1
    StartedAt = [DateTimeOffset]::Now
    CompletedAt = $null
    SampleSeconds = $SampleSeconds
    ReadOnlyCapture = $true
    RuntimeStatus = $null
    Diagnostics = $null
    Startup = $null
    Adapters = $null
    TrafficDelta = $null
    DefaultRoutes = @()
    AdapterRoutes = @()
    DnsClient = @()
    Mihomo = $null
    TcpConnections = @()
    UdpEndpoints = @()
    ConnectionSummary = $null
    Checks = $null
    CaptureReady = $false
    BindingEvidenceObserved = $false
    Warnings = @()
    Failure = ""
}

$exitCode = 0
try {
    $status = Send-NetSplitRpc -Command "get-status" -ConnectTimeoutMs 5000
    $diagnostics = Send-NetSplitRpc -Command "get-diagnostics" -ConnectTimeoutMs 5000
    $report.RuntimeStatus = $status
    $report.Diagnostics = $diagnostics

    if (-not $DirectAdapterName) {
        $DirectAdapterName = [string]$status.directAdapterName
    }
    if (-not $ProxyAdapterName) {
        $ProxyAdapterName = [string]$status.proxyAdapterName
    }

    $installRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split"
    $serviceExe = Join-Path $installRoot "service\NetSplit.Service.exe"
    $trayExe = Join-Path $installRoot "tray\NetSplit.Tray.exe"
    $managedMihomoPath = Join-Path $installRoot "service\mihomo.exe"
    $mihomoHashPath = "$managedMihomoPath.sha256"
    $startupMarker = Join-Path `
        $env:ProgramData `
        "net-split\runtime\startup.force-disabled"
    $report.Startup = Get-NetSplitStartupSnapshot `
        -ServiceName $script:NetSplitDefaultServiceName `
        -TaskName $script:NetSplitDefaultTaskName `
        -ServiceExecutable $serviceExe `
        -TrayExecutable $trayExe `
        -UserName $identity.Name `
        -StartupDisableMarker $startupMarker

    $directBefore = Get-AdapterEvidence -Name $DirectAdapterName
    $proxyBefore = Get-AdapterEvidence -Name $ProxyAdapterName
    $tunBefore = Get-AdapterEvidence -Name "NetSplit"
    $directAddresses = @($directBefore.Ipv4Addresses)
    $proxyAddresses = @($proxyBefore.Ipv4Addresses)
    $tunAddresses = @($tunBefore.Ipv4Addresses)

    $expectedMihomoHash = if (Test-Path -LiteralPath $mihomoHashPath -PathType Leaf) {
        (Get-Content -LiteralPath $mihomoHashPath -Raw).Trim()
    }
    else {
        ""
    }
    $actualMihomoHash = if (Test-Path -LiteralPath $managedMihomoPath -PathType Leaf) {
        (Get-FileHash -LiteralPath $managedMihomoPath -Algorithm SHA256).Hash
    }
    else {
        ""
    }
    $mihomoProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name = 'mihomo.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                [IO.Path]::GetFullPath($_.ExecutablePath).Equals(
                    [IO.Path]::GetFullPath($managedMihomoPath),
                    [StringComparison]::OrdinalIgnoreCase)
            })
    $mihomoPids = @($mihomoProcesses | Select-Object -ExpandProperty ProcessId)
    $version = if (Test-Path -LiteralPath $managedMihomoPath -PathType Leaf) {
        (Get-Item -LiteralPath $managedMihomoPath).VersionInfo.FileVersion
    }
    else {
        ""
    }
    $report.Mihomo = [ordered]@{
        ExpectedPath = $managedMihomoPath
        HashManifestPath = $mihomoHashPath
        FileExists = Test-Path -LiteralPath $managedMihomoPath -PathType Leaf
        ExpectedSha256 = $expectedMihomoHash
        ActualSha256 = $actualMihomoHash
        HashMatchesExpected = [bool]$expectedMihomoHash `
            -and $actualMihomoHash.Equals(
                $expectedMihomoHash,
                [StringComparison]::OrdinalIgnoreCase)
        FileVersion = $version
        ProcessCount = $mihomoProcesses.Count
        ProcessIds = $mihomoPids
        IdentityVerified = $mihomoProcesses.Count -eq 1
    }

    $tcpByKey = @{}
    $udpByKey = @{}
    Write-Host (
        "Sampling net-split bindings for $SampleSeconds seconds. " +
        "Generate domestic and foreign traffic now if stronger evidence is needed.")
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($SampleSeconds)
    do {
        foreach ($processId in $mihomoPids) {
            foreach ($connection in @(
                    Get-NetTCPConnection `
                        -OwningProcess $processId `
                        -ErrorAction SilentlyContinue)) {
                $key = "{0}|{1}|{2}|{3}|{4}" -f `
                    $connection.LocalAddress,
                    $connection.LocalPort,
                    $connection.RemoteAddress,
                    $connection.RemotePort,
                    $connection.State
                if (-not $tcpByKey.ContainsKey($key)) {
                    $tcpByKey[$key] = [pscustomobject]@{
                        LocalAddress = [string]$connection.LocalAddress
                        LocalPort = [int]$connection.LocalPort
                        RemoteAddress = [string]$connection.RemoteAddress
                        RemotePort = [int]$connection.RemotePort
                        State = [string]$connection.State
                        Binding = Get-LocalBinding `
                            -Address ([string]$connection.LocalAddress) `
                            -DirectAddresses $directAddresses `
                            -ProxyAddresses $proxyAddresses `
                            -TunAddresses $tunAddresses
                    }
                }
            }

            foreach ($endpoint in @(
                    Get-NetUDPEndpoint `
                        -OwningProcess $processId `
                        -ErrorAction SilentlyContinue)) {
                $key = "{0}|{1}" -f $endpoint.LocalAddress, $endpoint.LocalPort
                if (-not $udpByKey.ContainsKey($key)) {
                    $udpByKey[$key] = [pscustomobject]@{
                        LocalAddress = [string]$endpoint.LocalAddress
                        LocalPort = [int]$endpoint.LocalPort
                        Binding = Get-LocalBinding `
                            -Address ([string]$endpoint.LocalAddress) `
                            -DirectAddresses $directAddresses `
                            -ProxyAddresses $proxyAddresses `
                            -TunAddresses $tunAddresses
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    $directAfter = Get-AdapterEvidence -Name $DirectAdapterName
    $proxyAfter = Get-AdapterEvidence -Name $ProxyAdapterName
    $tunAfter = Get-AdapterEvidence -Name "NetSplit"
    $report.Adapters = [ordered]@{
        Direct = $directAfter
        Proxy = $proxyAfter
        Tun = $tunAfter
    }
    $report.TrafficDelta = [ordered]@{
        Direct = Get-AdapterDelta -Before $directBefore -After $directAfter
        Proxy = Get-AdapterDelta -Before $proxyBefore -After $proxyAfter
        Tun = Get-AdapterDelta -Before $tunBefore -After $tunAfter
    }

    $observedIndexes = @(
        $directAfter.InterfaceIndex,
        $proxyAfter.InterfaceIndex,
        $tunAfter.InterfaceIndex) |
        Where-Object { $_ -gt 0 }
    $report.DefaultRoutes = @(
        Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.DestinationPrefix -eq "0.0.0.0/0" } |
            Select-Object DestinationPrefix, NextHop, InterfaceAlias,
                InterfaceIndex, RouteMetric, Protocol, State)
    $report.AdapterRoutes = @(
        Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceIndex -in $observedIndexes } |
            Select-Object DestinationPrefix, NextHop, InterfaceAlias,
                InterfaceIndex, RouteMetric, Protocol, State)
    $report.DnsClient = @(
        Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceIndex -in $observedIndexes } |
            Select-Object InterfaceAlias, InterfaceIndex, ServerAddresses)
    $report.TcpConnections = @($tcpByKey.Values | Sort-Object Binding, RemoteAddress, RemotePort)
    $report.UdpEndpoints = @($udpByKey.Values | Sort-Object Binding, LocalAddress, LocalPort)

    $directTcpCount = @(
        $report.TcpConnections |
            Where-Object { $_.Binding -eq "DirectAdapter" }).Count
    $proxyTcpCount = @(
        $report.TcpConnections |
            Where-Object { $_.Binding -eq "ProxyAdapter" }).Count
    $otherTcpCount = @(
        $report.TcpConnections |
            Where-Object { $_.Binding -eq "Other" }).Count
    $report.ConnectionSummary = [ordered]@{
        TotalTcp = $report.TcpConnections.Count
        DirectAdapterTcp = $directTcpCount
        ProxyAdapterTcp = $proxyTcpCount
        OtherTcp = $otherTcpCount
        TotalUdpEndpoints = $report.UdpEndpoints.Count
    }

    $checks = [ordered]@{
        RuntimeEnabled = Test-NetSplitEnabledStatus $status
        ServiceReady = [bool]$diagnostics.serviceReady
        StartupRegistrationHealthy = [bool]$report.Startup.RegistrationHealthy
        DirectAdapterResolved = [bool]$directAfter.Exists
        ProxyAdapterResolved = [bool]$proxyAfter.Exists
        TunAdapterUp = [bool]$tunAfter.Exists -and $tunAfter.Status -eq "Up"
        MihomoIdentityVerified = [bool]$report.Mihomo.IdentityVerified `
            -and [bool]$report.Mihomo.HashMatchesExpected
        DnsReady = [bool]$status.dnsStatusKnown -and [bool]$status.dnsEnabled
    }
    $report.Checks = $checks
    $report.CaptureReady = @($checks.Values) -notcontains $false
    $report.BindingEvidenceObserved = ($directTcpCount + $proxyTcpCount) -gt 0

    if (-not $report.BindingEvidenceObserved) {
        $report.Warnings +=
            "No Mihomo TCP connection bound to either selected physical adapter was observed during the sample."
    }
    if ($otherTcpCount -gt 0) {
        $report.Warnings +=
            "$otherTcpCount Mihomo TCP connection(s) used an unclassified local address."
    }
    if (-not $report.CaptureReady) {
        $exitCode = 2
    }
    elseif ($RequireBindingEvidence -and -not $report.BindingEvidenceObserved) {
        $exitCode = 3
    }
}
catch {
    $report.Failure = $_.Exception.ToString()
    $exitCode = 1
}
finally {
    $report.CompletedAt = [DateTimeOffset]::Now
    [IO.File]::WriteAllText(
        $reportPath,
        ($report | ConvertTo-Json -Depth 30),
        [Text.UTF8Encoding]::new($true))
}

Write-Host "P0 observation report: $reportPath"
Write-Host "Capture ready: $($report.CaptureReady)"
Write-Host "Expected adapter binding observed: $($report.BindingEvidenceObserved)"
if ($report.Warnings.Count -gt 0) {
    Write-Host "Warnings: $($report.Warnings -join ' ')"
}
if ($report.Failure) {
    Write-Host "Failure: $($report.Failure)"
}

exit $exitCode
