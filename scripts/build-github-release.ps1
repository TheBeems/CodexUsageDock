[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version,

    [ValidateSet('x64', 'arm64')]
    [string[]]$Platforms = @('x64', 'arm64'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$InnoSetupPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'CodexUsageDock\CodexUsageDock.csproj'
$issFile = Join-Path $repoRoot 'installer\CodexUsageDock.iss'
$artifacts = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $artifacts 'publish'
$installerRoot = Join-Path $artifacts 'installers'

if (-not $InnoSetupPath) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $InnoSetupPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

if (-not $InnoSetupPath -or -not (Test-Path -LiteralPath $InnoSetupPath)) {
    throw 'Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup'
}

New-Item -ItemType Directory -Force -Path $publishRoot, $installerRoot | Out-Null
Get-ChildItem -LiteralPath $installerRoot -Filter "CodexUsageDock-$Version-*-setup.exe" -ErrorAction SilentlyContinue |
    Remove-Item -Force

foreach ($platform in $Platforms) {
    $runtime = "win-$platform"
    $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
    $publishDir = Join-Path $publishRoot $runtime
    $buildRoot = Join-Path $artifacts "build\$runtime"

    if (Test-Path -LiteralPath $publishDir) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force
    }

    Write-Host "Publishing $runtime..." -ForegroundColor Cyan
    & dotnet publish $project `
        --configuration $Configuration `
        --runtime $runtime `
        --self-contained true `
        --output $publishDir `
        -p:Platform=$msbuildPlatform `
        -p:DistributionMode=Installer `
        -p:BaseOutputPath="$(Join-Path $buildRoot 'bin')\" `
        -p:BaseIntermediateOutputPath="$(Join-Path $buildRoot 'obj')\" `
        -p:PublishSingleFile=false `
        -p:PublishTrimmed=false `
        -p:Version=$Version `
        -p:InformationalVersion=$Version

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $runtime with exit code $LASTEXITCODE."
    }

    $publishedExe = Join-Path $publishDir 'CodexUsageDock.exe'
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Expected executable was not produced: $publishedExe"
    }

    Write-Host "Creating $platform installer..." -ForegroundColor Cyan
    & $InnoSetupPath `
        "/DAppVersion=$Version" `
        "/DPlatform=$platform" `
        "/DSourceDir=$publishDir" `
        "/DOutputDir=$installerRoot" `
        $issFile

    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed for $platform with exit code $LASTEXITCODE."
    }

    $installer = Join-Path $installerRoot "CodexUsageDock-$Version-$platform-setup.exe"
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "Expected installer was not produced: $installer"
    }
}

$checksums = Get-ChildItem -LiteralPath $installerRoot -Filter "CodexUsageDock-$Version-*-setup.exe" |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$checksums | Set-Content -LiteralPath (Join-Path $installerRoot 'SHA256SUMS.txt') -Encoding ascii

Get-ChildItem -LiteralPath $installerRoot -File | Select-Object Name, Length
