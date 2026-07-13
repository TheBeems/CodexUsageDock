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
$packageVersions = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
$originalManifest = Get-Content -LiteralPath $manifest -Raw
$versionParts = @($Version.Split('.'))
while ($versionParts.Count -lt 4) { $versionParts += '0' }
$msixVersion = $versionParts[0..3] -join '.'

function Get-CentralPackageVersion {
    param(
        [Parameter(Mandatory)]
        [string]$PackageId
    )

    $packageVersion = $packageVersions.Project.ItemGroup.PackageVersion |
        Where-Object { $_.Include -eq $PackageId } |
        Select-Object -First 1
    if ($null -eq $packageVersion -or [string]::IsNullOrWhiteSpace($packageVersion.Version)) {
        throw "No central package version is configured for $PackageId."
    }

    return [string]$packageVersion.Version
}

function Assert-SelfContainedMsix {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $requiredEntries = @(
            'CodexUsageDock.exe',
            'CodexUsageDock.runtimeconfig.json',
            'coreclr.dll',
            'hostfxr.dll',
            'hostpolicy.dll'
        )

        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $missingEntries = @($requiredEntries | Where-Object { $_ -notin $entryNames })
        if ($missingEntries.Count -gt 0) {
            throw "MSIX is missing self-contained runtime files: $($missingEntries -join ', ')."
        }

        $runtimeConfigEntry = $archive.GetEntry('CodexUsageDock.runtimeconfig.json')
        $reader = [IO.StreamReader]::new($runtimeConfigEntry.Open())
        try {
            $runtimeConfig = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }

        $includedFramework = @($runtimeConfig.runtimeOptions.includedFrameworks) |
            Where-Object { $_.name -eq 'Microsoft.NETCore.App' } |
            Select-Object -First 1
        if (-not $includedFramework) {
            throw 'MSIX runtimeconfig is framework-dependent; expected an included Microsoft.NETCore.App framework.'
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-MakeAppxPath {
    $command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $windowsSdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsSdkBin) {
        $makeAppx = Get-ChildItem -LiteralPath $windowsSdkBin -Directory |
            Sort-Object Name -Descending |
            ForEach-Object {
                Join-Path $_.FullName 'x64\makeappx.exe'
                Join-Path $_.FullName 'arm64\makeappx.exe'
            } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($makeAppx) {
            return $makeAppx
        }
    }

    throw 'makeappx.exe was not found. Install the Windows SDK before building Store packages.'
}

function Assert-MsixBundle {
    param(
        [Parameter(Mandatory)]
        [string]$BundlePath,

        [Parameter(Mandatory)]
        [string[]]$PackageNames
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($BundlePath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $missingEntries = @($PackageNames | Where-Object { $_ -notin $entryNames })
        if ($missingEntries.Count -gt 0) {
            throw "MSIX bundle is missing packages: $($missingEntries -join ', ')."
        }
    }
    finally {
        $archive.Dispose()
    }
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Get-ChildItem -LiteralPath $artifacts -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'CodexUsageDock-*.msix' -or $_.Name -like 'CodexUsageDock-*.msixbundle' -or $_.Name -like 'CodexUsageDock-*.msixupload' -or $_.Name -eq 'SHA256SUMS.txt' } |
    Remove-Item -Force

& dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

# Current MSIX BuildTools omit a runtime dependency used by their .NET MSBuild task.
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$permissionsVersion = Get-CentralPackageVersion 'System.Security.Permissions'
$msixBuildToolsVersion = Get-CentralPackageVersion 'Microsoft.Windows.SDK.BuildTools.MSIX'
$permissionsDll = Join-Path $nugetRoot "system.security.permissions\$permissionsVersion\lib\net6.0\System.Security.Permissions.dll"
$msixToolsDir = Join-Path $nugetRoot "microsoft.windows.sdk.buildtools.msix\$msixBuildToolsVersion\tools\net6.0"
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
        Assert-SelfContainedMsix -PackagePath $package.FullName
        Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $artifacts "CodexUsageDock-$Version-$platform.msix") -Force
    }
}
finally {
    [IO.File]::WriteAllText($manifest, $originalManifest, [Text.UTF8Encoding]::new($false))
}

$bundleInput = Join-Path $artifacts 'bundle-input'
$uploadInput = Join-Path $artifacts 'upload-input'
$bundle = Join-Path $artifacts "CodexUsageDock-$Version.msixbundle"
$upload = Join-Path $artifacts "CodexUsageDock-$Version.msixupload"
$packageNames = @($Platforms | ForEach-Object { "CodexUsageDock-$Version-$_.msix" })

try {
    Remove-Item -LiteralPath $bundleInput, $uploadInput -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $bundleInput, $uploadInput | Out-Null
    foreach ($packageName in $packageNames) {
        Copy-Item -LiteralPath (Join-Path $artifacts $packageName) -Destination $bundleInput
    }

    $makeAppx = Get-MakeAppxPath
    & $makeAppx bundle /d $bundleInput /p $bundle /o
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX bundle creation failed with exit code $LASTEXITCODE."
    }
    Assert-MsixBundle -BundlePath $bundle -PackageNames $packageNames

    Copy-Item -LiteralPath $bundle -Destination $uploadInput
    [IO.Compression.ZipFile]::CreateFromDirectory($uploadInput, $upload)
}
finally {
    Remove-Item -LiteralPath $bundleInput, $uploadInput -Recurse -Force -ErrorAction SilentlyContinue
}

$checksums = Get-ChildItem -LiteralPath $artifacts -File |
    Where-Object { $_.Name -like "CodexUsageDock-$Version*" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
$checksums | Set-Content -LiteralPath (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
Get-ChildItem -LiteralPath $artifacts -File | Select-Object Name, Length
