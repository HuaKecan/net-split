$script:NetSplitRpcDefaultPipeName = "net-split-control-v1"
$script:NetSplitRpcMaximumRequestBytes = 1MB
$script:NetSplitRpcMaximumResponseBytes = 4MB

function Read-NetSplitExactBytes {
    param(
        [Parameter(Mandatory)]
        [IO.Stream]$Stream,
        [Parameter(Mandatory)]
        [byte[]]$Buffer
    )

    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $count = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($count -le 0) {
            throw "The net-split pipe closed before the response was complete."
        }

        $offset += $count
    }
}

function Read-NetSplitRpcFrame {
    param(
        [Parameter(Mandatory)]
        [IO.Stream]$Stream,
        [ValidateRange(1, 16MB)]
        [int]$MaximumBytes = $script:NetSplitRpcMaximumResponseBytes
    )

    $header = New-Object byte[] 4
    Read-NetSplitExactBytes -Stream $Stream -Buffer $header
    $length = [BitConverter]::ToInt32($header, 0)
    if ($length -le 0 -or $length -gt $MaximumBytes) {
        throw "The net-split service returned an invalid frame length."
    }

    $payload = New-Object byte[] $length
    Read-NetSplitExactBytes -Stream $Stream -Buffer $payload
    return [Text.Encoding]::UTF8.GetString($payload)
}

function Write-NetSplitRpcFrame {
    param(
        [Parameter(Mandatory)]
        [IO.Stream]$Stream,
        [Parameter(Mandatory)]
        [string]$Value,
        [ValidateRange(1, 16MB)]
        [int]$MaximumBytes = $script:NetSplitRpcMaximumRequestBytes
    )

    $payload = [Text.Encoding]::UTF8.GetBytes($Value)
    if ($payload.Length -le 0 -or $payload.Length -gt $MaximumBytes) {
        throw "The net-split request is too large."
    }

    $header = [BitConverter]::GetBytes([int]$payload.Length)
    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($payload, 0, $payload.Length)
    $Stream.Flush()
}

function Send-NetSplitRpc {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Command,
        [object]$Payload = $null,
        [ValidateRange(1000, 120000)]
        [int]$ConnectTimeoutMs = 15000,
        [string]$PipeName = $script:NetSplitRpcDefaultPipeName
    )

    if ([string]::IsNullOrWhiteSpace($Command)) {
        throw "The net-split RPC command cannot be empty."
    }

    if ([string]::IsNullOrWhiteSpace($PipeName)) {
        throw "The net-split RPC pipe name cannot be empty."
    }

    $pipe = New-Object IO.Pipes.NamedPipeClientStream(
        ".",
        $PipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous,
        [Security.Principal.TokenImpersonationLevel]::Identification)
    $requestId = [guid]::NewGuid()
    try {
        try {
            $pipe.Connect($ConnectTimeoutMs)
        }
        catch [TimeoutException] {
            throw "Could not connect to the net-split service pipe '$PipeName' within $ConnectTimeoutMs ms."
        }
        catch {
            throw "Could not connect to the net-split service pipe '$PipeName': $($_.Exception.Message)"
        }

        $request = [ordered]@{
            id = $requestId
            command = $Command
            payload = $Payload
        } | ConvertTo-Json -Compress -Depth 30
        Write-NetSplitRpcFrame `
            -Stream $pipe `
            -Value $request `
            -MaximumBytes $script:NetSplitRpcMaximumRequestBytes

        $responseJson = Read-NetSplitRpcFrame `
            -Stream $pipe `
            -MaximumBytes $script:NetSplitRpcMaximumResponseBytes
        try {
            $response = $responseJson | ConvertFrom-Json
        }
        catch {
            throw "The net-split service returned malformed JSON: $($_.Exception.Message)"
        }

        if ($null -eq $response) {
            throw "The net-split service returned an empty response."
        }

        $responseId = [string]$response.id
        if (-not $responseId.Equals(
                $requestId.ToString(),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The net-split service returned a response for a different request."
        }

        if (-not [bool]$response.success) {
            $errorMessage = [string]$response.error
            if ([string]::IsNullOrWhiteSpace($errorMessage)) {
                $errorMessage = "The net-split service rejected the '$Command' command."
            }

            throw $errorMessage
        }

        return $response.data
    }
    finally {
        $pipe.Dispose()
    }
}

function Get-NetSplitStatus {
    return Send-NetSplitRpc -Command "get-status"
}

function Wait-NetSplitServiceReady {
    param(
        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds = 60,
        [ValidateRange(100, 10000)]
        [int]$PollIntervalMs = 500,
        [ValidateRange(1000, 120000)]
        [int]$ConnectTimeoutMs = 5000,
        [string]$Description = "net-split service initialization"
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastMode = "unknown"
    $lastError = ""
    do {
        $terminalError = ""
        try {
            $diagnostics = Send-NetSplitRpc `
                -Command "get-diagnostics" `
                -ConnectTimeoutMs $ConnectTimeoutMs
            $lastMode = [string]$diagnostics.runtime.mode
            $lastError = ""
            $runtimeError = [string]$diagnostics.runtime.lastError
            $readiness = [string]$diagnostics.readiness
            if ($readiness -eq "RecoveryRequired" -or $lastMode -eq "Misconfigured") {
                $terminalError = if ($runtimeError) {
                    "net-split service initialization failed: $runtimeError"
                }
                else {
                    "net-split service requires recovery before it can accept commands."
                }
            }

            if (-not $terminalError -and [bool]$diagnostics.serviceReady) {
                return $diagnostics
            }
        }
        catch {
            $lastError = $_.Exception.Message
        }

        if ($terminalError) {
            throw $terminalError
        }

        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            break
        }

        Start-Sleep -Milliseconds $PollIntervalMs
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ($lastError) {
        throw "Timed out waiting for $Description. Last mode: $lastMode. Last RPC error: $lastError"
    }

    throw "Timed out waiting for $Description. Last mode: $lastMode"
}

function Wait-NetSplitStatus {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Predicate,
        [ValidateRange(1, 900)]
        [int]$TimeoutSeconds = 60,
        [ValidateRange(100, 10000)]
        [int]$PollIntervalMs = 500,
        [ValidateRange(1000, 120000)]
        [int]$ConnectTimeoutMs = 5000,
        [string]$Description = "the requested net-split state"
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastStatus = $null
    $lastError = ""
    do {
        $statusRead = $false
        try {
            $lastStatus = Send-NetSplitRpc `
                -Command "get-status" `
                -ConnectTimeoutMs $ConnectTimeoutMs
            $lastError = ""
            $statusRead = $true
        }
        catch {
            $lastError = $_.Exception.Message
        }

        if ($statusRead -and (& $Predicate $lastStatus)) {
            return $lastStatus
        }

        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            break
        }

        Start-Sleep -Milliseconds $PollIntervalMs
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    $mode = if ($lastStatus) { [string]$lastStatus.mode } else { "unknown" }
    if ($lastError) {
        throw "Timed out waiting for $Description. Last mode: $mode. Last RPC error: $lastError"
    }

    throw "Timed out waiting for $Description. Last mode: $mode"
}

function Test-NetSplitDisabledStatus {
    param(
        [Parameter(Mandatory)]
        [object]$Status
    )

    return [string]$Status.mode -eq "Disabled" `
        -and -not [bool]$Status.enabled `
        -and -not [bool]$Status.mihomoRunning `
        -and -not [bool]$Status.tunEnabled `
        -and $Status.PSObject.Properties["dnsEnabled"] `
        -and $Status.PSObject.Properties["dnsStatusKnown"] `
        -and [bool]$Status.dnsStatusKnown `
        -and -not [bool]$Status.dnsEnabled
}

function Test-NetSplitHealthyStatus {
    param(
        [Parameter(Mandatory)]
        [object]$Status
    )

    $proxyRouteProperty = $Status.PSObject.Properties["proxyRouteAvailable"]
    return [string]$Status.mode -eq "Healthy" `
        -and [bool]$Status.enabled `
        -and [bool]$Status.mihomoRunning `
        -and [bool]$Status.tunEnabled `
        -and $Status.PSObject.Properties["dnsEnabled"] `
        -and $Status.PSObject.Properties["dnsStatusKnown"] `
        -and [bool]$Status.dnsStatusKnown `
        -and [bool]$Status.dnsEnabled `
        -and [bool]$Status.directAdapterAvailable `
        -and [bool]$Status.proxyAdapterAvailable `
        -and (-not $proxyRouteProperty -or [bool]$Status.proxyRouteAvailable)
}

function Test-NetSplitEnabledStatus {
    param(
        [Parameter(Mandatory)]
        [object]$Status
    )

    $mode = [string]$Status.mode
    return $mode -in @("Healthy", "DirectUnavailable", "ProxyUnavailable") `
        -and [bool]$Status.enabled `
        -and [bool]$Status.mihomoRunning `
        -and [bool]$Status.tunEnabled `
        -and $Status.PSObject.Properties["dnsEnabled"] `
        -and $Status.PSObject.Properties["dnsStatusKnown"] `
        -and [bool]$Status.dnsStatusKnown `
        -and [bool]$Status.dnsEnabled
}
