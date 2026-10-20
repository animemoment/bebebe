Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile("D:\bebebe-main (1)\bebebe-main\ui\assets\textures\map\ForWorldMap.png")
$w = $bmp.Width
$h = $bmp.Height

Write-Output "Texture: ${w}x${h}"
Write-Output "Grid: $($w/16) cols x $($h/16) rows"
Write-Output ""

for ($y = 0; $y -lt $h; $y += 16) {
    for ($x = 0; $x -lt $w; $x += 16) {
        $sumR = 0
        $sumG = 0
        $sumB = 0
        $count = 0
        $minR = 255; $maxR = 0
        $minG = 255; $maxG = 0
        $minB = 255; $maxB = 0
        
        for ($dy = 0; $dy -lt 16; $dy++) {
            for ($dx = 0; $dx -lt 16; $dx++) {
                $p = $bmp.GetPixel($x + $dx, $y + $dy)
                if ($p.A -gt 128) {
                    $sumR += $p.R
                    $sumG += $p.G
                    $sumB += $p.B
                    $count++
                    if ($p.R -lt $minR) { $minR = $p.R }
                    if ($p.R -gt $maxR) { $maxR = $p.R }
                    if ($p.G -lt $minG) { $minG = $p.G }
                    if ($p.G -gt $maxG) { $maxG = $p.G }
                    if ($p.B -lt $minB) { $minB = $p.B }
                    if ($p.B -gt $maxB) { $maxB = $p.B }
                }
            }
        }
        
        $cx = $x / 16
        $cy = $y / 16
        
        if ($count -eq 0) {
            Write-Output "Tile(${cx},${cy}) TRANSPARENT (alpha <= 128 everywhere)"
        } else {
            $avgR = [int]($sumR / $count)
            $avgG = [int]($sumG / $count)
            $avgB = [int]($sumB / $count)
            $spread = "R(${minR}-${maxR}) G(${minG}-${maxG}) B(${minB}-${maxB})"
            Write-Output "Tile(${cx},${cy}) avg=($avgR,$avgG,$avgB) count=$count spread=${spread}"
        }
    }
}

$bmp.Dispose()
