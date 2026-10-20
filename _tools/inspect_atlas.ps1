# Minimal PNG decoder: dumps average RGB of every 16x16 cell of a grid atlas.
param(
    [string]$Path = "D:\bebebe-main (1)\bebebe-main\ui\assets\textures\map\ForWorldMap.png",
    [int]$Cell = 16
)

$bytes = [System.IO.File]::ReadAllBytes($Path)
if ($bytes[0] -ne 137 -or $bytes[1] -ne 80) { throw "not a PNG" }

$pos = 8
$w = 0; $h = 0; $bitDepth = 8; $colorType = 6; $interlace = 0
$idat = New-Object System.Collections.Generic.List[byte]
$plte = $null
$trns = $null

while ($pos -lt $bytes.Length) {
    $len = [int]$bytes[$pos] * 16777216 + [int]$bytes[$pos+1] * 65536 + [int]$bytes[$pos+2] * 256 + [int]$bytes[$pos+3]
    $type = [System.Text.Encoding]::ASCII.GetString($bytes, $pos + 4, 4)
    $dataStart = $pos + 8
    if ($type -eq "IHDR") {
        $w = [int]$bytes[$dataStart] * 16777216 + [int]$bytes[$dataStart+1] * 65536 + [int]$bytes[$dataStart+2] * 256 + [int]$bytes[$dataStart+3]
        $h = [int]$bytes[$dataStart+4] * 16777216 + [int]$bytes[$dataStart+5] * 65536 + [int]$bytes[$dataStart+6] * 256 + [int]$bytes[$dataStart+7]
        $bitDepth = [int]$bytes[$dataStart+8]
        $colorType = [int]$bytes[$dataStart+9]
        $interlace = [int]$bytes[$dataStart+12]
    } elseif ($type -eq "PLTE") {
        $plte = New-Object byte[] $len
        [Array]::Copy($bytes, $dataStart, $plte, 0, $len)
    } elseif ($type -eq "tRNS") {
        $trns = New-Object byte[] $len
        [Array]::Copy($bytes, $dataStart, $trns, 0, $len)
    } elseif ($type -eq "IDAT") {
        for ($i = 0; $i -lt $len; $i++) { $idat.Add($bytes[$dataStart + $i]) }
    } elseif ($type -eq "IEND") {
        break
    }
    $pos = $dataStart + $len + 4
}

if ($interlace -ne 0) { throw "interlaced PNG not supported" }

$raw = $idat.ToArray()
$ms = New-Object System.IO.MemoryStream($raw)
$ms.Position = 2   # skip zlib header
$ds = New-Object System.IO.Compression.DeflateStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
$out = New-Object System.IO.MemoryStream
$ds.CopyTo($out)
$ds.Dispose()
$pixels = $out.ToArray()

# channels per pixel
switch ($colorType) {
    0 { $ch = 1 }
    2 { $ch = 3 }
    3 { $ch = 1 }
    4 { $ch = 2 }
    6 { $ch = 4 }
    default { throw "unsupported colorType $colorType" }
}
$bitsPerPixel = $ch * $bitDepth
$bpp = [Math]::Max(1, [int]($bitsPerPixel / 8))
$stride = [int][Math]::Ceiling($w * $bitsPerPixel / 8.0)

$img = New-Object 'byte[,]' ($w * 4), $h   # RGBA expanded
$prev = New-Object byte[] $stride
$cur = New-Object byte[] $stride
$off = 0
for ($y = 0; $y -lt $h; $y++) {
    $ft = [int]$pixels[$off]; $off++
    [Array]::Copy($pixels, $off, $cur, 0, $stride); $off += $stride
    for ($i = 0; $i -lt $stride; $i++) {
        $a = if ($i -ge $bpp) { $cur[$i - $bpp] } else { 0 }
        $b = $prev[$i]
        $c = if ($i -ge $bpp) { $prev[$i - $bpp] } else { 0 }
        switch ($ft) {
            0 { }
            1 { $cur[$i] = ($cur[$i] + $a) -band 0xFF }
            2 { $cur[$i] = ($cur[$i] + $b) -band 0xFF }
            3 { $cur[$i] = ($cur[$i] + [int](($a + $b) / 2)) -band 0xFF }
            4 {
                $p = $a + $b - $c
                $pa = [Math]::Abs($p - $a); $pb = [Math]::Abs($p - $b); $pc = [Math]::Abs($p - $c)
                $pr = if ($pa -le $pb -and $pa -le $pc) { $a } elseif ($pb -le $pc) { $b } else { $c }
                $cur[$i] = ($cur[$i] + $pr) -band 0xFF
            }
            default { throw "bad filter $ft" }
        }
    }
    # expand row
    for ($x = 0; $x -lt $w; $x++) {
        if ($colorType -eq 3) {
            $idx = $cur[$x]
            $img[($x*4), $y]     = $plte[$idx*3]
            $img[($x*4+1), $y]   = $plte[$idx*3+1]
            $img[($x*4+2), $y]   = $plte[$idx*3+2]
            $alpha = 255
            if ($trns -ne $null -and $idx -lt $trns.Length) { $alpha = $trns[$idx] }
            $img[($x*4+3), $y]   = $alpha
        } else {
            $s = $x * $bpp
            if ($ch -ge 3) {
                $img[($x*4), $y]   = $cur[$s]
                $img[($x*4+1), $y] = $cur[$s+1]
                $img[($x*4+2), $y] = $cur[$s+2]
            } else {
                $img[($x*4), $y] = $cur[$s]; $img[($x*4+1), $y] = $cur[$s]
                $img[($x*4+2), $y] = $cur[$s]
            }
            $img[($x*4+3), $y] = if ($ch -eq 4) { $cur[$s+3] } elseif ($ch -eq 2) { $cur[$s+1] } else { 255 }
        }
    }
    [Array]::Copy($cur, $prev, $stride)
}

$cols = [int]($w / $Cell); $rows = [int]($h / $Cell)
"PNG ${w}x${h} colorType=$colorType bitDepth=$bitDepth => grid ${cols}x${rows} (cell ${Cell}px)"
"idx  cx,cy   avgRGB            hex       distinctColors"
for ($cy = 0; $cy -lt $rows; $cy++) {
    $line = ""
    for ($cx = 0; $cx -lt $cols; $cx++) {
        $r = 0; $g = 0; $b = 0; $n = 0
        $set = New-Object System.Collections.Generic.HashSet[string]
        for ($y = $cy * $Cell; $y -lt ($cy+1) * $Cell; $y++) {
            for ($x = $cx * $Cell; $x -lt ($cx+1) * $Cell; $x++) {
                $r += $img[($x*4), $y]; $g += $img[($x*4+1), $y]; $b += $img[($x*4+2), $y]; $n++
                [void]$set.Add("$($img[($x*4), $y]),$($img[($x*4+1), $y]),$($img[($x*4+2), $y])")
            }
        }
        $ar = [int]($r / $n); $ag = [int]($g / $n); $ab = [int]($b / $n)
        $line += ("{0,3},{1,3}:#{2:X2}{3:X2}{4:X2}({5,3})  " -f $cx, $cy, $ar, $ag, $ab, $set.Count)
    }
    "row $cy : $line"
}