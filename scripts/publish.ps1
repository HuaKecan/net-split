param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$MihomoPath = "",
    [string]$OutputRoot = "",
    [string]$GeoDataDirectory = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = if ($OutputRoot) {
    [IO.Path]::GetFullPath($OutputRoot)
}
else {
    Join-Path $root "artifacts\$Runtime"
}
$mihomoLockPath = Join-Path $root "config\mihomo.lock.json"
$mihomoLock = Get-Content -LiteralPath $mihomoLockPath -Raw | ConvertFrom-Json

function Test-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Root
    )

    $separatorCharacters = @(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [char[]]$separatorCharacters) + [IO.Path]::DirectorySeparatorChar
    return [IO.Path]::GetFullPath($Path).StartsWith(
        $normalizedRoot,
        [StringComparison]::OrdinalIgnoreCase)
}

if (-not $MihomoPath) {
    throw "Pass -MihomoPath with the official locked Mihomo $($mihomoLock.version) executable."
}

$MihomoPath = [IO.Path]::GetFullPath($MihomoPath)
if (-not (Test-Path -LiteralPath $MihomoPath -PathType Leaf)) {
    throw "Mihomo executable was not found: $MihomoPath"
}

if (Test-PathWithin -Path $MihomoPath -Root $output) {
    throw "MihomoPath must not be inside OutputRoot because publishing replaces that directory."
}

$sourceHash = (Get-FileHash -LiteralPath $MihomoPath -Algorithm SHA256).Hash
if (-not $sourceHash.Equals(
        [string]$mihomoLock.executableSha256,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Mihomo does not match config\mihomo.lock.json."
}

$versionOutput = (& $MihomoPath -v 2>&1 | Out-String)
if ($versionOutput.IndexOf(
        "v$($mihomoLock.version)",
        [StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Mihomo version output does not match the locked version $($mihomoLock.version)."
}

if (-not $GeoDataDirectory) {
    $geoDataCandidates = @(
        (Join-Path `
            ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) `
            "Clash Verge\resources"),
        (Join-Path $env:APPDATA "io.github.clash-verge-rev.clash-verge-rev")
    )
    $GeoDataDirectory = $geoDataCandidates |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_ "geoip.dat") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_ "geosite.dat") -PathType Leaf)
        } |
        Select-Object -First 1
}

if (-not $GeoDataDirectory) {
    throw "Pass -GeoDataDirectory with geoip.dat and geosite.dat."
}

$GeoDataDirectory = [IO.Path]::GetFullPath($GeoDataDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $GeoDataDirectory "geoip.dat") -PathType Leaf) `
        -or -not (Test-Path -LiteralPath (Join-Path $GeoDataDirectory "geosite.dat") -PathType Leaf)) {
    throw "GeoDataDirectory must contain geoip.dat and geosite.dat."
}

if (Test-PathWithin -Path $GeoDataDirectory -Root $output) {
    throw "GeoDataDirectory must not be inside OutputRoot because publishing replaces that directory."
}

 $separatorCharacters = @(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$pathRoot = [IO.Path]::GetPathRoot($output)
$outputIsFilesystemRoot = [string]::IsNullOrWhiteSpace($pathRoot) `
    -or $output.TrimEnd([char[]]$separatorCharacters).Equals(
        $pathRoot.TrimEnd([char[]]$separatorCharacters),
        [StringComparison]::OrdinalIgnoreCase)
if ($outputIsFilesystemRoot) {
    throw "OutputRoot must be a child directory, not a filesystem root."
}

$outputParent = Split-Path -Parent $output
if ([string]::IsNullOrWhiteSpace($outputParent)) {
    throw "OutputRoot must have a parent directory."
}

if (Test-Path -LiteralPath $output) {
    $existingOutput = Get-Item -LiteralPath $output -Force
    if (-not $existingOutput.PSIsContainer) {
        throw "OutputRoot must be a directory."
    }

    if (($existingOutput.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "OutputRoot must not be a reparse point."
    }
}

New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
$publishId = [guid]::NewGuid().ToString("N")
$stagingOutput = Join-Path $outputParent ".net-split-staging-$publishId"
$backupOutput = Join-Path $outputParent ".net-split-backup-$publishId"
$stagingPathsEscaped = -not (Test-PathWithin -Path $stagingOutput -Root $outputParent) `
    -or -not (Test-PathWithin -Path $backupOutput -Root $outputParent)
if ($stagingPathsEscaped) {
    throw "Publish staging paths escaped the output parent directory."
}

$backupMoved = $false
$published = $false
try {
    $stagingPathExists = Test-Path -LiteralPath $stagingOutput
    $backupPathExists = Test-Path -LiteralPath $backupOutput
    if ($stagingPathExists -or $backupPathExists) {
        throw "Publish staging paths already exist."
    }

    New-Item -ItemType Directory -Path $stagingOutput -Force | Out-Null

    dotnet publish (Join-Path $root "src\NetSplit.Service\NetSplit.Service.csproj") `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $stagingOutput "service")
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish NetSplit.Service (exit code $LASTEXITCODE)."
    }

    dotnet publish (Join-Path $root "src\NetSplit.Tray\NetSplit.Tray.csproj") `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $stagingOutput "tray")
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish NetSplit.Tray (exit code $LASTEXITCODE)."
    }

    dotnet publish (Join-Path $root "src\NetSplit.Recovery\NetSplit.Recovery.csproj") `
        -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o (Join-Path $stagingOutput "recovery")
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish NetSplit.Recovery (exit code $LASTEXITCODE)."
    }

    $destination = Join-Path $stagingOutput "service\mihomo.exe"
    Copy-Item -LiteralPath $MihomoPath -Destination $destination -Force
    Set-Content -LiteralPath "$destination.sha256" `
        -Value ([string]$mihomoLock.executableSha256).ToLowerInvariant() `
        -Encoding Ascii -NoNewline

    $geoDataDestination = Join-Path $stagingOutput "service\geodata"
    New-Item -ItemType Directory -Path $geoDataDestination -Force | Out-Null
    foreach ($fileName in @("geoip.dat", "geosite.dat", "Country.mmdb")) {
        $source = Join-Path $GeoDataDirectory $fileName
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $geoDataDestination -Force
        }
    }

    if (Test-Path -LiteralPath $output) {
        Move-Item -LiteralPath $output -Destination $backupOutput
        $backupMoved = $true
    }

    Move-Item -LiteralPath $stagingOutput -Destination $output
    $published = $true

    if ($backupMoved -and (Test-Path -LiteralPath $backupOutput)) {
        try {
            Remove-Item -LiteralPath $backupOutput -Recurse -Force
            $backupMoved = $false
        }
        catch {
            Write-Warning "Could not remove publish backup directory: $backupOutput"
        }
    }
}
catch {
    $canRestoreBackup = $backupMoved `
        -and -not (Test-Path -LiteralPath $output) `
        -and (Test-Path -LiteralPath $backupOutput)
    if ($canRestoreBackup) {
        Move-Item -LiteralPath $backupOutput -Destination $output
        $backupMoved = $false
    }

    throw
}
finally {
    if (Test-Path -LiteralPath $stagingOutput) {
        try {
            Remove-Item -LiteralPath $stagingOutput -Recurse -Force
        }
        catch {
            Write-Warning "Could not remove publish staging directory: $stagingOutput"
        }
    }

    $canRemoveBackup = $published `
        -and $backupMoved `
        -and (Test-Path -LiteralPath $backupOutput)
    if ($canRemoveBackup) {
        try {
            Remove-Item -LiteralPath $backupOutput -Recurse -Force
        }
        catch {
            Write-Warning "Could not remove publish backup directory: $backupOutput"
        }
    }
}

Write-Host "Published to $output"
