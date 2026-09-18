# SPDX-License-Identifier: MIT
# Copyright (c) 2025–2026 Vadim Belov <https://belov.us>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64)
$badgeRatio = 0.55
$logo = [System.Drawing.Bitmap]::new((Join-Path $PSScriptRoot 'icon-192.png'))
try {
    foreach ($source in Get-ChildItem (Join-Path $PSScriptRoot 'TrayBadges') -Filter '*.png') {
        $badge = [System.Drawing.Bitmap]::new($source.FullName)
        $frames = [System.Collections.Generic.List[byte[]]]::new()
        try {
            foreach ($size in $sizes) {
                $bitmap = [System.Drawing.Bitmap]::new($size, $size)
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                $stream = [System.IO.MemoryStream]::new()
                try {
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
                    $graphics.DrawImage($logo, 0, 0, $size, $size)
                    $badgeSize = [int][Math]::Ceiling($size * $badgeRatio)
                    $badgeX = $size - $badgeSize
                    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                    $graphics.FillEllipse([System.Drawing.Brushes]::Transparent, $badgeX, 0, $badgeSize, $badgeSize)
                    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
                    $graphics.DrawImage($badge, $badgeX, 0, $badgeSize, $badgeSize)
                    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                    $frames.Add($stream.ToArray())
                }
                finally {
                    $stream.Dispose()
                    $graphics.Dispose()
                    $bitmap.Dispose()
                }
            }

            $outputPath = Join-Path $PSScriptRoot "tray-$($source.BaseName).ico"
            $output = [System.IO.File]::Create($outputPath)
            $writer = [System.IO.BinaryWriter]::new($output)
            try {
                $writer.Write([uint16]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]$frames.Count)
                $offset = 6 + (16 * $frames.Count)
                for ($i = 0; $i -lt $frames.Count; $i++) {
                    $writer.Write([byte]$sizes[$i])
                    $writer.Write([byte]$sizes[$i])
                    $writer.Write([uint16]0)
                    $writer.Write([uint16]1)
                    $writer.Write([uint16]32)
                    $writer.Write([uint32]$frames[$i].Length)
                    $writer.Write([uint32]$offset)
                    $offset += $frames[$i].Length
                }
                foreach ($frame in $frames) {
                    $writer.Write($frame)
                }
            }
            finally {
                $writer.Dispose()
                $output.Dispose()
            }
        }
        finally {
            $badge.Dispose()
        }
    }
}
finally {
    $logo.Dispose()
}
