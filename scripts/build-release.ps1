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
$appxSrc = "$vsInstall\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage"
if (-not (Test-Path $appxSrc)) {
    Write-Error "AppxPackage MSBuild tasks not found at $appxSrc. Install the VS 'Universal Windows Platform development' workload."
}

# Copy the AppxPackage tasks into the .NET SDK layout MSBuild 18 looks for
# (idempotent). Required for wapproj builds under the .NET 10 SDK.
$sdkVer = (dotnet --version).Trim()
$dotnetRoot = if ($env:DOTNET_ROOT) { $env:DOTNET_ROOT } else { 'C:\Program Files\dotnet' }
$appxDst = Join-Path $dotnetRoot "sdk\$sdkVer\Microsoft\VisualStudio\v18.0\AppxPackage"
if (-not (Test-Path $appxDst)) {
    Write-Host "==> Copying AppxPackage MSBuild tasks into .NET SDK ($sdkVer)"
    New-Item -ItemType Directory -Force -Path $appxDst | Out-Null
    Copy-Item -Path "$appxSrc\*" -Destination $appxDst -Recurse
}

# Run unit tests
if (-not $SkipTests) {
    Write-Host "==> Running unit tests"
    dotnet restore tests/Whirtle.Client.Tests/Whirtle.Client.Tests.csproj
    dotnet test tests/Whirtle.Client.Tests/Whirtle.Client.Tests.csproj --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# Restore packaging project (wapproj uses NuGet via MSBuild, not dotnet restore)
Write-Host "==> Restoring UIClientPackaging"
dotnet msbuild UIClientPackaging\UIClientPackaging.wapproj /t:Restore /p:Configuration=Release /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Locate signtool.exe from the Windows SDK
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.0.*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { Write-Error "signtool.exe not found. Install the Windows SDK." }

# Locate the Trusted Signing dlib from the restored NuGet package
$dlib = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client\*\bin\x64\Azure.CodeSigning.Dlib.dll" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $dlib) { Write-Error "Azure.CodeSigning.Dlib.dll not found. Run restore first." }

$signingMetadata = Resolve-Path "UIClientPackaging\trusted-signing.json"

# Build a single multi-arch bundle (x64 + arm64)
Write-Host "==> Building Release package (x64 + arm64)"
dotnet msbuild UIClientPackaging\UIClientPackaging.wapproj `
    /p:Configuration=Release `
    /p:Platform=x64 `
    /p:AppxBundle=Always `
    /p:AppxBundlePlatforms="x64|arm64" `
    /p:UapAppxPackageBuildMode=SideloadOnly `
    /verbosity:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

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
