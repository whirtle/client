#!/usr/bin/env pwsh
# Produces the final release artifacts for a tagged version:
#   UIClientPackaging/AppPackages/Release/Whirtle-x64.msixbundle
#   UIClientPackaging/AppPackages/Release/Whirtle-x64.appinstaller
#   UIClientPackaging/AppPackages/Release/Whirtle-arm64.msixbundle
#   UIClientPackaging/AppPackages/Release/Whirtle-arm64.appinstaller
#
# Filenames are intentionally version-less so the URL
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

# 2. Build + sign both architecture bundles
$buildScript = Join-Path $PSScriptRoot 'build-release.ps1'
$buildArgs = @{}
if ($SkipTests) { $buildArgs['SkipTests'] = $true }
Write-Host "==> Invoking build-release.ps1"
& $buildScript @buildArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$releaseDir = Join-Path $repoRoot 'UIClientPackaging\AppPackages\Release'
$template = Get-Content $templatePath -Raw

# 3. For each arch: rename bundle to version-less filename, emit matching .appinstaller
$artifacts = @()
foreach ($arch in @('x64', 'arm64')) {
    $built = Get-ChildItem (Join-Path $releaseDir "UIClientPackaging_*_${arch}.msixbundle") -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $built) { Write-Error "build-release.ps1 did not produce a $arch bundle in $releaseDir" }

    $finalBundle = Join-Path $releaseDir "Whirtle-$arch.msixbundle"
    if (Test-Path $finalBundle) { Remove-Item $finalBundle -Force }
    Move-Item $built $finalBundle -Force
    Write-Host "==> Renamed $arch bundle to $(Split-Path $finalBundle -Leaf)"

    $appinstaller = $template `
        -replace '\{VERSION\}', "$Version.0" `
        -replace '\{OWNER\}',   $Owner `
        -replace '\{REPO\}',    $Repo `
        -replace '\{ARCH\}',    $arch
    $finalAppinstaller = Join-Path $releaseDir "Whirtle-$arch.appinstaller"
    Set-Content -Path $finalAppinstaller -Value $appinstaller -Encoding UTF8 -NoNewline
    Write-Host "==> Wrote $(Split-Path $finalAppinstaller -Leaf)"

    $artifacts += $finalBundle
    $artifacts += $finalAppinstaller
}

Write-Host ""
Write-Host "==> Release artifacts ready:"
$artifacts | ForEach-Object { Write-Host "    $_" }

# Surface paths to GitHub Actions if running there
if ($env:GITHUB_OUTPUT) {
    "artifacts<<ARTIFACTS_EOF" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    $artifacts | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
    "ARTIFACTS_EOF" | Out-File -FilePath $env:GITHUB_OUTPUT -Append -Encoding utf8
}
