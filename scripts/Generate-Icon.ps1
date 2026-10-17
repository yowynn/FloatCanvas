# Normalizes the adopted transparent PNG and builds the Windows ICO without redrawing it.
# Uses only Windows/.NET System.Drawing; no additional packages are required.
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\FloatCanvas.App\Assets'),
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\src\FloatCanvas.App\Assets\FloatCanvas.png')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$SourcePath = [IO.Path]::GetFullPath($SourcePath)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

function Get-AlphaBounds([Drawing.Bitmap]$Bitmap) {
    $taskBits = $Bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $Bitmap.Width, $Bitmap.Height),
        [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $taskStride = [Math]::Abs($taskBits.Stride)
        $taskPixels = [byte[]]::new($taskStride * $Bitmap.Height)
        [Runtime.InteropServices.Marshal]::Copy($taskBits.Scan0, $taskPixels, 0, $taskPixels.Length)
        $taskLeft = $Bitmap.Width; $taskTop = $Bitmap.Height
        $taskRight = -1; $taskBottom = -1
        for ($taskY = 0; $taskY -lt $Bitmap.Height; $taskY++) {
            $taskRow = if ($taskBits.Stride -ge 0) { $taskY * $taskStride } else { ($Bitmap.Height - 1 - $taskY) * $taskStride }
            for ($taskX = 0; $taskX -lt $Bitmap.Width; $taskX++) {
                if ($taskPixels[$taskRow + $taskX * 4 + 3] -eq 0) { continue }
                if ($taskX -lt $taskLeft) { $taskLeft = $taskX }
                if ($taskX -gt $taskRight) { $taskRight = $taskX }
                if ($taskY -lt $taskTop) { $taskTop = $taskY }
                if ($taskY -gt $taskBottom) { $taskBottom = $taskY }
            }
        }
        if ($taskRight -lt 0) { throw 'The artwork contains no non-transparent pixels.' }
        return [Drawing.Rectangle]::FromLTRB($taskLeft, $taskTop, $taskRight + 1, $taskBottom + 1)
    }
    finally { $Bitmap.UnlockBits($taskBits) }
}

function New-FittedBitmap([Drawing.Bitmap]$Bitmap, [Drawing.Rectangle]$Bounds, [int]$Size) {
    $taskResult = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskGraphics = [Drawing.Graphics]::FromImage($taskResult)
    $taskAttributes = [Drawing.Imaging.ImageAttributes]::new()
    try {
        $taskGraphics.Clear([Drawing.Color]::Transparent)
        $taskGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $taskGraphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $taskGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $taskGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskAttributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $taskScale = $Size / [double][Math]::Max($Bounds.Width, $Bounds.Height)
        $taskWidth = [single]($Bounds.Width * $taskScale)
        $taskHeight = [single]($Bounds.Height * $taskScale)
        $taskDestination = [Drawing.RectangleF]::new(($Size - $taskWidth) / 2, ($Size - $taskHeight) / 2, $taskWidth, $taskHeight)
        $taskPoints = [Drawing.PointF[]]@(
            [Drawing.PointF]::new($taskDestination.Left, $taskDestination.Top),
            [Drawing.PointF]::new($taskDestination.Right, $taskDestination.Top),
            [Drawing.PointF]::new($taskDestination.Left, $taskDestination.Bottom))
        $taskGraphics.DrawImage($Bitmap, $taskPoints, [Drawing.RectangleF]$Bounds,
            [Drawing.GraphicsUnit]::Pixel, $taskAttributes)
        return $taskResult
    }
    catch { $taskResult.Dispose(); throw }
    finally { $taskAttributes.Dispose(); $taskGraphics.Dispose() }
}

# Decode from memory so no GDI+ image can retain a lock on the output PNG path.
$taskSourceStream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($SourcePath))
$taskSource = [Drawing.Bitmap]::FromStream($taskSourceStream)
$taskFrames = [Collections.Generic.List[byte[]]]::new()
$taskSizes = @(16, 24, 32, 48, 64, 128, 256)
$taskArtwork = $null
try {
    $taskSourceBounds = Get-AlphaBounds $taskSource
    $taskMasterSize = [Math]::Max($taskSourceBounds.Width, $taskSourceBounds.Height)
    $taskMaster = [Drawing.Bitmap]::new($taskMasterSize, $taskMasterSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskArtwork = $taskSource.Clone($taskSourceBounds, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskMasterBounds = [Drawing.Rectangle]::new(
        [int][Math]::Floor(($taskMasterSize - $taskArtwork.Width) / 2),
        [int][Math]::Floor(($taskMasterSize - $taskArtwork.Height) / 2), $taskArtwork.Width, $taskArtwork.Height)
    $taskReadBits = $taskArtwork.LockBits([Drawing.Rectangle]::new(0, 0, $taskArtwork.Width, $taskArtwork.Height),
        [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $taskWriteBits = $taskMaster.LockBits($taskMasterBounds,
        [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        # Copy BGRA bytes directly: DrawImageUnscaled still rounds translucent colors.
        $taskRowPixels = [byte[]]::new($taskArtwork.Width * 4)
        for ($taskY = 0; $taskY -lt $taskArtwork.Height; $taskY++) {
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($taskReadBits.Scan0, $taskY * $taskReadBits.Stride), $taskRowPixels, 0, $taskRowPixels.Length)
            [Runtime.InteropServices.Marshal]::Copy($taskRowPixels, 0, [IntPtr]::Add($taskWriteBits.Scan0, $taskY * $taskWriteBits.Stride), $taskRowPixels.Length)
        }
    }
    finally { $taskArtwork.UnlockBits($taskReadBits); $taskMaster.UnlockBits($taskWriteBits) }
    try { $taskMaster.Save((Join-Path $OutputDirectory 'FloatCanvas.png'), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $taskMaster.Dispose() }

    foreach ($taskSize in $taskSizes) {
        $taskFrame = New-FittedBitmap $taskArtwork ([Drawing.Rectangle]::new(0, 0, $taskArtwork.Width, $taskArtwork.Height)) $taskSize
        $taskPng = [IO.MemoryStream]::new()
        try {
            # Tiny alpha extremities can disappear on reduction. Fit the pixels that survive
            # in this frame once more rather than retaining an unintended transparent rim.
            $taskFrameBounds = Get-AlphaBounds $taskFrame
            if ([Math]::Max($taskFrameBounds.Width, $taskFrameBounds.Height) -lt $taskSize) {
                $taskFitted = New-FittedBitmap $taskFrame $taskFrameBounds $taskSize
                $taskFrame.Dispose()
                $taskFrame = $taskFitted
            }
            $taskFrameBounds = Get-AlphaBounds $taskFrame
            if ([Math]::Max($taskFrameBounds.Width, $taskFrameBounds.Height) -ne $taskSize) {
                throw "The $taskSize-pixel ICO frame did not fill either canvas axis."
            }
            $taskFrame.Save($taskPng, [Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add($taskPng.ToArray())
        }
        finally {
            $taskPng.Dispose()
            $taskFrame.Dispose()
        }
    }
}
finally {
    if ($null -ne $taskArtwork) { $taskArtwork.Dispose() }
    $taskSource.Dispose()
    $taskSourceStream.Dispose()
}

$taskIconPath = Join-Path $OutputDirectory 'FloatCanvas.ico'
$taskWriter = [IO.BinaryWriter]::new([IO.File]::Create($taskIconPath))
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
finally { $taskWriter.Dispose() }
Write-Output "Generated tightly fitted FloatCanvas PNG ($taskMasterSize x $taskMasterSize) and ICO (16/24/32/48/64/128/256) from $SourcePath"
