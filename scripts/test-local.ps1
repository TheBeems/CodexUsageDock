[CmdletBinding()]
param(
    [ValidateSet('x64', 'ARM64')]
    [string]$Architecture = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'ARM64' } else { 'x64' }),
    [switch]$RestoreStore,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'local-test-policy.ps1')
$localTestRepo = Split-Path -Parent $PSScriptRoot
$localTestPackageName = 'TheBeems.CodexUsageDock'
$localTestPackageFamily = 'TheBeems.CodexUsageDock_qye81p6cmqsf6'

function Get-LocalTestPackage {
    $packages = @(Get-AppxPackage -Name $localTestPackageName)
    if ($packages.Count -gt 1) { throw 'Multiple package registrations found; resolve them before switching.' }
    if ($packages.Count -eq 0) { return $null }
    $package = $packages[0]
    if ($package.PackageFamilyName -ne $localTestPackageFamily) { throw 'Unexpected package publisher; refusing to switch.' }
    if ($package.IsDevelopmentMode) {
        $trustedRoot = [IO.Path]::GetFullPath((Join-Path $localTestRepo 'CodexUsageDock/bin')).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not [IO.Path]::GetFullPath($package.InstallLocation).StartsWith($trustedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The development registration belongs to another checkout; refusing to replace it.'
        }
    }
    elseif ($package.SignatureKind.ToString() -ne 'Store') { throw 'The installed package is not Store-signed; refusing to replace it.' }
    return $package
}

function Stop-LocalTestProvider($Package) {
    if ($null -eq $Package) { return }
    $executable = Join-Path $Package.InstallLocation 'CodexUsageDock.exe'
    $session = (Get-Process -Id $PID).SessionId
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'CodexUsageDock.exe'")) {
        if ($process.SessionId -eq $session -and $process.ExecutablePath -ieq $executable) {
            Stop-Process -Id $process.ProcessId -Force
        }
    }
}

function New-LocalTestBackup([string]$LocalData, $Package) {
    $backupRoot = Join-Path $LocalData ('CodexUsageDock-development-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    $paths = @(
        'CodexUsageDock',
        "Packages/$localTestPackageFamily/LocalCache/Local/CodexUsageDock",
        "Packages/$localTestPackageFamily/LocalState"
    )
    $copies = @()
    foreach ($relative in $paths) {
        $source = Join-Path $LocalData $relative
        if (Test-Path -LiteralPath $source -PathType Container) {
            $destination = Join-Path $backupRoot $relative
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Recurse -Force
            $copies += [pscustomobject]@{ Source = $source; Backup = $destination }
        }
    }
    [pscustomobject]@{
        CreatedAt = [DateTimeOffset]::Now.ToString('O')
        Package = if ($null -ne $Package) { $Package | Select-Object PackageFullName, InstallLocation, IsDevelopmentMode } else { $null }
        Paths = $copies
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $backupRoot 'backup.json') -Encoding utf8
    Write-Host "Data backup: $backupRoot"
    return ,$copies
}

function Restore-LocalTestData($Copies) {
    foreach ($copy in $Copies) {
        New-Item -ItemType Directory -Path $copy.Source -Force | Out-Null
        foreach ($entry in Get-ChildItem -LiteralPath $copy.Backup -Force) {
            Copy-Item -LiteralPath $entry.FullName -Destination $copy.Source -Recurse -Force
        }
    }
}

function Get-LocalTestWinget {
    $installer = Get-AppxPackage Microsoft.DesktopAppInstaller | Select-Object -First 1
    if ($null -eq $installer) { throw 'App Installer (winget) is required for returning to Microsoft Store.' }
    $path = Join-Path $installer.InstallLocation 'winget.exe'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'winget.exe was not found.' }
    return $path
}

function Install-LocalTestStore([string]$Winget) {
    & $Winget install --id 9NFCPJXQG9FG --exact --source msstore --accept-source-agreements --accept-package-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) { throw "Store installation failed (winget exit $LASTEXITCODE)." }
    $installed = Get-AppxPackage -Name $localTestPackageName
    if ($null -eq $installed -or $installed.IsDevelopmentMode -or $installed.SignatureKind.ToString() -ne 'Store' -or $installed.Status.ToString() -ne 'Ok') {
        throw 'A healthy Store installation could not be verified.'
    }
}

function Invoke-LocalTestBuild([string]$TargetArchitecture) {
    $dotnet = Join-Path $localTestRepo '.dotnet/dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
    $rid = if ($TargetArchitecture -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    Push-Location $localTestRepo
    try {
        & $dotnet build ./CodexUsageDock/CodexUsageDock.csproj -c Debug "-p:Platform=$TargetArchitecture" -r $rid --self-contained true
        if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE); no package was replaced." }
    }
    finally { Pop-Location }
}

function Invoke-LocalTestPreflight([string]$TargetArchitecture, [switch]$Register) {
    # Use a child process because the integration script returns a process exit code.
    $shell = (Get-Process -Id $PID).Path
    $arguments = @('-NoLogo', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'test-integration.ps1'), '-Architecture', $TargetArchitecture)
    $arguments += if ($Register) { '-Register' } else { '-ArtifactsOnly' }
    & $shell @arguments
    if ($LASTEXITCODE -ne 0) { throw "Integration check failed (exit $LASTEXITCODE)." }
}

function Invoke-LocalTest([string]$TargetArchitecture, [switch]$UseStore, [switch]$NoBuild) {
    if ($UseStore -and $NoBuild) { throw '-RestoreStore and -SkipBuild cannot be combined.' }
    $package = Get-LocalTestPackage
    if ($UseStore -and $null -ne $package -and -not $package.IsDevelopmentMode) {
        Write-Host 'The Store version is already installed.'
        return
    }
    if (-not $UseStore) { Assert-LocalTestPolicy }
    # Check that the Store recovery command is available before removing anything.
    $winget = Get-LocalTestWinget
    if (-not $UseStore) {
        if ($null -ne $package -and $package.IsDevelopmentMode) { Stop-LocalTestProvider $package }
        if (-not $NoBuild) { Invoke-LocalTestBuild $TargetArchitecture }
        Invoke-LocalTestPreflight $TargetArchitecture
    }
    Stop-LocalTestProvider $package
    $copies = New-LocalTestBackup ([Environment]::GetFolderPath('LocalApplicationData')) $package
    $removed = $false
    try {
        if ($null -ne $package -and ($UseStore -or -not $package.IsDevelopmentMode)) {
            Write-Host "Switching the current user's package: $($package.PackageFullName)"
            if ($package.IsDevelopmentMode) {
                Remove-AppxPackage -Package $package.PackageFullName -PreserveApplicationData
            }
            else {
                # PreserveApplicationData only applies to loose development registrations.
                Remove-AppxPackage -Package $package.PackageFullName
            }
            $removed = $true
            Restore-LocalTestData $copies
        }
        if ($UseStore) { Install-LocalTestStore $winget }
        else { Invoke-LocalTestPreflight $TargetArchitecture -Register }
    }
    catch {
        $switchError = $_
        if ($removed) {
            try {
                $current = Get-AppxPackage -Name $localTestPackageName
                if ($null -ne $current) {
                    # Do not remove a package installed concurrently or a successfully registered build.
                    Write-Warning 'A package is registered. It was left in place; inspect it before retrying.'
                }
                elseif ($package.IsDevelopmentMode) {
                    Add-AppxPackage -Register (Join-Path $package.InstallLocation 'AppxManifest.xml') -ForceUpdateFromAnyVersion -ForceApplicationShutdown
                }
                else { Install-LocalTestStore $winget }
                Restore-LocalTestData $copies
            }
            catch { Write-Warning "Automatic recovery failed: $($_.Exception.Message). The data backup was retained." }
        }
        throw $switchError
    }
    Write-Host 'Switch complete. In Command Palette, run Reload Command Palette Extension, then open Codex Usage.'
    if (-not $UseStore) { Write-Host 'Return to Microsoft Store with: ./scripts/test-local.ps1 -RestoreStore' }
}

# Dot-sourcing exposes functions for tests without building or touching registration.
if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LocalTest -TargetArchitecture $Architecture -UseStore:$RestoreStore -NoBuild:$SkipBuild
}
