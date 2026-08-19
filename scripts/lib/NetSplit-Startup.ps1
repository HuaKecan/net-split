$script:NetSplitDefaultServiceName = "NetSplitService"
$script:NetSplitDefaultTaskName = "NetSplit Tray"

function Invoke-NetSplitSc {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$ErrorMessage
    )

    & sc.exe @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (exit code $LASTEXITCODE)."
    }
}

function Test-NetSplitPathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }

    $separatorCharacters = @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [char[]]$separatorCharacters) + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Path).StartsWith(
        $normalizedRoot,
        [StringComparison]::OrdinalIgnoreCase)
}

function Set-NetSplitServiceStartup {
    param(
        [string]$ServiceName = $script:NetSplitDefaultServiceName,
        [Parameter(Mandatory)]
        [string]$ServiceExecutable
    )

    if (-not (Test-Path -LiteralPath $ServiceExecutable -PathType Leaf)) {
        throw "NetSplit service executable was not found: $ServiceExecutable"
    }

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $binaryPath = "`"$ServiceExecutable`""
    if ($service) {
        Invoke-NetSplitSc `
            -Arguments @(
                "config",
                $ServiceName,
                "binPath=",
                $binaryPath,
                "start=",
                "delayed-auto",
                "obj=",
                "LocalSystem") `
            -ErrorMessage "Failed to update the net-split service registration"
    }
    else {
        Invoke-NetSplitSc `
            -Arguments @(
                "create",
                $ServiceName,
                "binPath=",
                $binaryPath,
                "start=",
                "delayed-auto",
                "obj=",
                "LocalSystem",
                "DisplayName=",
                "NetSplit Service") `
            -ErrorMessage "Failed to create the net-split service"
    }

    Invoke-NetSplitSc `
        -Arguments @(
            "description",
            $ServiceName,
            "Dual-NIC Mihomo TUN split routing service") `
        -ErrorMessage "Failed to set the net-split service description"

    Invoke-NetSplitSc `
        -Arguments @(
            "failure",
            $ServiceName,
            "reset=",
            "86400",
            "actions=",
            "restart/5000/restart/15000/restart/60000") `
        -ErrorMessage "Failed to configure net-split service recovery"
}

function Register-NetSplitTrayTask {
    param(
        [string]$TaskName = $script:NetSplitDefaultTaskName,
        [Parameter(Mandatory)]
        [string]$TrayExecutable,
        [Parameter(Mandatory)]
        [string]$UserName
    )

    if (-not (Test-Path -LiteralPath $TrayExecutable -PathType Leaf)) {
        throw "NetSplit tray executable was not found: $TrayExecutable"
    }

    if ([string]::IsNullOrWhiteSpace($UserName)) {
        throw "The interactive user name cannot be empty."
    }

    $action = New-ScheduledTaskAction `
        -Execute $TrayExecutable `
        -Argument "--background"
    $trigger = New-ScheduledTaskTrigger `
        -AtLogOn `
        -User $UserName
    $trigger.Delay = "PT15S"
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -RestartCount 5 `
        -RestartInterval ([TimeSpan]::FromMinutes(1)) `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -Priority 7
    $taskPrincipal = New-ScheduledTaskPrincipal `
        -UserId $UserName `
        -LogonType Interactive `
        -RunLevel Limited
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -Principal $taskPrincipal `
        -Description "net-split user tray; starts after logon and retries after transient failures." `
        -Force | Out-Null
}

function Get-NetSplitXmlText {
    param(
        [Parameter(Mandatory)]
        [xml]$Xml,
        [Parameter(Mandatory)]
        [string]$Path
    )

    $namespace = [Xml.XmlNamespaceManager]::new($Xml.NameTable)
    $namespace.AddNamespace("t", $Xml.DocumentElement.NamespaceURI)
    $node = $Xml.SelectSingleNode($Path, $namespace)
    if ($node) {
        return [string]$node.InnerText
    }

    return ""
}

function Get-NetSplitExecutableFromServicePath {
    param([string]$PathName)

    if ([string]::IsNullOrWhiteSpace($PathName)) {
        return ""
    }

    if ($PathName -match '^\s*"([^"]+)"') {
        return $matches[1]
    }

    $exeIndex = $PathName.IndexOf(
        ".exe",
        [StringComparison]::OrdinalIgnoreCase)
    if ($exeIndex -ge 0) {
        return $PathName.Substring(0, $exeIndex + 4).Trim()
    }

    return ($PathName -split "\s+", 2)[0]
}

function ConvertTo-NetSplitHexResult {
    param([object]$Value)

    if ($null -eq $Value) {
        return ""
    }

    try {
        return "0x{0:X8}" -f ([uint32]$Value)
    }
    catch {
        return [string]$Value
    }
}

function Get-NetSplitStartupSnapshot {
    param(
        [string]$ServiceName = $script:NetSplitDefaultServiceName,
        [string]$TaskName = $script:NetSplitDefaultTaskName,
        [Parameter(Mandatory)]
        [string]$ServiceExecutable,
        [Parameter(Mandatory)]
        [string]$TrayExecutable,
        [string]$UserName = "",
        [string]$StartupDisableMarker = ""
    )

    $issues = [Collections.Generic.List[string]]::new()
    $service = Get-CimInstance Win32_Service `
        -Filter "Name = '$ServiceName'" `
        -ErrorAction SilentlyContinue
    $serviceController = Get-Service `
        -Name $ServiceName `
        -ErrorAction SilentlyContinue
    $servicePath = Get-NetSplitExecutableFromServicePath `
        -PathName ([string]$service.PathName)
    $servicePathMatches = $servicePath.Equals(
        [IO.Path]::GetFullPath($ServiceExecutable),
        [StringComparison]::OrdinalIgnoreCase)
    $serviceRegistrationHealthy = $null -ne $service `
        -and $servicePathMatches `
        -and [string]$service.StartMode -eq "Auto" `
        -and [bool]$service.DelayedAutoStart
    if (-not $serviceRegistrationHealthy) {
        $issues.Add("Windows service registration is missing or does not match the installed executable/start mode.")
    }

    $task = Get-ScheduledTask `
        -TaskName $TaskName `
        -ErrorAction SilentlyContinue
    $taskInfo = $null
    if ($task) {
        $taskInfo = Get-ScheduledTaskInfo `
            -TaskName $TaskName `
            -ErrorAction SilentlyContinue
    }
    $taskXml = $null
    if ($task) {
        try {
            $taskXml = [xml]($task | Export-ScheduledTask)
        }
        catch {
            $issues.Add("The Windows tray task XML could not be read.")
        }
    }

    $taskAction = ""
    $taskArguments = ""
    $taskTriggerUser = ""
    $taskPrincipalUser = ""
    $taskEnabledText = ""
    $taskDelay = ""
    $taskStartWhenAvailable = ""
    $taskRestartCount = ""
    $taskRestartInterval = ""
    if ($taskXml) {
        $taskAction = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Actions/t:Exec/t:Command"
        $taskArguments = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Actions/t:Exec/t:Arguments"
        $taskTriggerUser = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Triggers/t:LogonTrigger/t:UserId"
        $taskPrincipalUser = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Principals/t:Principal/t:UserId"
        $taskEnabledText = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Settings/t:Enabled"
        $taskDelay = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Triggers/t:LogonTrigger/t:Delay"
        $taskStartWhenAvailable = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Settings/t:StartWhenAvailable"
        $taskRestartCount = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Settings/t:RestartOnFailure/t:Count"
        $taskRestartInterval = Get-NetSplitXmlText `
            -Xml $taskXml `
            -Path "/t:Task/t:Settings/t:RestartOnFailure/t:Interval"
    }

    $expectedUserLeaf = $UserName
    if ($UserName -match "\\") {
        $expectedUserLeaf = $UserName.Split("\")[-1]
    }
    $taskUserMatches = [string]::IsNullOrWhiteSpace($UserName) `
        -or $taskTriggerUser.Equals($UserName, [StringComparison]::OrdinalIgnoreCase) `
        -or $taskPrincipalUser.Equals($UserName, [StringComparison]::OrdinalIgnoreCase) `
        -or $taskPrincipalUser.Equals($expectedUserLeaf, [StringComparison]::OrdinalIgnoreCase)
    $taskRegistrationHealthy = $null -ne $task `
        -and $taskEnabledText.Equals("true", [StringComparison]::OrdinalIgnoreCase) `
        -and $taskAction.Equals(
            [IO.Path]::GetFullPath($TrayExecutable),
            [StringComparison]::OrdinalIgnoreCase) `
        -and $taskArguments.Trim().Equals("--background", [StringComparison]::OrdinalIgnoreCase) `
        -and $taskDelay.Equals("PT15S", [StringComparison]::OrdinalIgnoreCase) `
        -and $taskStartWhenAvailable.Equals("true", [StringComparison]::OrdinalIgnoreCase) `
        -and $taskRestartCount -eq "5" `
        -and $taskRestartInterval.Equals("PT1M", [StringComparison]::OrdinalIgnoreCase) `
        -and $taskUserMatches
    if (-not $taskRegistrationHealthy) {
        $issues.Add("Windows tray task is missing or does not match the expected user/action/retry policy.")
    }

    $trayProcesses = @(
        Get-CimInstance Win32_Process `
            -Filter "Name = 'NetSplit.Tray.exe'" `
            -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.Equals(
                    [IO.Path]::GetFullPath($TrayExecutable),
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    $startupMarkerActive = -not [string]::IsNullOrWhiteSpace($StartupDisableMarker) `
        -and (Test-Path -LiteralPath $StartupDisableMarker -PathType Leaf)
    $serviceState = "Missing"
    if ($serviceController) {
        $serviceState = [string]$serviceController.Status
    }

    $taskState = "Missing"
    $taskEnabled = $false
    if ($task -and $taskEnabledText) {
        $taskState = [string]$task.State
        $taskEnabled = $taskEnabledText.Equals(
            "true",
            [StringComparison]::OrdinalIgnoreCase)
    }

    $lastRunTime = $null
    $lastTaskResult = ""
    if ($taskInfo) {
        if ($taskInfo.LastRunTime) {
            $lastRunTime = $taskInfo.LastRunTime.ToString("o")
        }
        $lastTaskResult = ConvertTo-NetSplitHexResult $taskInfo.LastTaskResult
    }

    return [pscustomobject]@{
        CapturedAt = [DateTimeOffset]::UtcNow.ToString("o")
        RegistrationHealthy = $serviceRegistrationHealthy -and $taskRegistrationHealthy
        Issues = $issues.ToArray()
        StartupDisableActive = $startupMarkerActive
        Service = [pscustomobject]@{
            Name = $ServiceName
            Exists = $null -ne $service
            State = $serviceState
            StartMode = [string]$service.StartMode
            DelayedAutoStart = [bool]$service.DelayedAutoStart
            PathName = [string]$service.PathName
            ExecutablePath = $servicePath
            ExecutableMatches = $servicePathMatches
        }
        TrayTask = [pscustomobject]@{
            Name = $TaskName
            Registered = $null -ne $task
            State = $taskState
            Enabled = $taskEnabled
            TriggerUser = $taskTriggerUser
            PrincipalUser = $taskPrincipalUser
            Action = $taskAction
            Arguments = $taskArguments
            LogonDelay = $taskDelay
            StartWhenAvailable = $taskStartWhenAvailable
            RestartCount = $taskRestartCount
            RestartInterval = $taskRestartInterval
            LastRunTime = $lastRunTime
            LastTaskResult = $lastTaskResult
        }
        TrayProcess = [pscustomobject]@{
            Count = $trayProcesses.Count
            ProcessIds = @($trayProcesses | Select-Object -ExpandProperty ProcessId)
            Running = $trayProcesses.Count -gt 0
        }
    }
}
