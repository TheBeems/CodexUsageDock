[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Architecture = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "ARM64" } else { "x64" }),

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$Register
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Write-Check {
    param(
        [ValidateSet("PASS", "WARN", "FAIL", "INFO")]
        [string]$Status,
        [string]$Message
    )

    $color = switch ($Status) {
        "PASS" { "Green" }
        "WARN" { "Yellow" }
        "FAIL" { "Red" }
        default { "Cyan" }
    }

    Write-Host "[$Status] $Message" -ForegroundColor $color
}

function Add-Failure {
    param([string]$Message)

    $failures.Add($Message)
    Write-Check -Status FAIL -Message $Message
}

function Add-Warning {
    param([string]$Message)

    $warnings.Add($Message)
    Write-Check -Status WARN -Message $Message
}

function Get-AttributeValue {
    param(
        [System.Xml.XmlNode]$Node,
        [string]$Name
    )

    if ($null -eq $Node -or $null -eq $Node.Attributes[$Name]) {
        return $null
    }

    return $Node.Attributes[$Name].Value
}

function Test-SamePath {
    param(
        [AllowNull()]
        [string]$First,

        [AllowNull()]
        [string]$Second
    )

    if ([string]::IsNullOrWhiteSpace($First) -or [string]::IsNullOrWhiteSpace($Second)) {
        return $false
    }

    try {
        $firstPath = [IO.Path]::GetFullPath($First).TrimEnd('\', '/')
        $secondPath = [IO.Path]::GetFullPath($Second).TrimEnd('\', '/')
        return $firstPath.Equals($secondPath, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Test-PathWithin {
    param(
        [AllowNull()]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
        $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
        if ($fullPath.Equals($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        return $fullPath.StartsWith("$fullParent$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Test-CommandPaletteManifest {
    param(
        [string]$Path,
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Failure "$Label was not found at '$Path'."
        return $null
    }

    try {
        [xml]$manifest = Get-Content -LiteralPath $Path -Raw
    }
    catch {
        Add-Failure "$Label is not valid XML: $($_.Exception.Message)"
        return $null
    }

    $namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $namespaces.AddNamespace("uap3", "http://schemas.microsoft.com/appx/manifest/uap/windows10/3")
    $namespaces.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10")
    $namespaces.AddNamespace("rescap", "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities")

    $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
    $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application", $namespaces)
    $exeServer = if ($null -ne $application) { $application.SelectSingleNode("f:Extensions/com:Extension[@Category='windows.comServer']/com:ComServer/com:ExeServer", $namespaces) } else { $null }
    $comClass = if ($null -ne $exeServer) { $exeServer.SelectSingleNode("com:Class", $namespaces) } else { $null }
    $appExtension = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExtension']/uap3:AppExtension[@Name='com.microsoft.commandpalette']", $namespaces)
    $activation = if ($null -ne $appExtension) { $appExtension.SelectSingleNode("uap3:Properties/f:CmdPalProvider/f:Activation/f:CreateInstance", $namespaces) } else { $null }
    $commands = if ($null -ne $appExtension) { $appExtension.SelectSingleNode("uap3:Properties/f:CmdPalProvider/f:SupportedInterfaces/f:Commands", $namespaces) } else { $null }
    $runFullTrust = $manifest.SelectSingleNode("/f:Package/f:Capabilities/rescap:Capability[@Name='runFullTrust']", $namespaces)
    $targetDeviceFamily = $manifest.SelectSingleNode("/f:Package/f:Dependencies/f:TargetDeviceFamily", $namespaces)
    $visualElements = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application/uap:VisualElements", $namespaces)
    $defaultTile = if ($null -ne $visualElements) { $visualElements.SelectSingleNode("uap:DefaultTile", $namespaces) } else { $null }

    $packageName = Get-AttributeValue -Node $identity -Name "Name"
    $comClassId = Get-AttributeValue -Node $comClass -Name "Id"
    $activationClassId = Get-AttributeValue -Node $activation -Name "ClassId"
    $applicationExecutable = Get-AttributeValue -Node $application -Name "Executable"
    if ($applicationExecutable -ceq '$targetnametoken$.exe') {
        $applicationExecutable = 'CodexUsageDock.exe'
    }
    $comExecutable = Get-AttributeValue -Node $exeServer -Name "Executable"
    if ($comExecutable -ceq '$targetnametoken$.exe') {
        $comExecutable = 'CodexUsageDock.exe'
    }
    $releaseFacts = [ordered]@{
        Publisher            = Get-AttributeValue -Node $identity -Name "Publisher"
        MaxVersionTested     = Get-AttributeValue -Node $targetDeviceFamily -Name "MaxVersionTested"
        Capabilities         = @($manifest.SelectNodes("/f:Package/f:Capabilities/*", $namespaces) | ForEach-Object { "$(Get-AttributeValue -Node $_ -Name 'Name')|$($_.NamespaceURI)" } | Sort-Object)
        ApplicationExecutable = $applicationExecutable
        ComExecutable         = $comExecutable
        Square44x44Logo      = Get-AttributeValue -Node $visualElements -Name "Square44x44Logo"
        Square150x150Logo    = Get-AttributeValue -Node $visualElements -Name "Square150x150Logo"
        Square71x71Logo      = Get-AttributeValue -Node $defaultTile -Name "Square71x71Logo"
        Wide310x150Logo      = Get-AttributeValue -Node $defaultTile -Name "Wide310x150Logo"
        Square310x310Logo    = Get-AttributeValue -Node $defaultTile -Name "Square310x310Logo"
    }

    if ([string]::IsNullOrWhiteSpace($packageName)) {
        Add-Failure "$Label has no package identity name."
    }

    $hasExpectedExecutable = $releaseFacts['ApplicationExecutable'] -ceq 'CodexUsageDock.exe' -and
        $releaseFacts['ComExecutable'] -ceq 'CodexUsageDock.exe'
    if (-not $hasExpectedExecutable -or $null -eq $exeServer -or (Get-AttributeValue -Node $exeServer -Name "Arguments") -ne "-RegisterProcessAsComServer") {
        Add-Failure "$Label does not contain the expected out-of-process COM server registration."
    }

    if ([string]::IsNullOrWhiteSpace($comClassId) -or [string]::IsNullOrWhiteSpace($activationClassId)) {
        Add-Failure "$Label is missing the COM class or Command Palette activation CLSID."
    }
    elseif ($comClassId -ine $activationClassId) {
        Add-Failure "$Label uses different COM and Command Palette activation CLSIDs."
    }

    if ($null -eq $appExtension) {
        Add-Failure "$Label does not register the com.microsoft.commandpalette AppExtension."
    }

    if ($null -eq $commands) {
        Add-Failure "$Label does not declare the Commands provider interface."
    }

    if ($null -eq $runFullTrust) {
        Add-Failure "$Label does not declare the runFullTrust capability required by the packaged COM server."
    }

    $hasRequiredReleaseFacts = $true
    if ([string]::IsNullOrWhiteSpace($releaseFacts['Publisher'])) {
        $hasRequiredReleaseFacts = $false
        Add-Failure "$Label is missing its package publisher."
    }

    if ($releaseFacts['MaxVersionTested'] -cne '10.0.26100.0') {
        $hasRequiredReleaseFacts = $false
        Add-Failure "$Label must target Windows MaxVersionTested 10.0.26100.0."
    }

    $capabilities = @($releaseFacts['Capabilities'])
    if ($capabilities.Count -ne 1 -or $capabilities[0] -notlike 'runFullTrust|*') {
        $hasRequiredReleaseFacts = $false
        Add-Failure "$Label must declare only the runFullTrust capability."
    }

    $expectedVisualAssets = [ordered]@{
        Square44x44Logo   = 'Assets\Square44x44Logo.png'
        Square150x150Logo = 'Assets\Square150x150Logo.png'
        Square71x71Logo   = 'Assets\SmallTile.png'
        Wide310x150Logo   = 'Assets\Wide310x150Logo.png'
        Square310x310Logo = 'Assets\LargeTile.png'
    }
    foreach ($assetFact in $expectedVisualAssets.GetEnumerator()) {
        if ($releaseFacts[$assetFact.Key] -cne $assetFact.Value) {
            $hasRequiredReleaseFacts = $false
            Add-Failure "$Label has an unexpected or missing $($assetFact.Key) tile reference."
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($packageName) -and
        -not [string]::IsNullOrWhiteSpace($comClassId) -and
        $comClassId -ieq $activationClassId -and
        $hasExpectedExecutable -and
        $null -ne $appExtension -and
        $null -ne $commands -and
        $null -ne $runFullTrust -and
        $hasRequiredReleaseFacts) {
        Write-Check -Status PASS -Message "$Label has a consistent package, COM, and Command Palette registration."
    }

    return [pscustomobject]@{
        PackageName     = $packageName
        ClassId         = $comClassId
        ReleaseFactsJson = $releaseFacts | ConvertTo-Json -Depth 3 -Compress
    }
}

function Test-ArtifactFreshness {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactPath,

        [Parameter(Mandatory)]
        [System.IO.FileInfo[]]$Inputs,

        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $ArtifactPath -PathType Leaf)) {
        return
    }

    $artifact = Get-Item -LiteralPath $ArtifactPath
    $latestInput = $Inputs | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -ne $latestInput -and $artifact.LastWriteTimeUtc -lt $latestInput.LastWriteTimeUtc) {
        Add-Failure "$Label is stale: '$($latestInput.Name)' is newer. Rebuild $Architecture/$Configuration before running the integration check."
    }
    else {
        Write-Check -Status PASS -Message "$Label is current relative to its source inputs."
    }
}

function Find-CommandPaletteExtensions {
    $windowsPowerShell = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
    if (-not (Test-Path -LiteralPath $windowsPowerShell -PathType Leaf)) {
        throw "Windows PowerShell is unavailable at '$windowsPowerShell'."
    }

    $catalogScript = @'
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$catalog = [Windows.ApplicationModel.AppExtensions.AppExtensionCatalog, Windows.ApplicationModel.AppExtensions, ContentType=WindowsRuntime]::Open("com.microsoft.commandpalette")
$extensions = @($catalog.FindAll())
[pscustomobject]@{
    Extensions = @($extensions | ForEach-Object {
        [pscustomobject]@{
            Id          = $_.Id
            DisplayName = $_.DisplayName
            PackageName = $_.Package.Id.Name
        }
    })
} | ConvertTo-Json -Depth 4 -Compress
'@

    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($catalogScript))
    $output = @(& $windowsPowerShell -NoLogo -NoProfile -NonInteractive -EncodedCommand $encodedCommand 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw ($output -join [Environment]::NewLine)
    }

    $json = $output -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "AppExtensionCatalog returned no result."
    }

    return $json | ConvertFrom-Json
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "CodexUsageDock\CodexUsageDock.csproj"
$projectDirectory = Split-Path -Parent $projectPath
$assetsDirectory = Join-Path $projectDirectory "Assets"
$assetGeneratorPath = Join-Path $repoRoot "scripts\generate-assets.ps1"
$sourceManifestPath = Join-Path $repoRoot "CodexUsageDock\Package.appxmanifest"
$extensionSourcePath = Join-Path $repoRoot "CodexUsageDock\CodexUsageDock.cs"
$rid = if ($Architecture -eq "ARM64") { "win-arm64" } else { "win-x64" }

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$targetFrameworkNode = $project.SelectSingleNode("/Project/PropertyGroup/TargetFramework")
$targetFramework = if ($null -ne $targetFrameworkNode) { $targetFrameworkNode.InnerText } else { $null }
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "The target framework could not be read from '$projectPath'."
}

$generatedManifestPath = Join-Path $repoRoot "CodexUsageDock\bin\$Architecture\$Configuration\$targetFramework\$rid\AppxManifest.xml"
$generatedOutputDirectory = Split-Path -Parent $generatedManifestPath
$generatedExecutablePath = Join-Path $generatedOutputDirectory "CodexUsageDock.exe"
$generatedInstallLocation = [IO.Path]::GetFullPath($generatedOutputDirectory).TrimEnd('\', '/')
$trustedDevelopmentRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'bin')).TrimEnd('\', '/')
$osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

Write-Host "Codex Usage Dock integration preflight" -ForegroundColor Cyan
Write-Check -Status INFO -Message "Host architecture: $osArchitecture; artifact: $Architecture/$Configuration ($rid)."

if ($Architecture -eq "ARM64" -and $osArchitecture -ne "Arm64") {
    Add-Failure "An ARM64 package cannot be registered on the $osArchitecture host. Run this check on Windows ARM64."
}

$sourceFacts = Test-CommandPaletteManifest -Path $sourceManifestPath -Label "Source manifest"
$generatedFacts = Test-CommandPaletteManifest -Path $generatedManifestPath -Label "Generated manifest"

if (Test-Path -LiteralPath $generatedExecutablePath -PathType Leaf) {
    Write-Check -Status PASS -Message "Generated COM server executable is present."
}
else {
    Add-Failure "Generated COM server executable was not found at '$generatedExecutablePath'."
}

$requiredRuntimeFiles = @(
    'CodexUsageDock.runtimeconfig.json'
    'coreclr.dll'
    'hostfxr.dll'
    'hostpolicy.dll'
)
foreach ($runtimeFile in $requiredRuntimeFiles) {
    $runtimePath = Join-Path $generatedOutputDirectory $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf) -or (Get-Item -LiteralPath $runtimePath).Length -eq 0) {
        Add-Failure "Generated self-contained runtime file '$runtimeFile' is missing or empty. Rebuild the selected artifact."
    }
}

$runtimeConfigPath = Join-Path $generatedOutputDirectory 'CodexUsageDock.runtimeconfig.json'
if (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) {
    try {
        $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
        $includedFramework = @($runtimeConfig.runtimeOptions.includedFrameworks) |
            Where-Object { $_.name -eq 'Microsoft.NETCore.App' } |
            Select-Object -First 1
        if ($null -eq $includedFramework) {
            Add-Failure "Generated runtimeconfig is framework-dependent; expected an included Microsoft.NETCore.App framework."
        }
        else {
            Write-Check -Status PASS -Message "Generated runtimeconfig describes a self-contained Microsoft.NETCore.App framework."
        }
    }
    catch {
        Add-Failure "Generated runtimeconfig is invalid: $($_.Exception.Message)"
    }
}

$sharedBuildInputs = @(
    Join-Path $repoRoot 'Directory.Build.props'
    Join-Path $repoRoot 'Directory.Packages.props'
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object { Get-Item -LiteralPath $_ }
$manifestInputs = @(
    Get-Item -LiteralPath $projectPath
    Get-Item -LiteralPath $sourceManifestPath
    Get-Item -LiteralPath $assetGeneratorPath
    $sharedBuildInputs
    Get-ChildItem -LiteralPath $assetsDirectory -File
)
$binaryInputs = @(
    Get-Item -LiteralPath $projectPath
    Get-Item -LiteralPath (Join-Path $projectDirectory 'app.manifest')
    $sharedBuildInputs
    Get-ChildItem -LiteralPath $projectDirectory -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
)
Test-ArtifactFreshness -ArtifactPath $generatedManifestPath -Inputs $manifestInputs -Label "Generated manifest"
Test-ArtifactFreshness -ArtifactPath $generatedExecutablePath -Inputs $binaryInputs -Label "Generated COM server"
$generatedAssetsMatch = $true
foreach ($sourceAsset in Get-ChildItem -LiteralPath $assetsDirectory -Filter '*.png' -File) {
    $generatedAssetPath = Join-Path (Join-Path $generatedOutputDirectory 'Assets') $sourceAsset.Name
    if (-not (Test-Path -LiteralPath $generatedAssetPath -PathType Leaf)) {
        $generatedAssetsMatch = $false
        Add-Failure "Generated asset '$($sourceAsset.Name)' is missing. Rebuild the selected artifact."
        continue
    }

    if ((Get-FileHash -LiteralPath $sourceAsset.FullName -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $generatedAssetPath -Algorithm SHA256).Hash) {
        $generatedAssetsMatch = $false
        Add-Failure "Generated asset '$($sourceAsset.Name)' does not match its source. Rebuild the selected artifact."
    }
}
if ($generatedAssetsMatch) {
    Write-Check -Status PASS -Message "Generated package assets match their canonical source files."
}

$extensionSource = Get-Content -LiteralPath $extensionSourcePath -Raw
$guidMatch = [regex]::Match($extensionSource, '(?s)\[Guid\("(?<Guid>[0-9a-fA-F-]{36})"\)\]\s*public\s+sealed\s+partial\s+class\s+CodexUsageDock')
if (-not $guidMatch.Success) {
    Add-Failure "The CodexUsageDock class GUID could not be read from '$extensionSourcePath'."
}
else {
    $csharpClassId = $guidMatch.Groups["Guid"].Value
    if ($null -ne $sourceFacts -and $sourceFacts.ClassId -ine $csharpClassId) {
        Add-Failure "The CodexUsageDock C# GUID does not match the source manifest CLSID."
    }
    elseif ($null -ne $generatedFacts -and $generatedFacts.ClassId -ine $csharpClassId) {
        Add-Failure "The CodexUsageDock C# GUID does not match the generated manifest CLSID."
    }
    else {
        Write-Check -Status PASS -Message "The CodexUsageDock C# GUID matches the packaged COM and activation CLSID."
    }
}

if ($null -ne $sourceFacts -and $null -ne $generatedFacts) {
    if ($sourceFacts.PackageName -ine $generatedFacts.PackageName) {
        Add-Failure "Source and generated manifests use different package identity names."
    }

    if ($sourceFacts.ClassId -ine $generatedFacts.ClassId) {
        Add-Failure "Source and generated manifests use different Command Palette CLSIDs."
    }

    if ($sourceFacts.ReleaseFactsJson -cne $generatedFacts.ReleaseFactsJson) {
        Add-Failure "Source and generated manifests differ in publisher, MaxVersionTested, capabilities, or tile references. Rebuild the selected artifact."
    }
}

$packageName = if ($null -ne $generatedFacts -and -not [string]::IsNullOrWhiteSpace($generatedFacts.PackageName)) {
    $generatedFacts.PackageName
}
elseif ($null -ne $sourceFacts) {
    $sourceFacts.PackageName
}
else {
    $null
}

if ($Register) {
    if ($null -eq $generatedFacts) {
        Add-Failure "Registration was requested, but the generated manifest is unavailable or invalid."
    }
    elseif ($failures.Count -gt 0) {
        Add-Failure "Registration was skipped because the preflight checks failed."
    }
    else {
        $existingPackages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
        $productionPackage = $existingPackages | Where-Object { -not $_.IsDevelopmentMode } | Select-Object -First 1
        if ($null -ne $productionPackage) {
            Add-Failure "Registration was refused because non-development package '$($productionPackage.PackageFullName)' is installed. Use an isolated test user or VM; this script never replaces a Store install."
        }
        else {
            $developmentPackages = @($existingPackages | Where-Object { $_.IsDevelopmentMode })
            $foreignDevelopmentPackage = $developmentPackages | Where-Object {
                -not (Test-PathWithin -Path $_.InstallLocation -Parent $trustedDevelopmentRoot)
            } | Select-Object -First 1

            if ($null -ne $foreignDevelopmentPackage) {
                Add-Failure "Registration was refused because development package '$($foreignDevelopmentPackage.PackageFullName)' belongs to a location outside this repository's build output. Remove it deliberately or use an isolated test user."
            }
            else {
                try {
                    if ($developmentPackages | Where-Object { Test-SamePath -First $_.InstallLocation -Second $generatedInstallLocation }) {
                        Write-Check -Status INFO -Message "Refreshing the existing development registration in place at the selected output path."
                    }
                    elseif ($developmentPackages.Count -gt 0) {
                        Write-Check -Status INFO -Message "Switching the existing repository development registration in place to the selected output path."
                    }

                    Write-Check -Status INFO -Message "Registering the generated development manifest for the current user because -Register was explicitly supplied."
                    Add-AppxPackage -Register $generatedManifestPath -ForceUpdateFromAnyVersion -ForceApplicationShutdown
                    Write-Check -Status PASS -Message "Development package registration completed."
                }
                catch {
                    Add-Failure "Development package registration failed: $($_.Exception.Message)"
                }
            }
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($packageName)) {
    $registeredPackages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
    if ($registeredPackages.Count -eq 0) {
        Add-Failure "Package '$packageName' is not registered for the current user. Build it and rerun with -Register in an isolated test environment."
    }
    else {
        $expectedPackageArchitecture = if ($Architecture -eq "ARM64") { "Arm64" } else { "X64" }
        $matchingArchitecture = @($registeredPackages | Where-Object { $_.Architecture.ToString() -ieq $expectedPackageArchitecture })
        if ($matchingArchitecture.Count -eq 0) {
            Add-Failure "The registered package architecture does not match requested architecture '$Architecture'."
        }

        $matchingSelectedBuild = @($matchingArchitecture | Where-Object {
            $_.IsDevelopmentMode -and (Test-SamePath -First $_.InstallLocation -Second $generatedInstallLocation)
        })
        if ($matchingSelectedBuild.Count -eq 0) {
            Add-Failure "The selected $Architecture/$Configuration build directory is not the registered development package."
        }

        foreach ($package in $registeredPackages) {
            $packageDescription = "Registered package: $($package.PackageFullName); architecture=$($package.Architecture); development=$($package.IsDevelopmentMode); status=$($package.Status)."
            if ($package.Status.ToString() -ine 'Ok') {
                Add-Failure $packageDescription
            }
            elseif ($package.Architecture.ToString() -ieq $expectedPackageArchitecture -and
                $package.IsDevelopmentMode -and
                (Test-SamePath -First $package.InstallLocation -Second $generatedInstallLocation)) {
                Write-Check -Status PASS -Message $packageDescription
            }
            else {
                Write-Check -Status INFO -Message $packageDescription
            }
        }
    }
}

$osBuild = [System.Environment]::OSVersion.Version.Build
if ($osBuild -ge 26100 -and -not [string]::IsNullOrWhiteSpace($packageName)) {
    try {
        $catalogResult = Find-CommandPaletteExtensions
        $catalogMatches = @($catalogResult.Extensions | Where-Object { $_.PackageName -ieq $packageName })
        if ($catalogMatches.Count -eq 0) {
            Add-Failure "AppExtensionCatalog did not discover '$packageName' for com.microsoft.commandpalette."
        }
        else {
            foreach ($match in $catalogMatches) {
                Write-Check -Status PASS -Message "AppExtensionCatalog discovered '$($match.DisplayName)' (extension id '$($match.Id)')."
            }
        }
    }
    catch {
        Add-Failure "AppExtensionCatalog discovery failed: $($_.Exception.Message)"
    }
}
else {
    Add-Warning "Synchronous AppExtensionCatalog discovery requires Windows build 26100 or newer; package and manifest checks were used on build $osBuild."
}

$commandPalette = @(Get-Process -Name "Microsoft.CmdPal.UI" -ErrorAction SilentlyContinue)
if ($commandPalette.Count -gt 0) {
    Write-Check -Status PASS -Message "Command Palette is running."
}
else {
    Add-Warning "Command Palette is not running; provider activation and UI behavior cannot be observed."
}

try {
    $extensionProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'CodexUsageDock.exe'")
    $matchingComProcess = @($extensionProcesses | Where-Object {
        (Test-SamePath -First $_.ExecutablePath -Second $generatedExecutablePath) -and
        $_.CommandLine -match '(?i)(?:^|\s)-RegisterProcessAsComServer(?:\s|$)'
    })

    if ($matchingComProcess.Count -gt 0) {
        Write-Check -Status PASS -Message "The selected provider executable is running with the packaged COM-server argument."
    }
    elseif ($extensionProcesses.Count -gt 0) {
        Add-Failure "A CodexUsageDock process is running, but it does not match the selected build and packaged COM-server argument."
    }
    else {
        Add-Warning "CodexUsageDock is not running. This is normal before the provider is opened, but COM activation remains manually unverified."
    }
}
catch {
    Add-Warning "CodexUsageDock process command-line inspection was unavailable: $($_.Exception.GetType().Name)."
}

Write-Host ""
Write-Check -Status INFO -Message "Manual step: open Command Palette, run 'Reload Command Palette Extension', and complete the x64/ARM64 checklist in DEVELOPMENT.md."

if ($warnings.Count -gt 0) {
    Write-Host "$($warnings.Count) warning(s); review the yellow checks above." -ForegroundColor Yellow
}

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) integration preflight check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host "Automated integration preflight checks passed. UI behavior still requires the documented manual checks." -ForegroundColor Green
