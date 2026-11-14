<#
.SYNOPSIS
  [개발용] 트레이/exe 아이콘(Tray\assets\active.ico, inactive.ico)을 여러 크기로 다시 만든다.

.DESCRIPTION
  디자인은 원래 런타임 생성 코드(커밋 95b7669의 TrayIcons.cs)와 같다: 둥근 사각 배지 + 흰 "L",
  활성=파랑(0,120,215) / 비활성=회색(150,150,150). 32px 기준 비율(여백 1, 모서리 반경 6, 글꼴 19px)을
  각 크기에 맞춰 키우거나 줄여 그린다.

  배율마다 맞는 크기를 고를 수 있도록 16~256px를 한 .ico에 담는다(관례대로 256px만 PNG 압축,
  나머지는 비압축 32bpp DIB). 앱은 실행 시 현재 DPI의 작은 아이콘 크기에 가장 맞는 이미지를 고른다.

  Windows PowerShell 5.1(System.Drawing 내장)에서 실행한다:
    powershell -ExecutionPolicy Bypass -File dev\Generate-TrayIcons.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$outDir = Join-Path $PSScriptRoot '..\Tray\assets'

function New-BadgePng([int]$size, [System.Drawing.Color]$bg) {
    $s = $size / 32.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = 'AntiAlias'
        $g.TextRenderingHint = 'AntiAliasGridFit'
        $g.Clear([System.Drawing.Color]::Transparent)

        $inset = [Math]::Max(1.0, 1.0 * $s) - 0.5          # 32px에서 1px 여백
        $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
        $d = 2 * 6.0 * $s                                     # 32px에서 반경 6
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
        $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
        $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
        $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
        $path.CloseFigure()
        $brush = New-Object System.Drawing.SolidBrush $bg
        $g.FillPath($brush, $path)

        $font = New-Object System.Drawing.Font 'Segoe UI', (19.0 * $s), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $g.DrawString('L', $font, [System.Drawing.Brushes]::White, $rect, $fmt)
        $font.Dispose(); $brush.Dispose(); $path.Dispose()
    } finally { $g.Dispose() }

    try {
        if ($size -ge 256) {
            # 256px는 PNG 압축(관례, 용량 절약)
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            return , $ms.ToArray()
        }
        return , (ConvertTo-IcoDib $bmp)   # 작은 크기는 비압축 DIB(모든 소비자가 읽을 수 있는 형식)
    } finally { $bmp.Dispose() }
}

# 32bpp BGRA DIB 아이콘 이미지: BITMAPINFOHEADER(높이 = 2배) + 아래→위 픽셀 + AND 마스크(알파 사용 시 0)
function ConvertTo-IcoDib([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $n, $n
    $data = $bmp.LockBits($rect, 'ReadOnly', ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $pixels = New-Object byte[] ($data.Stride * $n)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)

    $maskRow = [int]([Math]::Ceiling($n / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$n); $w.Write([int32]($n * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0)
    $w.Write([uint32]($n * $n * 4 + $maskRow * $n))
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $data.Stride, $n * 4) }
    $w.Write((New-Object byte[] ($maskRow * $n)))
    $w.Flush()
    return , $ms.ToArray()
}

function Write-Ico([string]$path, [System.Drawing.Color]$bg) {
    $images = foreach ($sz in $sizes) { , (New-BadgePng $sz $bg) }
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)   # ICONDIR
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {                                   # ICONDIRENTRY
        $sz = $sizes[$i]; $len = $images[$i].Length
        $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
        $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
        $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$len); $w.Write([uint32]$offset)
        $offset += $len
    }
    foreach ($img in $images) { $w.Write($img) }
    $w.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    "{0}: {1} ({2} bytes)" -f (Split-Path $path -Leaf), ($sizes -join ','), $ms.Length
}

Write-Ico (Join-Path $outDir 'active.ico') ([System.Drawing.Color]::FromArgb(0, 120, 215))
Write-Ico (Join-Path $outDir 'inactive.ico') ([System.Drawing.Color]::FromArgb(150, 150, 150))


