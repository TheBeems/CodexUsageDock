[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$BundlePath,

    [Parameter(Mandatory)]
    [string]$ExpectedPublisher,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$ExpectedVersion,

    [string]$ExpectedName = 'TheBeems.CodexUsageDock'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-SignToolPath {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $windowsSdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $windowsSdkBin) {
        $signTool = Get-ChildItem -LiteralPath $windowsSdkBin -Directory |
            Sort-Object Name -Descending |
            ForEach-Object {
                Join-Path $_.FullName 'x64\signtool.exe'
                Join-Path $_.FullName 'arm64\signtool.exe'
            } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($signTool) {
            return $signTool
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK before verifying signed MSIX bundles.'
}

function Get-BundleManifest {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxMetadata/AppxBundleManifest.xml')
        if (-not $manifestEntry) {
            throw 'The bundle does not contain AppxMetadata/AppxBundleManifest.xml.'
        }

        $reader = [IO.StreamReader]::new($manifestEntry.Open())
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

function Get-SignatureSha256 {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $signatureEntry = $archive.GetEntry('AppxSignature.p7x')
        if (-not $signatureEntry -or $signatureEntry.Length -eq 0) {
            throw 'The bundle does not contain a non-empty AppxSignature.p7x signature.'
        }

        $stream = $signatureEntry.Open()
        $sha256 = [Security.Cryptography.SHA256]::Create()
        try {
            return ([BitConverter]::ToString($sha256.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $sha256.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }
}

$resolvedBundlePath = (Resolve-Path -LiteralPath $BundlePath).Path
$signTool = Get-SignToolPath
$signToolOutput = & $signTool verify /pa /all /v $resolvedBundlePath 2>&1
$signToolExitCode = $LASTEXITCODE
$signToolOutput | ForEach-Object { Write-Host $_ }
if ($signToolExitCode -ne 0) {
    throw "SignTool verification failed with exit code $signToolExitCode."
}

$signature = Get-AuthenticodeSignature -LiteralPath $resolvedBundlePath
if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
    throw "The bundle signature status is '$($signature.Status)': $($signature.StatusMessage)"
}
if (-not $signature.SignerCertificate) {
    throw 'The bundle does not expose a signer certificate.'
}
if ($signature.SignerCertificate.Subject -cne $ExpectedPublisher) {
    throw "Signer subject '$($signature.SignerCertificate.Subject)' does not match '$ExpectedPublisher'."
}
if (-not $signature.TimeStamperCertificate) {
    throw 'The bundle signature does not contain a trusted timestamp.'
}

$bundleManifest = Get-BundleManifest -Path $resolvedBundlePath
$identity = $bundleManifest.SelectSingleNode("/*[local-name()='Bundle']/*[local-name()='Identity']")
if (-not $identity) {
    throw 'The bundle manifest does not contain an Identity element.'
}
if ([string]$identity.GetAttribute('Name') -cne $ExpectedName) {
    throw "Bundle identity name '$($identity.GetAttribute('Name'))' does not match '$ExpectedName'."
}
if ([string]$identity.GetAttribute('Publisher') -cne $ExpectedPublisher) {
    throw "Bundle publisher '$($identity.GetAttribute('Publisher'))' does not match '$ExpectedPublisher'."
}
if ([string]$identity.GetAttribute('Version') -cne $ExpectedVersion) {
    throw "Bundle version '$($identity.GetAttribute('Version'))' does not match '$ExpectedVersion'."
}

$packageNodes = @($bundleManifest.SelectNodes("/*[local-name()='Bundle']/*[local-name()='Packages']/*[local-name()='Package']"))
$architectures = @($packageNodes | ForEach-Object { [string]$_.GetAttribute('Architecture') } | Sort-Object -Unique)
$expectedArchitectures = @('x64', 'arm64')
$architectureDifference = @(Compare-Object -ReferenceObject $expectedArchitectures -DifferenceObject $architectures)
if ($architectureDifference.Count -gt 0) {
    throw "Bundle architectures '$($architectures -join ', ')' do not match 'x64, arm64'."
}

$installerSha256 = (Get-FileHash -LiteralPath $resolvedBundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
$signatureSha256 = Get-SignatureSha256 -Path $resolvedBundlePath

Write-Host "Verified signed MSIX bundle: $resolvedBundlePath" -ForegroundColor Green
Write-Host "Publisher: $ExpectedPublisher"
Write-Host "Version: $ExpectedVersion"
Write-Host "Architectures: $($architectures -join ', ')"
Write-Host "InstallerSha256: $installerSha256"
Write-Host "SignatureSha256: $signatureSha256"
