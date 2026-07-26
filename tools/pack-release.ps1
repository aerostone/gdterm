# pack-release.ps1 — build portable single-folder release for gdterm
# Requires: Windows + MSBuild (VS 2017+ / Build Tools) + .NET Framework 4.6.2 targeting pack
#
# Usage (from repo root):
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1
#   powershell -ExecutionPolicy Bypass -File tools\pack-release.ps1 -Configuration Release -OutDir dist\gdterm
#
# Output: a green portable folder with Gdterm.UI.exe + deps + empty data/ skeleton (no secrets).

param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$OutDir = 'dist\gdterm',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $Root

function Find-MSBuild {
    $candidates = @(
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\MSBuild\14.0\Bin\MSBuild.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    throw 'MSBuild.exe not found. Install Visual Studio Build Tools with .NET desktop workload.'
}

$msbuild = Find-MSBuild
Write-Host "MSBuild: $msbuild"
Write-Host "Configuration: $Configuration"
Write-Host "OutDir: $OutDir"

# NuGet restore for SSH.NET / KeePassLib packages if packages.config present
$nuget = Get-Command nuget -ErrorAction SilentlyContinue
if ($nuget) {
    Write-Host 'NuGet restore...'
    Get-ChildItem -Path src -Recurse -Filter packages.config | ForEach-Object {
        & nuget restore $_.FullName -PackagesDirectory (Join-Path $Root 'packages')
    }
} else {
    Write-Host 'nuget.exe not on PATH — assuming packages already restored under packages/'
}

Write-Host 'Building solution...'
& $msbuild (Join-Path $Root 'gdterm.sln') /t:Build /p:Configuration=$Configuration /m /v:minimal
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit $LASTEXITCODE" }

if (-not $SkipTests) {
    $testExe = Join-Path $Root "src\Gdterm.Tests\bin\$Configuration\Gdterm.Tests.exe"
    if (Test-Path $testExe) {
        Write-Host "Running tests: $testExe"
        & $testExe
        if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit $LASTEXITCODE" }
    } else {
        Write-Warning "Test exe not found at $testExe — skip"
    }
}

$uiOut = Join-Path $Root "src\Gdterm.UI\bin\$Configuration"
if (-not (Test-Path (Join-Path $uiOut 'Gdterm.UI.exe'))) {
    throw "Gdterm.UI.exe not found under $uiOut"
}

if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Path $OutDir | Out-Null

Write-Host "Copying binaries from $uiOut → $OutDir"
Copy-Item -Path (Join-Path $uiOut '*') -Destination $OutDir -Recurse -Force

# Portable data skeleton (empty — no secrets)
$data = Join-Path $OutDir 'data'
New-Item -ItemType Directory -Path (Join-Path $data 'config\tools') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $data 'logs\commands') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $data 'logs\terminal') -Force | Out-Null

# Sample dangerous-commands if present in repo
$sampleDanger = Join-Path $Root 'config\dangerous-commands.json'
if (-not (Test-Path $sampleDanger)) {
    $sampleDanger = Join-Path $Root 'src\Gdterm.UI\config\dangerous-commands.json'
}
if (Test-Path $sampleDanger) {
    Copy-Item $sampleDanger (Join-Path $data 'config\dangerous-commands.json') -Force
}

# Third-party licenses (green portable attribution)
$vtLicense = Join-Path $Root 'lib\LICENSE.VtNetCore.txt'
if (Test-Path $vtLicense) {
    Copy-Item $vtLicense (Join-Path $OutDir 'LICENSE.VtNetCore.txt') -Force
}
# VtNetCore.dll is copied via UI bin Private=True reference chain when Terminal is linked.
$vtDll = Join-Path $Root 'lib\VtNetCore.dll'
if ((Test-Path $vtDll) -and -not (Test-Path (Join-Path $OutDir 'VtNetCore.dll'))) {
    Copy-Item $vtDll (Join-Path $OutDir 'VtNetCore.dll') -Force
}

# README
@'
gdterm portable
===============
1. Run Gdterm.UI.exe
2. First launch: set master password (also unlocks KeePass)
3. All portable state lives under data\ next to the exe
4. Copy the whole folder to migrate machines (keep data\ private)

Third-party: VtNetCore (MIT) — see LICENSE.VtNetCore.txt

Do NOT commit data\gdterm.kdbx or data\master-password.json to git.
'@ | Set-Content -Path (Join-Path $OutDir 'README-PORTABLE.txt') -Encoding UTF8

Write-Host ""
Write-Host "Portable package ready: $OutDir"
Write-Host "Entry: $(Join-Path $OutDir 'Gdterm.UI.exe')"
