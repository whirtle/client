#!/usr/bin/env pwsh
# Builds a signed Release msixbundle containing x64 and arm64 payloads.
# Requires: Connect-AzAccount (for Azure Trusted Signing credentials) or an ambient
# Azure credential such as the one provided by azure/login@v2 in GitHub Actions.
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# Locate VS (for the AppxPackage MSBuild tasks) and signtool. We build via
# `dotnet msbuild` so we get MSBuild 18 from the .NET SDK — the wapproj refuses
# to load under MSBuild 17.
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "vswhere.exe not found. Install Visual Studio with the 'Universal Windows Platform development' workload."
}
$vsInstall = & $vswhere -latest -property installationPath

# Copy the MSBuild extensions needed to build a wapproj into the .NET SDK
# layout (idempotent). Required under the .NET 10 SDK (MSBuild 18) because
# VS 2022 only ships MSBuild 17 and the wapproj refuses to load under 17.
$sdkVer = (dotnet --version).Trim()
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { 'C:\Program Files\dotnet' }
$sdkMsbuildRoot = Join-Path $dotnetRoot "sdk\$sdkVer"

$msbuildExtensions = @(
    @{ Src = "$vsInstall\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage"; Dst = Join-Path $sdkMsbuildRoot 'Microsoft\VisualStudio\v18.0\AppxPackage' },
    @{ Src = "$vsInstall\MSBuild\Microsoft\DesktopBridge";                  Dst = Join-Path $sdkMsbuildRoot 'Microsoft\DesktopBridge' }
)
foreach ($ext in $msbuildExtensions) {
    if (-not (Test-Path $ext.Src)) {
        Write-Error "Required MSBuild extension not found at $($ext.Src). Install the VS 'Universal Windows Platform development' workload."
    }
    if (-not (Test-Path $ext.Dst)) {
        Write-Host "==> Copying $(Split-Path $ext.Src -Leaf) MSBuild extension into .NET SDK ($sdkVer)"
        New-Item -ItemType Directory -Force -Path $ext.Dst | Out-Null
        Copy-Item -Path "$($ext.Src)\*" -Destination $ext.Dst -Recurse
    }
}

# Run unit tests
if (-not $SkipTests) {
    Write-Host "==> Running unit tests"
    dotnet restore tests/Whirtle.Client.Tests/Whirtle.Client.Tests.csproj
    dotnet test tests/Whirtle.Client.Tests/Whirtle.Client.Tests.csproj --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Restore + build the packaging project in a single invocation.
# `-restore` runs MSBuild's Restore target first (which pulls NuGet packages for
# the wapproj — `dotnet restore` silently skips wapprojs).
Write-Host "==> Building Release package (x64 + arm64)"
dotnet msbuild UIClientPackaging\UIClientPackaging.wapproj `
    -restore `
    -p:Configuration=Release `
    -p:Platform=x64 `
    -p:AppxBundle=Always `
    -p:AppxBundlePlatforms="x64|arm64" `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Locate signtool.exe from the Windows SDK
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.0.*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { Write-Error "signtool.exe not found. Install the Windows SDK." }

# Locate the Trusted Signing dlib from the restored NuGet package
$dlib = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client\*\bin\x64\Azure.CodeSigning.Dlib.dll" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $dlib) { Write-Error "Azure.CodeSigning.Dlib.dll not found after restore." }

$signingMetadata = Resolve-Path "UIClientPackaging\trusted-signing.json"

$bundle = Get-ChildItem "UIClientPackaging\AppPackages\**\UIClientPackaging_*.msixbundle" -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $bundle) { Write-Error "Could not find built msixbundle" }

Write-Host "==> Signing $(Split-Path $bundle -Leaf)"
& $signtool sign /v /fd SHA256 `
    /tr http://timestamp.acs.microsoft.com /td SHA256 `
    /dlib $dlib /dmdf $signingMetadata `
    $bundle
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$releaseDir = "UIClientPackaging\AppPackages\Release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$dest = Join-Path $releaseDir (Split-Path $bundle -Leaf)
Move-Item $bundle $dest -Force

Write-Host ""
Write-Host "==> Build complete."
Write-Host "    bundle: $dest"
