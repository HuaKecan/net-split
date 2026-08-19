param(
    [string]$PublishRoot = "",
    [string]$InstallRoot = ""
)

$ErrorActionPreference = "Stop"
$serviceName = "NetSplitService"
$taskName = "NetSplit Tray"
$firewallRuleName = "NetSplit Mihomo DNS"
$lockedMihomoHash =
    "82CD796A23492F43A71C1EC27E4E5E0B3D58932014DA5A36E79ED9B11FEE8162"
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1")
. (Join-Path $PSScriptRoot "lib\NetSplit-Startup.ps1")

if (-not $PublishRoot) {
    $PublishRoot = Join-Path $root "artifacts\win-x64"
}

if (-not $InstallRoot) {
    $InstallRoot = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
        "net-split"
}

$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$installedMihomoPath = Join-Path $InstallRoot "service\mihomo.exe"

function Test-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root
    )

    if ([string]::IsNullOrWhiteSpace($Root)) {
        return $false
    }

    $normalizedPath = [IO.Path]::GetFullPath($Path)
    $separatorCharacters = @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [char[]]$separatorCharacters) + [IO.Path]::DirectorySeparatorChar
    return $normalizedPath.StartsWith(
        $normalizedRoot,
        [StringComparison]::OrdinalIgnoreCase)
}

function Disable-SavedSplitState {
    param(
        [Parameter(Mandatory)]
        [string]$DataRoot
    )

    $settingsFile = Join-Path $DataRoot "settings.json"
    if (-not (Test-Path -LiteralPath $settingsFile -PathType Leaf)) {
        return
    }

    $settingsJson = [IO.File]::ReadAllText(
        $settingsFile,
        [Text.UTF8Encoding]::new($false, $true))
    $settings = $settingsJson | ConvertFrom-Json
    $enabledProperty = $settings.PSObject.Properties |
        Where-Object { $_.Name.Equals("enabled", [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if ($enabledProperty) {
        $enabledProperty.Value = $false
    }
    else {
        $settings | Add-Member -NotePropertyName "enabled" -NotePropertyValue $false
    }

    $tempFile = "$settingsFile.installing"
    $json = $settings | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText(
        $tempFile,
        $json,
        [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempFile -Destination $settingsFile -Force
}

function Get-SavedSplitEnabled {
    param(
        [Parameter(Mandatory)]
        [string]$DataRoot
    )

    $settingsFile = Join-Path $DataRoot "settings.json"
    if (-not (Test-Path -LiteralPath $settingsFile -PathType Leaf)) {
        return $false
    }

    $settingsJson = [IO.File]::ReadAllText(
        $settingsFile,
        [Text.UTF8Encoding]::new($false, $true))
    $settings = $settingsJson | ConvertFrom-Json
    $enabledProperty = $settings.PSObject.Properties |
        Where-Object { $_.Name.Equals("enabled", [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if (-not $enabledProperty) {
        return $false
    }

    if ($enabledProperty.Value -isnot [bool]) {
        throw "settings.json contains a non-boolean enabled value."
    }

    return [bool]$enabledProperty.Value
}

function Assert-TrustedDataRoot {
    param(
        [Parameter(Mandatory)]
        [string]$DataRoot,
        [Parameter(Mandatory)]
        [Security.Principal.SecurityIdentifier]$InstallerSid
    )

    if (-not (Test-Path -LiteralPath $DataRoot)) {
        return
    }

    $dataRootItem = Get-Item -LiteralPath $DataRoot -Force
    if (-not $dataRootItem.PSIsContainer) {
        throw "ProgramData\net-split must be a directory."
    }

    if ($dataRootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "ProgramData\net-split must not be a reparse point."
    }

    $systemSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null)
    $administratorsSid = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null)
    $ownerAccount = [Security.Principal.NTAccount]::new(
        (Get-Acl -LiteralPath $DataRoot).Owner)
    $ownerSid = $ownerAccount.Translate(
        [Security.Principal.SecurityIdentifier])
    if (-not $ownerSid.Equals($systemSid) `
            -and -not $ownerSid.Equals($administratorsSid) `
            -and -not $ownerSid.Equals($InstallerSid)) {
        throw "Existing ProgramData\net-split has an untrusted owner."
    }
}

function Disable-RunningNetSplit {
    param(
        [ValidateRange(10, 300)]
        [int]$TimeoutSeconds = 90
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $lastError = ""
    do {
        try {
            $status = Send-NetSplitRpc -Command "get-status"
            if ($status.mode -eq "Disabled" `
                    -and -not $status.enabled `
                    -and -not $status.mihomoRunning `
                    -and -not $status.tunEnabled `
                    -and -not $status.dnsEnabled) {
                return
            }

            Send-NetSplitRpc -Command "disable" | Out-Null
        }
        catch {
            $lastError = $_.Exception.Message
        }

        Start-Sleep -Milliseconds 500
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    if ([string]::IsNullOrWhiteSpace($lastError)) {
        throw "Timed out waiting for net-split to disable before replacement."
    }

    throw "Timed out waiting for net-split to disable before replacement. Last error: $lastError"
}

function Wait-ProcessPathGone {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath,
        [ValidateRange(1, 120)]
        [int]$TimeoutSeconds = 30
    )

    $normalizedPath = [IO.Path]::GetFullPath($ExecutablePath)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $processes = @(
            Get-CimInstance Win32_Process |
                Where-Object {
                    $_.ExecutablePath -and
                    $_.ExecutablePath.Equals(
                        $normalizedPath,
                        [StringComparison]::OrdinalIgnoreCase)
                }
        )
        if ($processes.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    }
    while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Process did not exit before replacement: $normalizedPath"
}

$programFilesRoots = @(
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
    [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
if (-not ($programFilesRoots | Where-Object { Test-PathWithin $InstallRoot $_ })) {
    throw "InstallRoot must be inside Program Files."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $argumentList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($PublishRoot) {
        $argumentList += " -PublishRoot `"$($PublishRoot.Replace('"', '\"'))`""
    }
    if ($InstallRoot) {
        $argumentList += " -InstallRoot `"$($InstallRoot.Replace('"', '\"'))`""
    }

    $elevated = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argumentList `
        -Wait `
        -PassThru
    exit $elevated.ExitCode
}

$systemSid = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::LocalSystemSid,
    $null)
$administratorsSid = [Security.Principal.SecurityIdentifier]::new(
    [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
    $null)
$dataRoot = Join-Path $env:ProgramData "net-split"
$runtimeDataRoot = Join-Path $dataRoot "runtime"
$startupDisableMarkerFile = Join-Path $runtimeDataRoot "startup.force-disabled"

$sourceServiceExe = Join-Path $PublishRoot "service\NetSplit.Service.exe"
$sourceTrayExe = Join-Path $PublishRoot "tray\NetSplit.Tray.exe"
$sourceRecoveryExe = Join-Path $PublishRoot "recovery\NetSplit.Recovery.exe"
$sourceMihomoExe = Join-Path $PublishRoot "service\mihomo.exe"
$sourceMihomoHashFile = "$sourceMihomoExe.sha256"
$rpcLibrarySource = Join-Path $PSScriptRoot "lib\NetSplit-Rpc.ps1"
$p0ObserveSource = Join-Path $PSScriptRoot "p0-observe.ps1"
$startupLibrarySource = Join-Path $PSScriptRoot "lib\NetSplit-Startup.ps1"
$startupRepairSource = Join-Path $PSScriptRoot "repair-startup.ps1"
$startupStatusSource = Join-Path $PSScriptRoot "startup-status.ps1"
if (-not (Test-Path -LiteralPath $sourceServiceExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $sourceTrayExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $sourceRecoveryExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $sourceMihomoExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $sourceMihomoHashFile -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $rpcLibrarySource -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $p0ObserveSource -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $startupLibrarySource -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $startupRepairSource -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $startupStatusSource -PathType Leaf)) {
    throw "Published binaries were not found. Run scripts\publish.ps1 first."
}

$expectedMihomoHash = (Get-Content -LiteralPath $sourceMihomoHashFile -Raw).Trim()
if (-not $expectedMihomoHash.Equals(
        $lockedMihomoHash,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Mihomo hash manifest does not match the locked application version."
}

$actualMihomoHash = (Get-FileHash -LiteralPath $sourceMihomoExe -Algorithm SHA256).Hash
if (-not $actualMihomoHash.Equals(
        $expectedMihomoHash,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Mihomo hash verification failed."
}

Assert-TrustedDataRoot -DataRoot $dataRoot -InstallerSid $identity.User
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$restoreSplitAfterInstall = $false
$restoreStateSource = "fresh-install"
if ($existing) {
    try {
        $preinstallStatus = Send-NetSplitRpc -Command "get-status"
        if (-not $preinstallStatus.PSObject.Properties["enabled"]) {
            throw "The running service returned a status without the enabled field."
        }

        $restoreSplitAfterInstall = [bool]$preinstallStatus.enabled
        $restoreStateSource = "runtime"
    }
    catch {
        $restoreSplitAfterInstall = Get-SavedSplitEnabled -DataRoot $dataRoot
        $restoreStateSource = "settings-fallback"
        Write-Warning (
            "Could not read the pre-install runtime state; using the saved enabled setting. " +
            "RPC error: $($_.Exception.Message)")
    }
}

if ($existing -and $existing.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
    Disable-RunningNetSplit -TimeoutSeconds 90
}

Assert-TrustedDataRoot -DataRoot $dataRoot -InstallerSid $identity.User
Disable-SavedSplitState -DataRoot $dataRoot

if ($existing -and $existing.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus(
        [ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))
    Wait-ProcessPathGone `
        -ExecutablePath (Join-Path $InstallRoot "service\NetSplit.Service.exe") `
        -TimeoutSeconds 30
}

for ($attempt = 0; $attempt -lt 40; $attempt++) {
    $mihomoProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name = 'mihomo.exe'" |
            Where-Object {
                $_.ExecutablePath -and
                $_.ExecutablePath.Equals(
                    $installedMihomoPath,
                    [StringComparison]::OrdinalIgnoreCase)
            }
    )
    if ($mihomoProcesses.Count -eq 0) {
        break
    }

    foreach ($process in $mihomoProcesses) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 250
}

$remainingMihomoProcesses = @(
    Get-CimInstance Win32_Process -Filter "Name = 'mihomo.exe'" |
        Where-Object {
            $_.ExecutablePath -and
            $_.ExecutablePath.Equals(
                $installedMihomoPath,
                [StringComparison]::OrdinalIgnoreCase)
        }
)
if ($remainingMihomoProcesses.Count -gt 0) {
    throw "The installed Mihomo process did not stop before replacement."
}

Get-Process -Name "NetSplit.Tray" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Wait-ProcessPathGone `
    -ExecutablePath (Join-Path $InstallRoot "tray\NetSplit.Tray.exe") `
    -TimeoutSeconds 30

if (Test-Path -LiteralPath $InstallRoot) {
    $installRootItem = Get-Item -LiteralPath $InstallRoot -Force
    if ($installRootItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "InstallRoot must not be a reparse point."
    }
}
else {
    New-Item -ItemType Directory -Path $InstallRoot | Out-Null
}

if (-not $PublishRoot.Equals($InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
    foreach ($directoryName in @("service", "tray", "recovery")) {
        $sourceDirectory = Join-Path $PublishRoot $directoryName
        $targetDirectory = Join-Path $InstallRoot $directoryName
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $sourceDirectory -Force |
            Copy-Item -Destination $targetDirectory -Recurse -Force
    }

    Copy-Item -LiteralPath $PSCommandPath `
        -Destination (Join-Path $InstallRoot "install.ps1") -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "uninstall.ps1") `
        -Destination (Join-Path $InstallRoot "uninstall.ps1") -Force
}

$installedP0Observe = Join-Path $InstallRoot "p0-observe.ps1"
Copy-Item -LiteralPath $p0ObserveSource `
    -Destination $installedP0Observe -Force

$installedScriptLibrary = Join-Path $InstallRoot "lib"
New-Item -ItemType Directory -Path $installedScriptLibrary -Force | Out-Null
Copy-Item -LiteralPath $rpcLibrarySource `
    -Destination (Join-Path $installedScriptLibrary "NetSplit-Rpc.ps1") -Force
Copy-Item -LiteralPath $startupLibrarySource `
    -Destination (Join-Path $installedScriptLibrary "NetSplit-Startup.ps1") -Force
Copy-Item -LiteralPath $startupRepairSource `
    -Destination (Join-Path $InstallRoot "repair-startup.ps1") -Force
Copy-Item -LiteralPath $startupStatusSource `
    -Destination (Join-Path $InstallRoot "startup-status.ps1") -Force

$serviceExe = Join-Path $InstallRoot "service\NetSplit.Service.exe"
$trayExe = Join-Path $InstallRoot "tray\NetSplit.Tray.exe"
$mihomoExe = Join-Path $InstallRoot "service\mihomo.exe"
$mihomoHashFile = "$mihomoExe.sha256"
if (-not (Test-Path -LiteralPath $serviceExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $trayExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $mihomoExe -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $mihomoHashFile -PathType Leaf)) {
    throw "Installed binaries are incomplete."
}

$installedExpectedHash = (Get-Content -LiteralPath $mihomoHashFile -Raw).Trim()
$installedActualHash = (Get-FileHash -LiteralPath $mihomoExe -Algorithm SHA256).Hash
if (-not $installedExpectedHash.Equals(
        $lockedMihomoHash,
        [StringComparison]::OrdinalIgnoreCase) `
        -or -not $installedActualHash.Equals(
            $installedExpectedHash,
            [StringComparison]::OrdinalIgnoreCase)) {
    throw "Installed Mihomo failed locked hash verification."
}

Assert-TrustedDataRoot -DataRoot $dataRoot -InstallerSid $identity.User
if (-not (Test-Path -LiteralPath $dataRoot)) {
    New-Item -ItemType Directory -Path $dataRoot | Out-Null
}

$lockAcl = [Security.AccessControl.DirectorySecurity]::new()
$lockAcl.SetAccessRuleProtection($true, $false)
$lockAcl.SetOwner($administratorsSid)
$lockInheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
$lockAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $systemSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $lockInheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow))
$lockAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administratorsSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $lockInheritance,
        [Security.AccessControl.PropagationFlags]::None,
        [Security.AccessControl.AccessControlType]::Allow))
Set-Acl -LiteralPath $dataRoot -AclObject $lockAcl

$directories = [Collections.Generic.List[string]]::new()
$files = [Collections.Generic.List[string]]::new()
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($dataRoot)
while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    foreach ($entry in Get-ChildItem -LiteralPath $current -Force) {
        if ($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw "Protected data paths must not contain reparse points: $($entry.FullName)"
        }

        if ($entry.PSIsContainer) {
            $directories.Add($entry.FullName)
            $pending.Push($entry.FullName)
        }
        else {
            $files.Add($entry.FullName)
        }
    }
}

$dataAcl = [Security.AccessControl.DirectorySecurity]::new()
$dataAcl.SetAccessRuleProtection($true, $false)
$dataAcl.SetOwner($administratorsSid)
$inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor `
    [Security.AccessControl.InheritanceFlags]::ObjectInherit
$systemRule = [Security.AccessControl.FileSystemAccessRule]::new(
    $systemSid,
    [Security.AccessControl.FileSystemRights]::FullControl,
    $inheritance,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow)
$adminRule = [Security.AccessControl.FileSystemAccessRule]::new(
    $administratorsSid,
    [Security.AccessControl.FileSystemRights]::FullControl,
    $inheritance,
    [Security.AccessControl.PropagationFlags]::None,
    [Security.AccessControl.AccessControlType]::Allow)
$dataAcl.AddAccessRule($systemRule)
$dataAcl.AddAccessRule($adminRule)
Set-Acl -LiteralPath $dataRoot -AclObject $dataAcl
foreach ($directory in $directories) {
    Set-Acl -LiteralPath $directory -AclObject $dataAcl
}

$fileAcl = [Security.AccessControl.FileSecurity]::new()
$fileAcl.SetAccessRuleProtection($true, $false)
$fileAcl.SetOwner($administratorsSid)
$fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $systemSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
$fileAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administratorsSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
foreach ($file in $files) {
    Set-Acl -LiteralPath $file -AclObject $fileAcl
}

if (-not (Test-Path -LiteralPath $runtimeDataRoot)) {
    New-Item -ItemType Directory -Path $runtimeDataRoot | Out-Null
}
Set-Acl -LiteralPath $runtimeDataRoot -AclObject $dataAcl
[IO.File]::WriteAllText(
    $startupDisableMarkerFile,
    "install",
    [Text.UTF8Encoding]::new($false))
Set-Acl -LiteralPath $startupDisableMarkerFile -AclObject $fileAcl

Set-Content -LiteralPath (Join-Path $dataRoot "authorized-user.sid") `
    -Value $identity.User.Value -Encoding Ascii -NoNewline
Disable-SavedSplitState -DataRoot $dataRoot

Get-NetFirewallRule -DisplayName "$firewallRuleName*" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
foreach ($protocol in @("TCP", "UDP")) {
    New-NetFirewallRule -DisplayName "$firewallRuleName ($protocol)" `
        -Description "Allow the locked net-split Mihomo DNS listener on loopback only." `
        -Direction Inbound -Program $mihomoExe -Protocol $protocol `
        -LocalAddress 127.0.0.1 -LocalPort 1053 -Action Allow -Profile Any | Out-Null
}

Set-NetSplitServiceStartup `
    -ServiceName $serviceName `
    -ServiceExecutable $serviceExe
Register-NetSplitTrayTask `
    -TaskName $taskName `
    -TrayExecutable $trayExe `
    -UserName $identity.Name

Start-Service -Name $serviceName
Wait-NetSplitServiceReady `
    -Description "net-split service initialization after installation" `
    -TimeoutSeconds 90 | Out-Null
Disable-RunningNetSplit -TimeoutSeconds 90
if (-not (Test-Path -LiteralPath $startupDisableMarkerFile -PathType Leaf)) {
    throw "The startup disable marker disappeared before installation completed."
}
Remove-Item -LiteralPath $startupDisableMarkerFile -Force
$restoredStatus = $null
$restoreError = ""
if ($restoreSplitAfterInstall) {
    try {
        Send-NetSplitRpc -Command "enable" | Out-Null
        $restoredStatus = Wait-NetSplitStatus `
            -Description "the pre-install enabled state" `
            -TimeoutSeconds 120 `
            -Predicate {
                param($status)
                Test-NetSplitEnabledStatus $status
            }
    }
    catch {
        $restoreError = $_.Exception.Message
        try {
            Disable-RunningNetSplit -TimeoutSeconds 90
        }
        catch {
            $restoreError += " Cleanup also failed: $($_.Exception.Message)"
        }
    }
}

Start-ScheduledTask -TaskName $taskName

if ($restoreError) {
    throw (
        "net-split was installed, but the pre-install enabled state could not be restored. " +
        "Split routing was kept disabled. Error: $restoreError")
}

if ($restoreSplitAfterInstall) {
    Write-Host (
        "net-split installed to $InstallRoot and restored split routing " +
        "($($restoredStatus.mode), state source: $restoreStateSource).")
}
else {
    Write-Host (
        "net-split installed to $InstallRoot with split routing disabled " +
        "(state source: $restoreStateSource).")
    Write-Host "Validate the configuration before enabling TUN."
}
