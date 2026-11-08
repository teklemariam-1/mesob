# Draws the Mesob mark (a woven mesob basket seen from above: amber rings on charcoal with a
# terracotta centre) and writes windows\Assets\mesob.ico with 16-256 px images.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "Assets"
New-Item -ItemType Directory -Force $out | Out-Null

function Draw([int]$s) {
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $charcoal = [System.Drawing.Color]::FromArgb(255, 24, 22, 20)
    $amber = [System.Drawing.Color]::FromArgb(255, 232, 163, 61)
    $terra = [System.Drawing.Color]::FromArgb(255, 200, 85, 61)
    $pad = [Math]::Max(1, $s * 0.04)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $charcoal), $pad, $pad, $s - 2 * $pad, $s - 2 * $pad)
    $rings = if ($s -ge 48) { 4 } else { 2 }
    for ($i = 0; $i -lt $rings; $i++) {
        $r = ($s / 2 - $pad) * (0.86 - $i * (0.62 / $rings))
        $w = [Math]::Max(1.2, $s * 0.055)
        $pen = New-Object System.Drawing.Pen $amber, $w
        if ($s -ge 48) { $pen.DashPattern = @(2.2, 0.9) }
        $g.DrawEllipse($pen, $s / 2 - $r, $s / 2 - $r, 2 * $r, 2 * $r)
    }
    $c = $s * 0.14
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $terra), $s / 2 - $c, $s / 2 - $c, 2 * $c, 2 * $c)
    $g.Dispose()
    $b
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $out "mesob-256.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create((Join-Path $out "mesob.ico"))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Get-ChildItem $out | ForEach-Object { "{0}  {1:N0} bytes" -f $_.Name, $_.Length }
