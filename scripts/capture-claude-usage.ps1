[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [switch]$Standalone
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$MaximumInputBytes = 256 * 1024

function Test-FullyQualifiedPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    try {
        return [IO.Path]::IsPathFullyQualified($Path)
    }
    catch {
        # Windows PowerShell 5.1 does not expose IsPathFullyQualified.
        return $Path -match '^(?:[A-Za-z]:[\\/]|\\\\)'
    }
}

function Get-JsonProperty {
    param(
        [AllowNull()]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Convert-RateLimitWindow {
    param(
        [AllowNull()]
        [object]$RateLimits,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$CapturedAt
    )

    $sourceWindow = Get-JsonProperty -Object $RateLimits -Name $Name
    if ($null -eq $sourceWindow -or $sourceWindow -is [string] -or $sourceWindow -is [System.Array]) {
        return $null
    }

    $used = Get-JsonProperty -Object $sourceWindow -Name "used_percentage"
    if ($null -eq $used -or $used -is [string] -or $used -is [char] -or $used -is [bool]) {
        return $null
    }

    try {
        $usedNumber = [double]$used
    }
    catch {
        return $null
    }

    if ([double]::IsNaN($usedNumber) -or [double]::IsInfinity($usedNumber) -or
        $usedNumber -lt 0 -or $usedNumber -gt 100) {
        return $null
    }

    $reset = Get-JsonProperty -Object $sourceWindow -Name "resets_at"
    if ($null -eq $reset -or $reset -is [string] -or $reset -is [char] -or $reset -is [bool]) {
        return $null
    }

    try {
        $resetNumber = [double]$reset
        if ([double]::IsNaN($resetNumber) -or [double]::IsInfinity($resetNumber) -or
            $resetNumber -ne [Math]::Truncate($resetNumber)) {
            return $null
        }

        $resetSeconds = [long]$resetNumber
        $resetAt = [DateTimeOffset]::FromUnixTimeSeconds($resetSeconds)
    }
    catch {
        return $null
    }

    if ($resetAt -le $CapturedAt) {
        return $null
    }

    return [pscustomobject][ordered]@{
        used_percentage = $usedNumber
        resets_at       = $resetSeconds
    }
}

function Read-StandardInputAndPassThrough {
    param(
        [switch]$SuppressOutput
    )

    $inputStream = [Console]::OpenStandardInput()
    $outputStream = [Console]::OpenStandardOutput()
    $retained = [IO.MemoryStream]::new()
    $buffer = New-Object byte[] 8192
    $oversized = $false

    try {
        while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            # Keep the existing statusline contract unless standalone output was requested.
            if (-not $SuppressOutput) {
                $outputStream.Write($buffer, 0, $count)
            }

            if (-not $oversized) {
                $remaining = $MaximumInputBytes - [int]$retained.Length
                if ($count -le $remaining) {
                    $retained.Write($buffer, 0, $count)
                }
                else {
                    if ($remaining -gt 0) {
                        $retained.Write($buffer, 0, $remaining)
                    }

                    $oversized = $true
                }
            }
        }

        if (-not $SuppressOutput) {
            $outputStream.Flush()
        }
        return [pscustomobject]@{
            Bytes     = $retained.ToArray()
            Oversized = $oversized
        }
    }
    finally {
        $retained.Dispose()
        $inputStream.Dispose()
    }
}

function Write-AtomicSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $directory = [IO.Path]::GetDirectoryName($Path)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw [ArgumentException]::new("The output path has no directory.")
    }

    $directory = [IO.Path]::GetFullPath($directory)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $leaf = [IO.Path]::GetFileName($Path)
    if ([string]::IsNullOrWhiteSpace($leaf)) {
        throw [ArgumentException]::new("The output path has no file name.")
    }

    $temporaryLeaf = "." + $leaf + "." + [Guid]::NewGuid().ToString("N") + ".tmp"
    $temporaryPath = [IO.Path]::Combine($directory, $temporaryLeaf)
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Content)
    $stream = $null

    try {
        $stream = [IO.File]::Open(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        $stream.Dispose()
        $stream = $null

        if ([IO.File]::Exists($Path)) {
            $backupPath = [IO.Path]::Combine(
                $directory,
                "." + $leaf + "." + [Guid]::NewGuid().ToString("N") + ".bak")
            try {
                [IO.File]::Replace($temporaryPath, $Path, $backupPath, $true)
            }
            finally {
                if ([IO.File]::Exists($backupPath)) {
                    [IO.File]::Delete($backupPath)
                }
            }
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }

        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

try {
    $captured = Read-StandardInputAndPassThrough -SuppressOutput:$Standalone

    if ([string]::IsNullOrWhiteSpace($OutputPath) -or -not (Test-FullyQualifiedPath -Path $OutputPath)) {
        throw [ArgumentException]::new("The output path must be fully qualified.")
    }

    $capturedAt = [DateTimeOffset]::UtcNow
    $primary = $null
    $weekly = $null
    $parseSucceeded = -not $captured.Oversized

    if ($parseSucceeded) {
        try {
            $json = [Text.UTF8Encoding]::new($false, $true).GetString($captured.Bytes)
            $source = $json | ConvertFrom-Json
            $rateLimits = Get-JsonProperty -Object $source -Name "rate_limits"
            $primary = Convert-RateLimitWindow -RateLimits $rateLimits -Name "five_hour" -CapturedAt $capturedAt
            $weekly = Convert-RateLimitWindow -RateLimits $rateLimits -Name "seven_day" -CapturedAt $capturedAt
        }
        catch {
            $parseSucceeded = $false
            $primary = $null
            $weekly = $null
        }
    }

    $validWindows = @($primary, $weekly) | Where-Object { $null -ne $_ }
    $validCount = @($validWindows).Count
    $status = if (-not $parseSucceeded -or $validCount -eq 0) {
        "unavailable"
    }
    elseif ($validCount -eq 2) {
        "available"
    }
    else {
        "partial"
    }
    $message = if ($validCount -eq 2) {
        "Claude rate-limit windows are available."
    }
    elseif ($validCount -eq 1) {
        "One Claude rate-limit window is unavailable; windows remain independent."
    }
    else {
        "No valid Claude rate-limit windows were provided."
    }

    $snapshot = [ordered]@{
        schemaVersion  = 1
        provider       = "claude"
        observedAtUTC  = $capturedAt.ToString("O", [Globalization.CultureInfo]::InvariantCulture)
        rate_limits    = [ordered]@{
            five_hour = $primary
            seven_day = $weekly
        }
        status         = $status
        message        = $message
    }
    $snapshotJson = $snapshot | ConvertTo-Json -Depth 8 -Compress
    Write-AtomicSnapshot -Path $OutputPath -Content $snapshotJson

    if ($Standalone) {
        $primaryRemaining = if ($null -eq $primary) {
            "--"
        }
        else {
            ([double](100 - [double](Get-JsonProperty -Object $primary -Name "used_percentage"))).ToString(
                "0.##",
                [Globalization.CultureInfo]::InvariantCulture) + "%"
        }
        $weeklyRemaining = if ($null -eq $weekly) {
            "--"
        }
        else {
            ([double](100 - [double](Get-JsonProperty -Object $weekly -Name "used_percentage"))).ToString(
                "0.##",
                [Globalization.CultureInfo]::InvariantCulture) + "%"
        }

        [Console]::WriteLine("Claude 5h $primaryRemaining / week $weeklyRemaining")
    }

    exit 0
}
catch {
    [Console]::Error.WriteLine("Claude usage capture could not write the snapshot.")
    exit 1
}
