#!/usr/bin/env pwsh
# Produces the final release artifacts for a tagged version:
#   UIClientPackaging/AppPackages/Release/Whirtle.msixbundle
#   UIClientPackaging/AppPackages/Release/Whirtle.appinstaller
#
# Both filenames are intentionally version-less so the URL
# https://github.com/<owner>/<repo>/releases/latest/download/<filename>
# stays stable across releases (required for .appinstaller auto-update).
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$Owner = 'whirtle',
    [string]$Repo  = 'client',

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must be in the form MAJOR.MINOR.PATCH (e.g. 0.1.11); got '$Version'"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

$manifestPath = Join-Path $repoRoot 'UIClientPackaging\Package.appxmanifest'
$templatePath = Join-Path $repoRoot 'UIClientPackaging\Whirtle.appinstaller.template'

# 1. Bump Package.appxmanifest Version to "$Version.0"
Write-Host "==> Updating Package.appxmanifest Version to $Version.0"
$manifestXml = [xml](Get-Content $manifestPath)
$manifestXml.Package.Identity.Version = "$Version.0"
$manifestXml.Save($manifestPath)

# 2. Build + sign the multi-arch bundle
$buildScript = Join-Path $PSScriptRoot 'build-release.ps1'
$buildArgs = @{}
if ($SkipTests) { $buildArgs['SkipTests'] = $true }
Write-Host "==> Invoking build-release.ps1"
& $buildScript @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$releaseDir = Join-Path $repoRoot 'UIClientPackaging\AppPackages\Release'
$builtBundle = Get-ChildItem (Join-Path $releaseDir '*.msixbundle') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $builtBundle) { Write-Error "build-release.ps1 did not produce a bundle in $releaseDir" }

# 3. Rename bundle to the version-less filename expected by the .appinstaller URI
$finalBundle = Join-Path $releaseDir 'Whirtle.msixbundle'
if (Test-Path $finalBundle) { Remove-Item $finalBundle -Force }
Move-Item $builtBundle $finalBundle -Force
Write-Host "==> Renamed bundle to Whirtle.msixbundle"

# 4. Emit the .appinstaller from the template
$template = Get-Content $templatePath -Raw
$appinstaller = $template `
    -replace '\{VERSION\}', "$Version.0" `
    -replace '\{OWNER\}', $Owner `
    -replace '\{REPO\}', $Repo
$finalAppinstaller = Join-Path $releaseDir 'Whirtle.appinstaller'
Set-Content -Path $finalAppinstaller -Value $appinstaller -Encoding UTF8 -NoNewline
Write-Host "==> Wrote Whirtle.appinstaller"

Write-Host ""
Write-Host "==> Release artifacts ready:"
Write-Host "    bundle:       $finalBundle"
Write-Host "    appinstaller: $finalAppinstaller"

# Surface paths to GitHub Actions if running there
if ($env:GITHUB_OUTPUT) {
    "bundle=$finalBundle"             | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "appinstaller=$finalAppinstaller" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
