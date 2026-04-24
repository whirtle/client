#!/usr/bin/env pwsh
# Builds signed Release msixbundles for x64 and arm64.
# Requires: Connect-AzAccount (for Azure Trusted Signing credentials) or an ambient
# Azure credential such as the one provided by azure/login@v2 in GitHub Actions.
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# Locate VS for the MSBuild extensions the wapproj requires. We build via
# `dotnet msbuild` so we get MSBuild 18 from the .NET SDK — the wapproj refuses
# to load under MSBuild 17 (VS 2022's MSBuild).
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "vswhere.exe not found. Install Visual Studio with the 'Universal Windows Platform development' workload."
}
$vsInstall = & $vswhere -latest -property installationPath

# Copy the MSBuild extensions needed to build a wapproj into the .NET SDK
# layout (idempotent). Required under the .NET 10 SDK (MSBuild 18) because
# VS 2022 only ships MSBuild 17.
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

# Locate signtool.exe from the Windows SDK
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.0.*\x64\signtool.exe" |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) { Write-Error "signtool.exe not found. Install the Windows SDK." }

$signingMetadata = Resolve-Path "UIClientPackaging\trusted-signing.json"

$releaseDir = Join-Path (Get-Location) "UIClientPackaging\AppPackages\Release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null

# Build and sign one msixbundle per architecture. Multi-arch bundles would be
# nicer but trip up the wapproj's EntryPointExe check under the .NET 10 SDK.
foreach ($arch in @('x64', 'arm64')) {
    Write-Host "==> Building Release package ($arch)"
    dotnet msbuild UIClientPackaging\UIClientPackaging.wapproj `
        -restore `
        -p:Configuration=Release `
        -p:Platform=$arch `
        -p:AppxBundle=Always `
        -p:AppxBundlePlatforms=$arch `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:UseAppHost=true `
        -p:SelfContained=false `
        -v:minimal
    if ($LASTEXITCODE -ne 0) {
        $publishDir = "src\Whirtle.Client.UI\bin\$arch\Release\net10.0-windows10.0.19041.0\win-$arch\msixpublish"
        if (Test-Path $publishDir) {
            Write-Host "==> Contents of $publishDir after failed build:"
            Get-ChildItem $publishDir -Recurse -Force | Select-Object -ExpandProperty FullName | ForEach-Object { Write-Host "    $_" }
        } else {
            Write-Host "==> $publishDir does not exist"
        }
        exit $LASTEXITCODE
    }

    # Locate the Trusted Signing dlib (restored into ~/.nuget/packages by -restore above).
    $dlib = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.trusted.signing.client\*\bin\x64\Azure.CodeSigning.Dlib.dll" |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $dlib) { Write-Error "Azure.CodeSigning.Dlib.dll not found after restore." }

    $bundle = Get-ChildItem "UIClientPackaging\AppPackages\**\UIClientPackaging_*_${arch}.msixbundle" -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $bundle) { Write-Error "Could not find built msixbundle for $arch" }

    Write-Host "==> Signing $(Split-Path $bundle -Leaf)"
    & $signtool sign /v /fd SHA256 `
        /tr http://timestamp.acs.microsoft.com /td SHA256 `
        /dlib $dlib /dmdf $signingMetadata `
        $bundle
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $dest = Join-Path $releaseDir (Split-Path $bundle -Leaf)
    Move-Item $bundle $dest -Force
    Write-Host "    -> $dest"
}

Write-Host ""
Write-Host "==> Build complete."
