[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'CodexUsageDock\CodexUsageDock.csproj'
$manifest = Join-Path $repoRoot 'CodexUsageDock\Package.appxmanifest'
$artifacts = Join-Path $repoRoot 'artifacts\store'
$workRoot = Join-Path $artifacts '.work'
$packageVersions = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
$originalManifestBytes = [IO.File]::ReadAllBytes($manifest)
$originalManifestText = [IO.File]::ReadAllText($manifest)
$expectedName = 'TheBeems.CodexUsageDock'
$expectedPublisher = 'CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C'
$platforms = @('x64', 'arm64')

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    [xml]$projectDocument = Get-Content -LiteralPath $ProjectPath -Raw
    $versionNode = $projectDocument.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode) {
        throw "$ProjectPath must define a three-part Version property."
    }

    $version = $versionNode.InnerText.Trim()
    if ([string]::IsNullOrWhiteSpace($version) -or $version -notmatch '^\d+\.\d+\.\d+$') {
        throw "$ProjectPath must define a three-part Version property."
    }

    return $version
}

$Version = Get-ProjectVersion -ProjectPath $project
$msixVersion = "$Version.0"
$upload = Join-Path $artifacts "CodexUsageDock-$Version.msixupload"
$checksums = Join-Path $artifacts 'SHA256SUMS.txt'
$buildCompleted = $false

function Get-XmlIdentity {
    param(
        [Parameter(Mandatory)]
        [xml]$Document
    )

    $identityNode = $Document.SelectSingleNode("/*[local-name()='Package' or local-name()='Bundle']/*[local-name()='Identity']")
    if (-not $identityNode) {
        throw 'The package identity could not be read.'
    }

    return [pscustomobject]@{
        Name = [string]$identityNode.GetAttribute('Name')
        Publisher = [string]$identityNode.GetAttribute('Publisher')
        Version = [string]$identityNode.GetAttribute('Version')
    }
}

function Read-ZipXml {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,

        [Parameter(Mandatory)]
        [string]$EntryName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry = $archive.GetEntry($EntryName)
        if (-not $entry) {
            throw "$ArchivePath does not contain $EntryName."
        }

        $reader = [IO.StreamReader]::new($entry.Open())
        try {
            return [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Set-ManifestVersion {
    param(
        [Parameter(Mandatory)]
        [string]$ManifestText,

        [Parameter(Mandatory)]
        [string]$Value
    )

    $escapedValue = [Security.SecurityElement]::Escape($Value)
    $pattern = [regex]::new(
        '(<Identity\b[\s\S]*?\bVersion=")[^"]+(")',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $pattern.IsMatch($ManifestText)) {
        throw 'Package.appxmanifest does not contain Identity Version.'
    }

    return $pattern.Replace(
        $ManifestText,
        { param($match) $match.Groups[1].Value + $escapedValue + $match.Groups[2].Value },
        1)
}

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

function Assert-Identity {
    param(
        [Parameter(Mandatory)]
        [object]$Identity,

        [Parameter(Mandatory)]
        [string]$ExpectedVersion,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Identity.Name -cne $expectedName) {
        throw "$Description identity name '$($Identity.Name)' does not match '$expectedName'."
    }
    if ($Identity.Publisher -cne $expectedPublisher) {
        throw "$Description publisher '$($Identity.Publisher)' does not match '$expectedPublisher'."
    }
    if ($Identity.Version -cne $ExpectedVersion) {
        throw "$Description version '$($Identity.Version)' does not match '$ExpectedVersion'."
    }
}

function Assert-CommandPaletteRegistrations {
    param(
        [Parameter(Mandatory)]
        [xml]$PackageManifest
    )

    $comExtension = $PackageManifest.SelectSingleNode("//*[local-name()='Extension' and @Category='windows.comServer']")
    if (-not $comExtension) {
        throw 'MSIX is missing the packaged COM server registration.'
    }

    $commandPaletteExtension = $PackageManifest.SelectSingleNode("//*[local-name()='AppExtension' and @Name='com.microsoft.commandpalette']")
    if (-not $commandPaletteExtension) {
        throw 'MSIX is missing the com.microsoft.commandpalette AppExtension registration.'
    }

    $comClass = $comExtension.SelectSingleNode(".//*[local-name()='Class']")
    $activation = $commandPaletteExtension.SelectSingleNode(".//*[local-name()='CreateInstance']")
    $comClassId = if ($comClass) { [string]$comClass.GetAttribute('Id') } else { '' }
    $activationClassId = if ($activation) { [string]$activation.GetAttribute('ClassId') } else { '' }
    if ([string]::IsNullOrWhiteSpace($comClassId) -or
        -not [string]::Equals($comClassId, $activationClassId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The packaged COM class and Command Palette activation ClassId do not match.'
    }
}

function Assert-SelfContainedMsix {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

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

    $bundleManifest = Read-ZipXml -ArchivePath $BundlePath -EntryName 'AppxMetadata/AppxBundleManifest.xml'
    Assert-Identity -Identity (Get-XmlIdentity -Document $bundleManifest) -ExpectedVersion $msixVersion -Description 'MSIX bundle'

    $packageNodes = @($bundleManifest.SelectNodes("/*[local-name()='Bundle']/*[local-name()='Packages']/*[local-name()='Package']"))
    $architectures = @($packageNodes | ForEach-Object { [string]$_.GetAttribute('Architecture') } | Sort-Object -Unique)
    $difference = @(Compare-Object -ReferenceObject @('arm64', 'x64') -DifferenceObject $architectures)
    if ($difference.Count -gt 0) {
        throw "MSIX bundle architecture mismatch. Found '$($architectures -join ', ')'; expected 'arm64, x64'."
    }
}

function Assert-MsixUpload {
    param(
        [Parameter(Mandatory)]
        [string]$UploadPath,

        [Parameter(Mandatory)]
        [string]$ExpectedBundleName
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($UploadPath)
    try {
        $files = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) })
        if ($files.Count -ne 1 -or $files[0].FullName -cne $ExpectedBundleName) {
            throw "MSIX upload must contain exactly '$ExpectedBundleName'."
        }
    }
    finally {
        $archive.Dispose()
    }
}

$sourceIdentity = Get-XmlIdentity -Document ([xml]$originalManifestText)
if ($sourceIdentity.Name -cne $expectedName -or $sourceIdentity.Publisher -cne $expectedPublisher) {
    throw "Package.appxmanifest does not match Microsoft Store product 9NFCPJXQG9FG."
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
Get-ChildItem -LiteralPath $artifacts -Force -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -eq '.work' -or
        $_.Name -like 'CodexUsageDock-*.msix' -or
        $_.Name -like 'CodexUsageDock-*.msixbundle' -or
        $_.Name -like 'CodexUsageDock-*.msixupload' -or
        $_.Name -eq 'SHA256SUMS.txt' -or
        $_.Name -like '*-package'
    } |
    Remove-Item -Recurse -Force

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
    $updatedManifest = Set-ManifestVersion -ManifestText $originalManifestText -Value $msixVersion
    [IO.File]::WriteAllText($manifest, $updatedManifest, [Text.UTF8Encoding]::new($false))

    New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
    $packageNames = @()
    foreach ($platform in $platforms) {
        $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
        $runtime = "win-$platform"
        $packageDir = Join-Path $workRoot "$platform-package"

        Write-Host "Building unsigned Microsoft Store MSIX for $platform..." -ForegroundColor Cyan
        & dotnet publish $project `
            --configuration Release `
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
        $packageManifest = Read-ZipXml -ArchivePath $package.FullName -EntryName 'AppxManifest.xml'
        Assert-Identity -Identity (Get-XmlIdentity -Document $packageManifest) -ExpectedVersion $msixVersion -Description "$platform MSIX"
        Assert-CommandPaletteRegistrations -PackageManifest $packageManifest

        $packageName = "CodexUsageDock-$Version-$platform.msix"
        Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $workRoot $packageName) -Force
        $packageNames += $packageName
    }

    $bundleInput = Join-Path $workRoot 'bundle-input'
    $uploadInput = Join-Path $workRoot 'upload-input'
    $bundleName = "CodexUsageDock-$Version.msixbundle"
    $bundle = Join-Path $workRoot $bundleName
    New-Item -ItemType Directory -Force -Path $bundleInput, $uploadInput | Out-Null
    foreach ($packageName in $packageNames) {
        Copy-Item -LiteralPath (Join-Path $workRoot $packageName) -Destination $bundleInput
    }

    $makeAppx = Get-MakeAppxPath
    & $makeAppx bundle /d $bundleInput /p $bundle /bv $msixVersion /o
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX bundle creation failed with exit code $LASTEXITCODE."
    }
    Assert-MsixBundle -BundlePath $bundle -PackageNames $packageNames

    Copy-Item -LiteralPath $bundle -Destination $uploadInput
    [IO.Compression.ZipFile]::CreateFromDirectory($uploadInput, $upload)
    Assert-MsixUpload -UploadPath $upload -ExpectedBundleName $bundleName

    $hash = (Get-FileHash -LiteralPath $upload -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($upload))" | Set-Content -LiteralPath $checksums -Encoding ascii
    $buildCompleted = $true
}
finally {
    [IO.File]::WriteAllBytes($manifest, $originalManifestBytes)
    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $buildCompleted) {
        Remove-Item -LiteralPath $upload, $checksums -Force -ErrorAction SilentlyContinue
    }
}

$restoredManifestBytes = [IO.File]::ReadAllBytes($manifest)
if ([Convert]::ToBase64String($restoredManifestBytes) -cne [Convert]::ToBase64String($originalManifestBytes)) {
    throw 'Package.appxmanifest was not restored byte-for-byte.'
}

Get-ChildItem -LiteralPath $artifacts -File | Select-Object Name, Length
