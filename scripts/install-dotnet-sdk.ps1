[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sdkVersion = '10.0.301'
$architecture = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$installDirectory = Join-Path $PSScriptRoot '..\.dotnet'
$sdkPath = Join-Path $installDirectory "sdk\$sdkVersion"
if (Test-Path $sdkPath)
{
    Write-Host ".NET SDK $sdkVersion is already available at $installDirectory."
    exit 0
}

$archivePath = Join-Path $env:TEMP "dotnet-sdk-$sdkVersion-win-$architecture.zip"
try
{
    Invoke-WebRequest -Uri "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-$architecture.zip" -OutFile $archivePath
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
    Expand-Archive -Path $archivePath -DestinationPath $installDirectory -Force
}
finally
{
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $sdkPath))
{
    throw ".NET SDK $sdkVersion was not installed successfully."
}

Write-Host ".NET SDK $sdkVersion is ready at $installDirectory."
