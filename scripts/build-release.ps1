[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')]
    [string]$Version,

    [ValidateSet('x64', 'arm64')]
    [string[]]$Platforms = @('x64', 'arm64'),

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'CodexUsageDock\CodexUsageDock.csproj'
$manifest = Join-Path $repoRoot 'CodexUsageDock\Package.appxmanifest'
$artifacts = Join-Path $repoRoot 'artifacts\store'
$originalManifest = Get-Content -LiteralPath $manifest -Raw
$versionParts = @($Version.Split('.'))
while ($versionParts.Count -lt 4) { $versionParts += '0' }
$msixVersion = $versionParts[0..3] -join '.'

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Get-ChildItem -LiteralPath $artifacts -Filter 'CodexUsageDock-*.msix' -ErrorAction SilentlyContinue | Remove-Item -Force

& dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

# Current MSIX BuildTools omit a runtime dependency used by their .NET MSBuild task.
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$permissionsDll = Join-Path $nugetRoot 'system.security.permissions\8.0.0\lib\net6.0\System.Security.Permissions.dll'
$msixToolsDir = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools.msix\1.7.260610101\tools\net6.0'
if (-not (Test-Path -LiteralPath $permissionsDll) -or -not (Test-Path -LiteralPath $msixToolsDir)) {
    throw 'Required MSIX build task dependencies were not restored.'
}
Copy-Item -LiteralPath $permissionsDll -Destination $msixToolsDir -Force

try {
    $identityVersionPattern = [regex]::new('(<Identity\b[\s\S]*?\bVersion=")[^"]+("\s*/>)', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $updatedManifest = $identityVersionPattern.Replace($originalManifest, "`${1}$msixVersion`${2}", 1)
    [IO.File]::WriteAllText($manifest, $updatedManifest, [Text.UTF8Encoding]::new($false))

    foreach ($platform in $Platforms) {
        $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
        $runtime = "win-$platform"
        $packageDir = Join-Path $artifacts "$platform-package"
        if (Test-Path -LiteralPath $packageDir) {
            Remove-Item -LiteralPath $packageDir -Recurse -Force
        }

        Write-Host "Building unsigned Store MSIX for $platform..." -ForegroundColor Cyan
        & dotnet publish $project `
            --configuration $Configuration `
            --runtime $runtime `
            --self-contained true `
            -p:Platform=$msbuildPlatform `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxBundle=Never `
            -p:AppxPackageSigningEnabled=false `
            -p:PackageCertificateThumbprint= `
            -p:AppxPackageDir="$packageDir\" `
            -p:PublishTrimmed=false `
            -p:Version=$Version `
            -p:InformationalVersion=$Version

        if ($LASTEXITCODE -ne 0) {
            throw "Store package build failed for $platform with exit code $LASTEXITCODE."
        }

        $package = Get-ChildItem -LiteralPath $packageDir -Recurse -Filter '*.msix' | Select-Object -First 1
        if (-not $package) {
            throw "No MSIX package was produced for $platform."
        }
        Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $artifacts "CodexUsageDock-$Version-$platform.msix") -Force
    }
}
finally {
    [IO.File]::WriteAllText($manifest, $originalManifest, [Text.UTF8Encoding]::new($false))
}

$checksums = Get-ChildItem -LiteralPath $artifacts -Filter "CodexUsageDock-$Version-*.msix" |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$checksums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $artifacts -File | Select-Object Name, Length
