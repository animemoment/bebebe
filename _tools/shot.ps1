<#
shot.ps1 - снять кадр из живой игры и сохранить как PNG + напечатать числовой отпечаток.

Зачем: godot_screenshot отдаёт base64 прямо в ответе, и он рвётся в чате
(спилл-токены). Здесь мы идём своим бриджем _tools/game_cmd.js -> JSON в файл,
декодируем PNG на диск и считаем статистику. Картинку смотрит человек или
vision-модель; этот скрипт даёт ещё и числовой инвариант кадра
(средний цвет / доля непустых пикселей / сетка 16x16), чтобы ловить
"экран почернел", "UI не появился", "кадр изменился" без глаз.

Примеры:
  pwsh -File shot.ps1 -Name menu
  pwsh -File shot.ps1 -Name corner -CropX 0 -CropY 0 -CropW 300 -CropH 160 -Zoom 3
#>
param(
    [string]$OutDir = "D:\bebebe-main (1)\_tools\shots",
    [string]$Name = "shot",
    [int]$Port = 9090,
    [int]$CropX = -1,
    [int]$CropY = -1,
    [int]$CropW = 0,
    [int]$CropH = 0,
    [double]$Zoom = 1.0,
    [int]$TimeoutMs = 25000
)

$ErrorActionPreference = "Stop"
$tools = "D:\bebebe-main (1)\_tools"
$node = "C:\Program Files\nodejs\node.exe"

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$json = Join-Path $env:TEMP "shot_raw.json"
Remove-Item $json -ErrorAction SilentlyContinue

& $node "$tools\game_cmd.js" screenshot "{}" $Port $json $TimeoutMs | Out-Null

if (-not (Test-Path $json)) { throw "no response file from bridge" }
$resp = Get-Content $json -Raw | ConvertFrom-Json

if (-not $resp.success -or -not $resp.data) {
    throw ("screenshot failed: " + ($resp | ConvertTo-Json -Compress))
}

$png = Join-Path $OutDir "$Name.png"
[IO.File]::WriteAllBytes($png, [Convert]::FromBase64String($resp.data))

Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($png)

# --- числовая статистика кадра (выборка по сетке, чтобы не тормозить) ---
$step = 6
$sumR = 0L; $sumG = 0L; $sumB = 0L; $n = 0L; $nonBlack = 0L
for ($y = 0; $y -lt $bmp.Height; $y += $step) {
    for ($x = 0; $x -lt $bmp.Width; $x += $step) {
        $c = $bmp.GetPixel($x, $y)
        $sumR += $c.R; $sumG += $c.G; $sumB += $c.B; $n++
        if (($c.R + $c.G + $c.B) -gt 24) { $nonBlack++ }
    }
}

# сетка 16x16 средних значений — грубый "отпечаток" компоновки кадра
$grid = @()
$cw = [Math]::Max(1, [int]($bmp.Width / 16))
$ch = [Math]::Max(1, [int]($bmp.Height / 16))
for ($gy = 0; $gy -lt 16; $gy++) {
    $row = @()
    for ($gx = 0; $gx -lt 16; $gx++) {
        $x0 = $gx * $cw; $y0 = $gy * $ch
        $c = $bmp.GetPixel([Math]::Min($bmp.Width - 1, $x0 + [int]($cw / 2)),
                           [Math]::Min($bmp.Height - 1, $y0 + [int]($ch / 2)))
        $row += [int](($c.R + $c.G + $c.B) / 3)
    }
    $grid += , $row
}

$bmp.Dispose()

$gridStr = ($grid | ForEach-Object { ($_ | ForEach-Object { $_.ToString("x2") }) -join "" }) -join " "
$hash = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($gridStr))
).Replace("-", "").Substring(0, 16)

"PNG      : $png"
"SIZE     : $($resp.width)x$($resp.height)  ($((Get-Item $png).Length) bytes)"
"MEAN_RGB : R=$([int]($sumR / $n)) G=$([int]($sumG / $n)) B=$([int]($sumB / $n))"
"NONBLACK : $([int](100 * $nonBlack / $n))%  ($nonBlack / $n samples)"
"GRIDHASH : $hash"

# --- опциональный кроп с зумом (для глаз) ---
if ($CropX -ge 0 -and $CropW -gt 0 -and $CropH -gt 0) {
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    $rect = New-Object System.Drawing.Rectangle($CropX, $CropY, $CropW, $CropH)
    $outW = [int]($CropW * $Zoom); $outH = [int]($CropH * $Zoom)
    $dst = New-Object System.Drawing.Bitmap($outW, $outH)
    $g = [System.Drawing.Graphics]::FromImage($dst)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($bmp, (New-Object System.Drawing.Rectangle(0, 0, $outW, $outH)), $rect, [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose(); $bmp.Dispose()
    $cropPath = Join-Path $OutDir "${Name}_crop.png"
    $dst.Save($cropPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $dst.Dispose()
    "CROP     : $cropPath  (${outW}x${outH}, из $CropW x $CropH @ $CropX,$CropY, zoom=$Zoom)"
}