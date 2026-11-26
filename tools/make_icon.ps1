# 產生 ATE Bench 的應用程式圖示 (AteBench\app.ico 與預覽用 PNG)。
# 每個尺寸都由向量重新繪製，小尺寸會加粗線條、省略細節，確保 16 px 也清楚。
# 用法: powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
param(
    [string]$OutIco = (Join-Path $PSScriptRoot "..\AteBench\app.ico"),
    [string]$PreviewDir = (Join-Path $PSScriptRoot "icon")
)
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = "Stop"

function Brush([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }

# 以 256 x 256 的座標系設計，依實際尺寸縮放
function Render([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $k = $size / 256.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($k, $k)))

    $small = $size -le 24
    $inset = if ($small) { 4 } else { 10 }
    $tile = New-Object System.Windows.Rect($inset, $inset, (256 - 2 * $inset), (256 - 2 * $inset))
    $radius = if ($small) { 44 } else { 56 }

    # 底板: 青 -> 靛 漸層 (與程式的強調色一致)
    $grad = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.ColorConverter]::ConvertFromString("#FF22D3EE"),
        [System.Windows.Media.ColorConverter]::ConvertFromString("#FF6366F1"),
        (New-Object System.Windows.Point(0, 0)), (New-Object System.Windows.Point(1, 1)))
    $dc.DrawRoundedRectangle($grad, $null, $tile, $radius, $radius)

    # 上半部的柔和高光
    $hi = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.ColorConverter]::ConvertFromString("#40FFFFFF"),
        [System.Windows.Media.ColorConverter]::ConvertFromString("#00FFFFFF"),
        (New-Object System.Windows.Point(0, 0)), (New-Object System.Windows.Point(0, 0.6)))
    $dc.DrawRoundedRectangle($hi, $null, $tile, $radius, $radius)

    # 示波器格線 (只在較大尺寸顯示)
    if ($size -ge 48) {
        $gridPen = New-Object System.Windows.Media.Pen((Brush "#2606121E"), 3)
        foreach ($y in 86, 136, 186) { $dc.DrawLine($gridPen, (New-Object System.Windows.Point(36, $y)), (New-Object System.Windows.Point(220, $y))) }
        foreach ($x in 82, 128, 174) { $dc.DrawLine($gridPen, (New-Object System.Windows.Point($x, 50)), (New-Object System.Windows.Point($x, 206))) }
    }

    # 波形: 平穩 -> 尖峰 -> 平穩，代表匯流排上的一次指令
    $geo = [System.Windows.Media.Geometry]::Parse("M34,136 H80 L104,70 L136,194 L160,104 L176,136 H222")
    $thick = if ($size -le 16) { 26 } elseif ($size -le 32) { 22 } else { 18 }
    $pen = New-Object System.Windows.Media.Pen((Brush "#FF06121E"), $thick)
    $pen.StartLineCap = "Round"; $pen.EndLineCap = "Round"; $pen.LineJoin = "Round"
    $dc.DrawGeometry($null, $pen, $geo)

    # 波形終點的亮點 (探針)
    if ($size -ge 48) {
        $dc.DrawEllipse((Brush "#FFFFFFFF"), (New-Object System.Windows.Media.Pen((Brush "#FF06121E"), 7)), (New-Object System.Windows.Point(222, 136)), 15, 15)
    }

    $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
New-Item -ItemType Directory -Force $PreviewDir | Out-Null
$images = @{}
foreach ($s in $sizes) {
    $images[$s] = Render $s
    [IO.File]::WriteAllBytes((Join-Path $PreviewDir "icon_$s.png"), $images[$s])
}

# 組成 .ico (各尺寸以 PNG 壓縮儲存，Windows Vista 以後皆支援)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $d = $images[$s]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 以 0 表示
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Flush()
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($OutIco), $out.ToArray())
"wrote $([IO.Path]::GetFullPath($OutIco)) ($($out.Length) bytes, sizes: $($sizes -join ', '))"
