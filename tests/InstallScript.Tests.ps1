$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root "scripts\install.ps1"
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors)

if ($parseErrors.Count -gt 0) {
    throw "install.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

$content = Get-Content -LiteralPath $scriptPath -Raw
$requiredPatterns = @(
    '\$InstallRoot\s*=\s*Join-Path',
    'SpecialFolder\]::ProgramFiles',
    '\$serviceExe\s*=\s*Join-Path\s+\$InstallRoot',
    '\$trayExe\s*=\s*Join-Path\s+\$InstallRoot',
    '\$mihomoExe\s*=\s*Join-Path\s+\$InstallRoot',
    'Disable-SavedSplitState -DataRoot \$dataRoot',
    'function Get-SavedSplitEnabled',
    'Start-ScheduledTask -TaskName \$taskName',
    'Get-CimInstance Win32_Process',
    'installedMihomoPath',
    'Disable-RunningNetSplit',
    'Assert-TrustedDataRoot -DataRoot \$dataRoot',
    'startup\.force-disabled',
    'Set-Acl -LiteralPath \$startupDisableMarkerFile',
    'Remove-Item -LiteralPath \$startupDisableMarkerFile -Force',
    'NetSplit-Rpc\.ps1',
    'p0-observe\.ps1',
    'NetSplit-Startup\.ps1',
    'Set-NetSplitServiceStartup',
    'Register-NetSplitTrayTask',
    'repair-startup\.ps1',
    'startup-status\.ps1',
    'Copy-Item[\s\S]*\$startupRepairSource',
    'Copy-Item[\s\S]*\$startupStatusSource',
    'Copy-Item -LiteralPath \$rpcLibrarySource[\s\S]*NetSplit-Rpc\.ps1',
    '\$installedP0Observe\s*=\s*Join-Path\s+\$InstallRoot\s+"p0-observe\.ps1"',
    'Copy-Item -LiteralPath \$p0ObserveSource[\s\S]*\$installedP0Observe',
    'function Wait-ProcessPathGone',
    'Wait-ProcessPathGone[\s\S]*NetSplit\.Service\.exe',
    'Wait-ProcessPathGone[\s\S]*NetSplit\.Tray\.exe',
    'lib\\NetSplit-Rpc\.ps1',
    'Start-Service -Name \$serviceName[\s\S]*?Disable-RunningNetSplit',
    'Send-NetSplitRpc -Command "enable"',
    'Test-NetSplitEnabledStatus',
    'pre-install enabled state could not be restored',
    '\$existing = Get-Service[\s\S]*?Disable-SavedSplitState -DataRoot \$dataRoot[\s\S]*?Stop-Service -Name \$serviceName',
    'InstallRoot must be inside Program Files'
)
foreach ($pattern in $requiredPatterns) {
    if ($content -notmatch $pattern) {
        throw "install.ps1 is missing required installed-path behavior: $pattern"
    }
}

if ($content -match '\$serviceExe\s*=\s*Join-Path\s+\$PublishRoot') {
    throw "The Windows service must not be registered from PublishRoot."
}

if ($content -match 'Start-Process -FilePath \$trayExe') {
    throw "The tray must be launched through its limited scheduled task."
}

$markerWrite = [regex]::Match(
    $content,
    '\[IO\.File\]::WriteAllText\(\s*\$startupDisableMarkerFile,',
    [Text.RegularExpressions.RegexOptions]::Singleline)
$serviceStart = [regex]::Match(
    $content,
    '(?m)^\s*Start-Service -Name \$serviceName\s*$')
$markerRemoval = [regex]::Match(
    $content,
    '(?m)^\s*Remove-Item -LiteralPath \$startupDisableMarkerFile -Force\s*$')
if (-not $markerWrite.Success `
        -or -not $serviceStart.Success `
        -or -not $markerRemoval.Success `
        -or $markerWrite.Index -ge $serviceStart.Index `
        -or $serviceStart.Index -ge $markerRemoval.Index) {
    throw "The startup disable marker must be written before service start and removed only afterward."
}

$savedDisableCalls = [regex]::Matches(
    $content,
    '(?m)^\s*Disable-SavedSplitState -DataRoot \$dataRoot\s*$')
if ($savedDisableCalls.Count -lt 2) {
    throw "Saved split state must be disabled both before replacement and after ACL hardening."
}

$restoreSnapshot = [regex]::Match(
    $content,
    '(?m)^\s*\$restoreSplitAfterInstall\s*=\s*\$false\s*$')
$earlyTrustCheck = [regex]::Match(
    $content,
    '(?m)^\s*Assert-TrustedDataRoot -DataRoot \$dataRoot -InstallerSid \$identity\.User\s*$')
$firstDisable = [regex]::Match(
    $content,
    '(?m)^\s*Disable-RunningNetSplit -TimeoutSeconds 90\s*$')
$restoreEnable = [regex]::Match(
    $content,
    '(?m)^\s*Send-NetSplitRpc -Command "enable" \| Out-Null\s*$')
if (-not $earlyTrustCheck.Success `
        -or -not $restoreSnapshot.Success `
        -or -not $firstDisable.Success `
        -or -not $restoreEnable.Success `
        -or $earlyTrustCheck.Index -ge $restoreSnapshot.Index `
        -or $restoreSnapshot.Index -ge $firstDisable.Index `
        -or $markerRemoval.Index -ge $restoreEnable.Index) {
    throw "Install state must be captured before disable and restored only after marker removal."
}

$disableFunction = $ast.Find(
    {
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -eq "Disable-SavedSplitState"
    },
    $true)
if (-not $disableFunction) {
    throw "Disable-SavedSplitState could not be loaded for its UTF-8 regression test."
}

Invoke-Expression $disableFunction.Extent.Text
$savedEnabledFunction = $ast.Find(
    {
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -eq "Get-SavedSplitEnabled"
    },
    $true)
if (-not $savedEnabledFunction) {
    throw "Get-SavedSplitEnabled could not be loaded for its regression test."
}

Invoke-Expression $savedEnabledFunction.Extent.Text
$tempRoot = Join-Path `
    ([IO.Path]::GetTempPath()) `
    "net-split-install-tests-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $settingsFile = Join-Path $tempRoot "settings.json"
    $directAdapterName =
        [string][char]0x4EE5 + [char]0x592A + [char]0x7F51
    $proxyAdapterName = "$directAdapterName 3"
    $sampleSettings = [ordered]@{
        schemaVersion = 2
        enabled = $true
        directAdapter = [ordered]@{
            lastKnownName = $directAdapterName
        }
        proxyAdapter = [ordered]@{
            lastKnownName = $proxyAdapterName
        }
    }
    $sampleJson = $sampleSettings | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText(
        $settingsFile,
        $sampleJson,
        [Text.UTF8Encoding]::new($false))

    if (-not (Get-SavedSplitEnabled -DataRoot $tempRoot)) {
        throw "Get-SavedSplitEnabled did not preserve the pre-install enabled intent."
    }

    Disable-SavedSplitState -DataRoot $tempRoot

    $roundTripJson = [IO.File]::ReadAllText(
        $settingsFile,
        [Text.UTF8Encoding]::new($false, $true))
    $roundTrip = $roundTripJson | ConvertFrom-Json
    if ($roundTrip.enabled -ne $false `
            -or $roundTrip.directAdapter.lastKnownName -ne $directAdapterName `
            -or $roundTrip.proxyAdapter.lastKnownName -ne $proxyAdapterName) {
        throw "Disable-SavedSplitState did not preserve no-BOM UTF-8 settings."
    }

    if (Get-SavedSplitEnabled -DataRoot $tempRoot) {
        throw "Get-SavedSplitEnabled did not observe the disabled saved state."
    }

    $roundTrip.enabled = "true"
    [IO.File]::WriteAllText(
        $settingsFile,
        ($roundTrip | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    try {
        Get-SavedSplitEnabled -DataRoot $tempRoot | Out-Null
        throw "Get-SavedSplitEnabled accepted a non-boolean enabled value."
    }
    catch {
        if ($_.Exception.Message -notmatch "non-boolean enabled") {
            throw
        }
    }
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

Write-Host "install.ps1 installed-path checks passed."
