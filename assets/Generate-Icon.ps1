$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = @(16,20,24,32,48,64,128,256)
$streams = @()
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform($size/256.0,$size/256.0)
    $dark = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#18334d'))
    $teal = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#51c9be'))
    $gray = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#91a8bf'))
    $g.FillRectangle($dark,32,30,192,196)
    $g.FillRectangle($teal,48,42,160,34)
    $g.FillRectangle($gray,90,178,76,16)
    [System.Drawing.PointF[]]$left = @([System.Drawing.PointF]::new(8,102),[System.Drawing.PointF]::new(50,102),[System.Drawing.PointF]::new(50,86),[System.Drawing.PointF]::new(88,124),[System.Drawing.PointF]::new(50,162),[System.Drawing.PointF]::new(50,146),[System.Drawing.PointF]::new(8,146))
    [System.Drawing.PointF[]]$right = @($left | ForEach-Object { [System.Drawing.PointF]::new(256-$_.X,$_.Y) })
    $g.FillPolygon($teal,$left); $g.FillPolygon($teal,$right)
    $stream = [System.IO.MemoryStream]::new(); $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $streams += ,$stream.ToArray()
    if ($size -eq 256) { $bitmap.Save((Join-Path $PSScriptRoot 'WACM.png'),[System.Drawing.Imaging.ImageFormat]::Png) }
    $stream.Dispose(); $g.Dispose(); $bitmap.Dispose(); $dark.Dispose(); $teal.Dispose(); $gray.Dispose()
}
$out = [System.IO.File]::Create((Join-Path $PSScriptRoot 'WACM.ico'))
$writer = [System.IO.BinaryWriter]::new($out)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16*$sizes.Count
for ($i=0;$i -lt $sizes.Count;$i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$streams[$i].Length); $writer.Write([uint32]$offset)
    $offset += $streams[$i].Length
}
foreach ($bytes in $streams) { $writer.Write([byte[]]$bytes) }
$writer.Dispose(); $out.Dispose()
