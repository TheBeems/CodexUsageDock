[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $repoRoot 'CodexUsageDock\Assets'
}
else {
    [IO.Path]::GetFullPath($OutputDirectory)
}
New-Item -ItemType Directory -Path $assetsPath -Force | Out-Null
$assets = [ordered]@{
    'Square44x44Logo.targetsize-24_altform-unplated.png' = @(24, 24)
    'LockScreenLogo.scale-200.png' = @(48, 48)
    'StoreLogo.png' = @(50, 50)
    'Square44x44Logo.scale-200.png' = @(88, 88)
    'SmallTile.scale-200.png' = @(142, 142)
    'Square150x150Logo.scale-200.png' = @(300, 300)
    'Wide310x150Logo.scale-200.png' = @(620, 300)
    'LargeTile.scale-200.png' = @(620, 620)
    'SplashScreen.scale-200.png' = @(1240, 600)
}

function New-CodexUsageMark {
    param(
        [Parameter(Mandatory)]
        [int]$Width,

        [Parameter(Mandatory)]
        [int]$Height,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            [single]$side = [Math]::Min($Width, $Height) * 0.78
            [single]$left = ($Width - $side) / 2
            [single]$top = ($Height - $side) / 2
            $backgroundRectangle = [System.Drawing.RectangleF]::new($left, $top, $side, $side)

            $background = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 15, 23, 42))
            try {
                $graphics.FillEllipse($background, $backgroundRectangle)
            }
            finally {
                $background.Dispose()
            }

            [single]$ringInset = $side * 0.19
            [single]$ringWidth = [Math]::Max(1.5, $side * 0.075)
            $ringRectangle = [System.Drawing.RectangleF]::new(
                $left + $ringInset,
                $top + $ringInset,
                $side - (2 * $ringInset),
                $side - (2 * $ringInset))
            $ring = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 56, 189, 248), $ringWidth)
            try {
                $ring.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $ring.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                $graphics.DrawArc($ring, $ringRectangle, -55, 275)
            }
            finally {
                $ring.Dispose()
            }

            [single]$centerX = $Width / 2
            [single]$baseline = $top + ($side * 0.66)
            [single]$barSpacing = $side * 0.13
            [single]$barWidth = [Math]::Max(1.5, $side * 0.065)
            [single[]]$barHeights = @(
                ($side * 0.14)
                ($side * 0.24)
                ($side * 0.34)
            )
            $barColors = @(
                [System.Drawing.Color]::FromArgb(255, 52, 211, 153),
                [System.Drawing.Color]::FromArgb(255, 94, 234, 212),
                [System.Drawing.Color]::FromArgb(255, 240, 249, 255)
            )

            for ($index = 0; $index -lt 3; $index++) {
                [single]$x = $centerX + (($index - 1) * $barSpacing)
                $bar = [System.Drawing.Pen]::new($barColors[$index], $barWidth)
                try {
                    $bar.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                    $bar.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
                    $graphics.DrawLine($bar, $x, $baseline, $x, $baseline - $barHeights[$index])
                }
                finally {
                    $bar.Dispose()
                }
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

foreach ($asset in $assets.GetEnumerator()) {
    $destination = Join-Path $assetsPath $asset.Key
    New-CodexUsageMark -Width $asset.Value[0] -Height $asset.Value[1] -Destination $destination
    Write-Host "Generated $($asset.Key) ($($asset.Value[0])x$($asset.Value[1]))."
}
