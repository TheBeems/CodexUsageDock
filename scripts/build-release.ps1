[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'CodexUsageDock\CodexUsageDock.csproj'
$manifest = Join-Path $repoRoot 'CodexUsageDock\Package.appxmanifest'
$assets = Join-Path $repoRoot 'CodexUsageDock\Assets'
$assetGenerator = Join-Path $repoRoot 'scripts\generate-assets.ps1'
$artifacts = Join-Path $repoRoot 'artifacts\store'
$workRoot = Join-Path $artifacts '.work'
$localNugetPackages = Join-Path $workRoot 'nuget-packages'
$localIntermediateOutput = Join-Path $workRoot 'obj'
$versionedManifest = Join-Path $workRoot 'Package.appxmanifest'
$packageVersions = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Packages.props') -Raw)
$originalManifestBytes = [IO.File]::ReadAllBytes($manifest)
$originalManifestText = [IO.File]::ReadAllText($manifest)
$expectedName = 'TheBeems.CodexUsageDock'
$expectedPublisher = 'CN=F748B633-A4F0-42F4-B6F1-B5BDCAED8E0C'
$platforms = @('x64', 'arm64')
$originalNugetPackages = [Environment]::GetEnvironmentVariable('NUGET_PACKAGES', 'Process')
$requiredStoreAssets = [ordered]@{
    'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24, 24)
    'LockScreenLogo.scale-200.png' = @(48, 48)
    'Square44x44Logo.scale-200.png' = @(88, 88)
    'SmallTile.scale-200.png' = @(142, 142)
    'Square150x150Logo.scale-200.png' = @(300, 300)
    'LargeTile.scale-200.png' = @(620, 620)
    'Wide310x150Logo.scale-200.png' = @(620, 300)
    'SplashScreen.scale-200.png' = @(1240, 600)
    'StoreLogo.png' = @(50, 50)
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing

function Assert-StoreAssets {
    param(
        [Parameter(Mandatory)]
        [string]$AssetsPath
    )

    foreach ($asset in $requiredStoreAssets.GetEnumerator()) {
        $assetPath = Join-Path $AssetsPath $asset.Key
        if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) {
            throw "Required Microsoft Store asset is missing: $($asset.Key)."
        }

        $image = [System.Drawing.Image]::FromFile($assetPath)
        try {
            $expectedWidth = $asset.Value[0]
            $expectedHeight = $asset.Value[1]
            if ($image.Width -ne $expectedWidth -or $image.Height -ne $expectedHeight) {
                throw "Store asset '$($asset.Key)' is $($image.Width)x$($image.Height); expected ${expectedWidth}x${expectedHeight}."
            }
        }
        finally {
            $image.Dispose()
        }
    }
}

function Test-ImagesVisuallyEquivalent {
    param(
        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$ExpectedImage,

        [Parameter(Mandatory)]
        [System.Drawing.Bitmap]$ActualImage
    )

    $comparisonWidth = [Math]::Min(64, $ExpectedImage.Width)
    $comparisonHeight = [Math]::Min(64, $ExpectedImage.Height)
    $expectedPreview = [System.Drawing.Bitmap]::new($comparisonWidth, $comparisonHeight)
    $actualPreview = [System.Drawing.Bitmap]::new($comparisonWidth, $comparisonHeight)

    try {
        $expectedGraphics = [System.Drawing.Graphics]::FromImage($expectedPreview)
        $actualGraphics = [System.Drawing.Graphics]::FromImage($actualPreview)
        try {
            $expectedGraphics.DrawImage($ExpectedImage, 0, 0, $comparisonWidth, $comparisonHeight)
            $actualGraphics.DrawImage($ActualImage, 0, 0, $comparisonWidth, $comparisonHeight)
        }
        finally {
            $expectedGraphics.Dispose()
            $actualGraphics.Dispose()
        }

        [long]$totalChannelDifference = 0
        for ($y = 0; $y -lt $comparisonHeight; $y++) {
            for ($x = 0; $x -lt $comparisonWidth; $x++) {
                $expectedPixel = $expectedPreview.GetPixel($x, $y)
                $actualPixel = $actualPreview.GetPixel($x, $y)
                $totalChannelDifference += [Math]::Abs([int]$expectedPixel.A - [int]$actualPixel.A)
                $totalChannelDifference += [Math]::Abs([int]$expectedPixel.R - [int]$actualPixel.R)
                $totalChannelDifference += [Math]::Abs([int]$expectedPixel.G - [int]$actualPixel.G)
                $totalChannelDifference += [Math]::Abs([int]$expectedPixel.B - [int]$actualPixel.B)
            }
        }

        $meanChannelDifference = $totalChannelDifference / ($comparisonWidth * $comparisonHeight * 4)
        return $meanChannelDifference -le 1.0
    }
    finally {
        $expectedPreview.Dispose()
        $actualPreview.Dispose()
    }
}

function Assert-GeneratedAssetsMatch {
    param(
        [Parameter(Mandatory)]
        [string]$AssetsPath,

        [Parameter(Mandatory)]
        [string]$GeneratorPath,

        [Parameter(Mandatory)]
        [string]$VerificationPath
    )

    & $GeneratorPath -OutputDirectory $VerificationPath

    foreach ($assetName in $requiredStoreAssets.Keys) {
        $sourcePath = Join-Path $AssetsPath $assetName
        $generatedPath = Join-Path $VerificationPath $assetName
        $sourceImage = [System.Drawing.Bitmap]::new($sourcePath)
        $generatedImage = [System.Drawing.Bitmap]::new($generatedPath)

        try {
            if ($sourceImage.Width -ne $generatedImage.Width -or $sourceImage.Height -ne $generatedImage.Height) {
                throw "Store asset '$assetName' does not match scripts/generate-assets.ps1. Regenerate the canonical asset set before packaging."
            }

            if (-not (Test-ImagesVisuallyEquivalent -ExpectedImage $sourceImage -ActualImage $generatedImage)) {
                throw "Store asset '$assetName' does not match scripts/generate-assets.ps1. Regenerate the canonical asset set before packaging."
            }
        }
        finally {
            $sourceImage.Dispose()
            $generatedImage.Dispose()
        }
    }
}

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
$stagedUpload = Join-Path $workRoot "CodexUsageDock-$Version.msixupload"
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

    $application = $PackageManifest.SelectSingleNode("//*[local-name()='Application']")
    $targetDeviceFamily = $PackageManifest.SelectSingleNode("//*[local-name()='TargetDeviceFamily']")
    $visualElements = $PackageManifest.SelectSingleNode("//*[local-name()='VisualElements']")
    $defaultTile = if ($visualElements) { $visualElements.SelectSingleNode("./*[local-name()='DefaultTile']") } else { $null }
    $exeServer = $comExtension.SelectSingleNode(".//*[local-name()='ExeServer']")
    $comClass = $comExtension.SelectSingleNode(".//*[local-name()='Class']")
    $activation = $commandPaletteExtension.SelectSingleNode(".//*[local-name()='CreateInstance']")
    $commands = $commandPaletteExtension.SelectSingleNode(".//*[local-name()='SupportedInterfaces']/*[local-name()='Commands']")
    $applicationExecutable = if ($application) { [string]$application.GetAttribute('Executable') } else { '' }
    $comExecutable = if ($exeServer) { [string]$exeServer.GetAttribute('Executable') } else { '' }
    $comArguments = if ($exeServer) { [string]$exeServer.GetAttribute('Arguments') } else { '' }
    if ($applicationExecutable -cne 'CodexUsageDock.exe' -or $comExecutable -cne $applicationExecutable) {
        throw 'The packaged application and COM server must both activate CodexUsageDock.exe.'
    }
    if ($comArguments -cne '-RegisterProcessAsComServer') {
        throw 'The packaged COM server is missing the -RegisterProcessAsComServer argument.'
    }
    if (-not $commands) {
        throw 'MSIX is missing the Command Palette Commands provider interface.'
    }

    if (-not $targetDeviceFamily -or [string]$targetDeviceFamily.GetAttribute('MaxVersionTested') -cne '10.0.26100.0') {
        throw 'MSIX must declare MaxVersionTested 10.0.26100.0.'
    }

    $visualAssetAttributes = [ordered]@{
        Square44x44Logo   = @($visualElements, 'Assets\Square44x44Logo.png')
        Square150x150Logo = @($visualElements, 'Assets\Square150x150Logo.png')
        Square71x71Logo   = @($defaultTile, 'Assets\SmallTile.png')
        Wide310x150Logo   = @($defaultTile, 'Assets\Wide310x150Logo.png')
        Square310x310Logo = @($defaultTile, 'Assets\LargeTile.png')
    }
    foreach ($assetAttribute in $visualAssetAttributes.GetEnumerator()) {
        if (-not $assetAttribute.Value[0] -or [string]$assetAttribute.Value[0].GetAttribute($assetAttribute.Key) -cne $assetAttribute.Value[1]) {
            throw "MSIX has an unexpected or missing $($assetAttribute.Key) manifest asset reference."
        }
    }

    $comClassId = if ($comClass) { [string]$comClass.GetAttribute('Id') } else { '' }
    $activationClassId = if ($activation) { [string]$activation.GetAttribute('ClassId') } else { '' }
    if ([string]::IsNullOrWhiteSpace($comClassId) -or
        -not [string]::Equals($comClassId, $activationClassId, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The packaged COM class and Command Palette activation ClassId do not match.'
    }

    $capabilityNames = @($PackageManifest.SelectNodes("/*[local-name()='Package']/*[local-name()='Capabilities']/*") | ForEach-Object { [string]$_.GetAttribute('Name') })
    if ($capabilityNames.Count -ne 1 -or $capabilityNames[0] -cne 'runFullTrust') {
        throw "MSIX must declare only the runFullTrust capability; found: $($capabilityNames -join ', ')."
    }
}

function Assert-SelfContainedMsix {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $requiredRuntimeEntries = @(
            'CodexUsageDock.exe',
            'coreclr.dll',
            'hostfxr.dll',
            'hostpolicy.dll'
        )
        $requiredAssetEntries = @($requiredStoreAssets.Keys | ForEach-Object { "Assets/$_" })

        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $missingRuntimeEntries = @($requiredRuntimeEntries | Where-Object { $_ -notin $entryNames })
        if ($missingRuntimeEntries.Count -gt 0) {
            throw "MSIX is missing self-contained runtime files: $($missingRuntimeEntries -join ', ')."
        }

        $missingAssetEntries = @($requiredAssetEntries | Where-Object { $_ -notin $entryNames })
        if ($missingAssetEntries.Count -gt 0) {
            throw "MSIX is missing required Microsoft Store assets: $($missingAssetEntries -join ', ')."
        }

        # A self-contained single-file publish embeds runtimeconfig.json in the app bundle.
        # If the SDK emits it as a loose file, validate that it still declares the included runtime.
        $runtimeConfigEntry = $archive.GetEntry('CodexUsageDock.runtimeconfig.json')
        if ($runtimeConfigEntry) {
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

New-Item -ItemType Directory -Force -Path $workRoot, $localNugetPackages, $localIntermediateOutput | Out-Null
$updatedManifest = Set-ManifestVersion -ManifestText $originalManifestText -Value $msixVersion
[IO.File]::WriteAllText($versionedManifest, $updatedManifest, [Text.UTF8Encoding]::new($false))
$env:NUGET_PACKAGES = $localNugetPackages
try {
    Assert-StoreAssets -AssetsPath $assets
    Assert-GeneratedAssetsMatch `
        -AssetsPath $assets `
        -GeneratorPath $assetGenerator `
        -VerificationPath (Join-Path $workRoot 'generated-assets')

    & dotnet restore $project `
        -p:BaseIntermediateOutputPath="$localIntermediateOutput\" `
        -p:MSBuildProjectExtensionsPath="$localIntermediateOutput\" `
        -p:PackageAppxManifestPath="$versionedManifest"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    # Current MSIX BuildTools omit a runtime dependency used by their .NET MSBuild task.
    # Patch only this disposable, build-local package cache so the user's global cache is never mutated.
    $permissionsVersion = Get-CentralPackageVersion 'System.Security.Permissions'
    $msixBuildToolsVersion = Get-CentralPackageVersion 'Microsoft.Windows.SDK.BuildTools.MSIX'
    $permissionsDll = Join-Path $localNugetPackages "system.security.permissions\$permissionsVersion\lib\net6.0\System.Security.Permissions.dll"
    $msixToolsDir = Join-Path $localNugetPackages "microsoft.windows.sdk.buildtools.msix\$msixBuildToolsVersion\tools\net6.0"
    if (-not (Test-Path -LiteralPath $permissionsDll) -or -not (Test-Path -LiteralPath $msixToolsDir)) {
        throw 'Required MSIX build task dependencies were not restored to the isolated package cache.'
    }
    Copy-Item -LiteralPath $permissionsDll -Destination $msixToolsDir -Force

    $packageNames = @()
    foreach ($platform in $platforms) {
        $msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
        $runtime = "win-$platform"
        $packageDir = Join-Path $workRoot "$platform-package"

        Write-Host "Building unsigned Microsoft Store MSIX for $platform..." -ForegroundColor Cyan
        & dotnet publish $project `
            --no-restore `
            --configuration Release `
            --runtime $runtime `
            --self-contained true `
            -p:Platform=$msbuildPlatform `
            -p:BaseIntermediateOutputPath="$localIntermediateOutput\" `
            -p:MSBuildProjectExtensionsPath="$localIntermediateOutput\" `
            -p:PackageAppxManifestPath="$versionedManifest" `
            -p:GenerateAppxPackageOnBuild=true `
            -p:AppxBundle=Never `
            -p:AppxPackageSigningEnabled=false `
            -p:PackageCertificateThumbprint= `
            -p:AppxPackageDir="$packageDir\" `
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
    [IO.Compression.ZipFile]::CreateFromDirectory($uploadInput, $stagedUpload)
    Assert-MsixUpload -UploadPath $stagedUpload -ExpectedBundleName $bundleName

    $currentManifestBytes = [IO.File]::ReadAllBytes($manifest)
    if ([Convert]::ToBase64String($currentManifestBytes) -cne [Convert]::ToBase64String($originalManifestBytes)) {
        throw 'Package.appxmanifest changed during the release build. No release artifact was published; rerun from the updated source.'
    }

    Copy-Item -LiteralPath $stagedUpload -Destination $upload -Force
    $hash = (Get-FileHash -LiteralPath $stagedUpload -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($upload))" | Set-Content -LiteralPath $checksums -Encoding ascii
    $buildCompleted = $true
}
finally {
    if ($null -eq $originalNugetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $originalNugetPackages
    }

    Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $buildCompleted) {
        Remove-Item -LiteralPath $upload, $checksums -Force -ErrorAction SilentlyContinue
    }
}

Get-ChildItem -LiteralPath $artifacts -File | Select-Object Name, Length
