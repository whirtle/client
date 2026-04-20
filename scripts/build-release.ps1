#!/usr/bin/env pwsh
# Builds signed Release msixbundles for x64 and arm64.
# Requires: Connect-AzAccount or Connect-AzAccount -ServicePrincipal (for Azure Trusted Signing credentials)
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

# Locate MSBuild from the latest Visual Studio installation
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    Write-Error "vswhere.exe not found. Install Visual Studio with the 'Desktop development with C++' or 'Universal Windows Platform development' workload."
}
$vsInstall = & $vswhere -latest -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = "$vsInstall\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    Write-Error "MSBuild.exe not found at: $msbuild"
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
& $msbuild UIClientPackaging\UIClientPackaging.wapproj /t:Restore /p:Configuration=Release /verbosity:minimal
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

# Build and sign package for each architecture
$releaseDir = "UIClientPackaging\AppPackages\Release"
New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
$outputs = @{}
foreach ($arch in @('x64', 'arm64')) {
    Write-Host "==> Building Release package ($arch)"
    & $msbuild UIClientPackaging\UIClientPackaging.wapproj `
        /p:Configuration=Release `
        /p:Platform=$arch `
        /p:AppxBundle=Always `
        /p:AppxBundlePlatforms=$arch `
        /p:UapAppxPackageBuildMode=SideloadOnly `
        /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host "==> Signing ($arch)"
    $bundle = Get-ChildItem "UIClientPackaging\AppPackages\**\UIClientPackaging_*_${arch}.msixbundle" -Recurse |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $bundle) { Write-Error "Could not find built msixbundle for $arch" }

    $renamed = $bundle -replace 'UIClientPackaging_', 'Whirtle_'
    $renamedLeaf = Split-Path $renamed -Leaf
    Write-Host "Renaming: $bundle"
    Write-Host "      To: $renamedLeaf"
    Rename-Item $bundle $renamedLeaf
    $bundle = Join-Path (Split-Path $bundle -Parent) $renamedLeaf

    & $signtool sign /v /fd SHA256 `
        /tr http://timestamp.acs.microsoft.com /td SHA256 `
        /dlib $dlib /dmdf $signingMetadata `
        $bundle
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $dest = Join-Path $releaseDir (Split-Path $bundle -Leaf)
    Move-Item $bundle $dest -Force
    $outputs[$arch] = $dest
}

Write-Host ""
Write-Host "==> Build complete."
Write-Host "    x64:   $($outputs['x64'])"
Write-Host "    arm64: $($outputs['arm64'])"
