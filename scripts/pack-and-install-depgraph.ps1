# Licensed to the project authors under the MIT license.
# See the LICENSE file in the project root for more information.

<#
.SYNOPSIS
    Packs RoslynDepGraph and (re)installs it as a global .NET tool named `RoslynDepGraph`.

.DESCRIPTION
    Builds a Release nupkg for src/RoslynDepGraph, stops any running `RoslynDepGraph`
    process (which would otherwise lock the tool files), uninstalls the existing
    global tool, and reinstalls it from the freshly packed package.

    NuGet source-mapping handling:
    If the user's NuGet configuration has <packageSourceMapping> enabled, the
    `--add-source` option cannot be used (NuGet rejects combining them). In that
    case the script writes a temporary NuGet config that clears all sources and
    points only at the local package folder, then installs with --configfile.
    Otherwise it installs with --add-source pointing at the local folder.

.PARAMETER Version
    Optional version to pack. Defaults to the <Version> in the csproj (0.1.0).

.PARAMETER SkipUninstall
    If set, does not uninstall the existing tool first (useful when the tool is
    not yet installed, or when you want to keep the current install).

.EXAMPLE
    ./scripts/pack-and-install-depgraph.ps1

.EXAMPLE
    ./scripts/pack-and-install-depgraph.ps1 -Version 0.1.1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipUninstall
)

$ErrorActionPreference = 'Stop'

# Resolve the repo root (the directory containing this script's parent).
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'src\RoslynDepGraph'
$projectFile = Join-Path $projectDir 'RoslynDepGraph.csproj'
$toolName = 'RoslynDepGraph'

if (-not (Test-Path $projectFile)) {
    throw "Project file not found: $projectFile"
}

# --- 1. Pack -----------------------------------------------------------------
$packDir = Join-Path $env:TEMP 'RoslynDepGraphPack'
if (Test-Path $packDir) {
    Remove-Item -Recurse -Force $packDir
}
New-Item -ItemType Directory -Force -Path $packDir | Out-Null

Write-Host "Packing $projectFile ..."
$packArgs = @('pack', $projectFile, '-c', 'Release', '-o', $packDir)
if ($Version) {
    $packArgs += @('-p:Version=' + $Version)
}
& dotnet @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed with exit code $LASTEXITCODE"
}

$nupkg = Get-ChildItem -Path $packDir -Filter '*.nupkg' | Select-Object -First 1
if (-not $nupkg) {
    throw "No .nupkg produced in $packDir"
}
Write-Host "Packed: $($nupkg.FullName)"

# --- 2. Stop any running RoslynDepGraph process (locks the tool files) ----------------
$running = Get-Process -Name 'RoslynDepGraph' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running 'RoslynDepGraph' process(es) to release file locks..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# --- 3. Uninstall the existing tool ------------------------------------------
if (-not $SkipUninstall) {
    Write-Host "Uninstalling existing global tool '$toolName' (if present)..."
    & dotnet tool uninstall --global $toolName 2>&1 | Out-Host
    # Uninstall returns non-zero if the tool was not installed; that's fine.
}

# --- 4. Reinstall from the local package -------------------------------------
# Detect whether NuGet source mapping is enabled. If so, --add-source is
# rejected, so we use a temporary config that clears sources and points only at
# the local package folder.
$nugetConfig = Join-Path $env:APPDATA 'NuGet\NuGet.Config'
$sourceMappingEnabled = $false
if (Test-Path $nugetConfig) {
    $configXml = [xml](Get-Content $nugetConfig -Raw)
    $sourceMapping = $configXml.configuration.packageSourceMapping
    if ($sourceMapping -and $sourceMapping.packageSource) {
        $sourceMappingEnabled = $true
    }
}

$installArgs = @('tool', 'install', '--global', $toolName)

if ($sourceMappingEnabled) {
    Write-Host "NuGet source mapping detected; using a temporary config to bypass it."
    $tempConfig = Join-Path $env:TEMP 'RoslynPruneNuGet.Config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packDir" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $tempConfig
    $installArgs += @('--configfile', $tempConfig)
}
else {
    Write-Host "No NuGet source mapping detected; using --add-source."
    $installArgs += @('--add-source', $packDir)
}

Write-Host "Installing global tool '$toolName'..."
& dotnet @installArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet tool install failed with exit code $LASTEXITCODE"
}

# --- 5. Clean up -------------------------------------------------------------
Remove-Item -Recurse -Force $packDir -ErrorAction SilentlyContinue
if ($sourceMappingEnabled) {
    Remove-Item -Force (Join-Path $env:TEMP 'RoslynPruneNuGet.Config') -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Done. Verify with:"
Write-Host "  RoslynDepGraph --version"
Write-Host "  RoslynDepGraph --help"
