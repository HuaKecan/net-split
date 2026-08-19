$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root "scripts\publish.ps1"
$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $scriptPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null

if ($parseErrors.Count -gt 0) {
    throw "publish.ps1 has parse errors: $($parseErrors.Message -join '; ')"
}

$content = Get-Content -LiteralPath $scriptPath -Raw
$projects = @(
    "NetSplit.Service",
    "NetSplit.Tray",
    "NetSplit.Recovery"
)
foreach ($project in $projects) {
    $escapedProject = [Regex]::Escape($project)
    $pattern = "dotnet publish[\s\S]*?${escapedProject}\.csproj[\s\S]*?" +
        'if \(\$LASTEXITCODE -ne 0\)[\s\S]*?' +
        "Failed to publish ${escapedProject}"
    if ($content -notmatch $pattern) {
        throw "publish.ps1 does not fail fast when $project publishing fails."
    }
}

$publishContent = Get-Content -LiteralPath $scriptPath -Raw
foreach ($pattern in @(
    'MihomoPath must not be inside OutputRoot',
    'GeoDataDirectory must not be inside OutputRoot',
    'Mihomo executable was not found',
    'GeoDataDirectory must contain geoip.dat and geosite.dat',
    '(Test-Path -LiteralPath (Join-Path $_ "geoip.dat") -PathType Leaf) -and',
    'stagingOutput',
    'backupOutput',
    'Move-Item -LiteralPath $stagingOutput -Destination $output',
    'Failed to publish NetSplit.Service'
)) {
    if ($publishContent -notmatch [regex]::Escape($pattern)) {
        throw "publish.ps1 is missing preflight input validation: $pattern"
    }
}

$switchIndex = $publishContent.IndexOf(
    'Move-Item -LiteralPath $output -Destination $backupOutput',
    [StringComparison]::Ordinal)
$mihomoGuardIndex = $publishContent.IndexOf(
    'MihomoPath must not be inside OutputRoot',
    [StringComparison]::Ordinal)
if ($switchIndex -lt 0 -or $mihomoGuardIndex -lt 0 -or $mihomoGuardIndex -gt $switchIndex) {
    throw "publish.ps1 must validate MihomoPath before switching OutputRoot."
}

$stagingIndex = $publishContent.IndexOf(
    '$stagingOutput',
    [StringComparison]::Ordinal)
$servicePublishIndex = $publishContent.IndexOf(
    'dotnet publish (Join-Path $root "src\NetSplit.Service',
    [StringComparison]::Ordinal)
if ($stagingIndex -lt 0 -or $servicePublishIndex -lt 0 -or $stagingIndex -gt $servicePublishIndex) {
    throw "publish.ps1 must publish into staging before switching OutputRoot."
}

Write-Host "publish.ps1 exit-code checks passed."
