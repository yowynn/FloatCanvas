# Original FloatCanvas brand artwork and generator, licensed under MIT.
# Recreates the editable SVG and a seven-size PNG-compressed Windows ICO without UI or external tools.
[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\FloatCanvas.App\Assets'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$taskSvg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>浮绘 FloatCanvas</title>
  <metadata>Original FloatCanvas artwork. License: MIT.</metadata>
  <rect x="40" y="66" width="176" height="164" rx="36" fill="#B9DDFF" transform="rotate(-8 128 148)"/>
  <rect x="40" y="28" width="176" height="176" rx="36" fill="#2383E2"/>
  <path d="M78 139 C91 100 101 156 121 118 C138 87 151 88 174 103" fill="none" stroke="#FFFFFF" stroke-width="15" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'FloatCanvas.svg'), $taskSvg + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

function New-RoundedPath([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $taskPath = [Drawing.Drawing2D.GraphicsPath]::new()
    [single]$taskDiameter = $Radius * 2
    $taskPath.AddArc($X, $Y, $taskDiameter, $taskDiameter, 180, 90)
    $taskPath.AddArc($X + $Width - $taskDiameter, $Y, $taskDiameter, $taskDiameter, 270, 90)
    $taskPath.AddArc($X + $Width - $taskDiameter, $Y + $Height - $taskDiameter, $taskDiameter, $taskDiameter, 0, 90)
    $taskPath.AddArc($X, $Y + $Height - $taskDiameter, $taskDiameter, $taskDiameter, 90, 90)
    $taskPath.CloseFigure()
    return $taskPath
}

$taskFrames = [Collections.Generic.List[byte[]]]::new()
$taskSizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($taskSize in $taskSizes) {
    # Render each frame with 4x supersampling; this also makes the 16px tray image readable.
    $taskBitmap = [Drawing.Bitmap]::new($taskSize * 4, $taskSize * 4, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskBitmap)
    $taskBackPath = New-RoundedPath 40 66 176 164 36
    $taskFrontPath = New-RoundedPath 40 28 176 176 36
    $taskWavePath = [Drawing.Drawing2D.GraphicsPath]::new()
    $taskRotation = [Drawing.Drawing2D.Matrix]::new()
    $taskBackBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#B9DDFF'))
    $taskFrontBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#2383E2'))
    $taskPen = [Drawing.Pen]::new([Drawing.Color]::White, 15)
    $taskScaled = $null
    $taskScaledGraphics = $null
    $taskPng = [IO.MemoryStream]::new()
    try {
        $taskGraphics.Clear([Drawing.Color]::Transparent)
        $taskGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskGraphics.ScaleTransform([single]($taskSize * 4 / 256), [single]($taskSize * 4 / 256))
        $taskRotation.RotateAt(-8, [Drawing.PointF]::new(128, 148))
        $taskBackPath.Transform($taskRotation)
        $taskGraphics.FillPath($taskBackBrush, $taskBackPath)
        $taskGraphics.FillPath($taskFrontBrush, $taskFrontPath)
        $taskPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $taskPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $taskPen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $taskWavePath.AddBezier(78, 139, 91, 100, 101, 156, 121, 118)
        $taskWavePath.AddBezier(121, 118, 138, 87, 151, 88, 174, 103)
        $taskGraphics.DrawPath($taskPen, $taskWavePath)
        $taskScaled = [Drawing.Bitmap]::new($taskSize, $taskSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskScaledGraphics = [Drawing.Graphics]::FromImage($taskScaled)
        $taskScaledGraphics.Clear([Drawing.Color]::Transparent)
        $taskScaledGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $taskScaledGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $taskScaledGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskScaledGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskScaledGraphics.DrawImage($taskBitmap, [Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize))
        $taskScaled.Save($taskPng, [Drawing.Imaging.ImageFormat]::Png)
        $taskFrames.Add($taskPng.ToArray())
    }
    finally {
        $taskPng.Dispose()
        if ($null -ne $taskScaledGraphics) { $taskScaledGraphics.Dispose() }
        if ($null -ne $taskScaled) { $taskScaled.Dispose() }
        $taskPen.Dispose(); $taskFrontBrush.Dispose(); $taskBackBrush.Dispose(); $taskRotation.Dispose()
        $taskWavePath.Dispose(); $taskFrontPath.Dispose(); $taskBackPath.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose()
    }
}

$taskIconPath = Join-Path $OutputDirectory 'FloatCanvas.ico'
$taskFile = [IO.File]::Create($taskIconPath)
$taskWriter = [IO.BinaryWriter]::new($taskFile)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskSizes.Count)
    [uint32]$taskOffset = 6 + 16 * $taskSizes.Count
    for ($taskIndex = 0; $taskIndex -lt $taskSizes.Count; $taskIndex++) {
        [byte]$taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
        $taskWriter.Write($taskDimension); $taskWriter.Write($taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
        $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length); $taskWriter.Write($taskOffset)
        $taskOffset += [uint32]$taskFrames[$taskIndex].Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write($taskFrame) }
}
finally { $taskWriter.Dispose(); $taskFile.Dispose() }
Write-Output "Generated FloatCanvas SVG and ICO (16/24/32/48/64/128/256) in $OutputDirectory"
