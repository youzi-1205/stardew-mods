# Generates assets/truck.png — a horse-spritesheet-compatible pickup truck sheet.
# Layout (matches Animals/horse): 224x128, 32x32 frames, 7 columns.
# Row 0 = facing down (frames 0-6), row 1 = facing right (7-13, left is flipped),
# row 2 = facing up (14-20), row 3 = idle (21-25).
Add-Type -AssemblyName System.Drawing

$W = 224; $H = 128; $F = 32
$bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::Transparent)

function C([int]$r,[int]$gg,[int]$b) { [System.Drawing.Color]::FromArgb(255,$r,$gg,$b) }
$body    = C 194 59 34    # firebrick red
$bodyDk  = C 142 42 24    # darker red (roof / shading)
$bedIn   = C 156 49 32    # bed interior
$glass   = C 189 227 240  # light blue window
$wheel   = C 30 30 30
$hub     = C 150 150 150
$light   = C 255 224 102  # headlight yellow
$tail    = C 216 30 30    # taillight red
$grill   = C 90 90 90
$bumper  = C 120 120 120

function FillRect($bm, [int]$x, [int]$y, [int]$w, [int]$h, $col) {
    for ($i = $x; $i -lt ($x + $w); $i++) {
        for ($j = $y; $j -lt ($y + $h); $j++) {
            if ($i -ge 0 -and $j -ge 0 -and $i -lt $bm.Width -and $j -lt $bm.Height) { $bm.SetPixel($i, $j, $col) }
        }
    }
}

# ---- one 32x32 frame: front view (facing down) ----
function DrawFront([int]$ox, [int]$oy, [int]$wheelPhase) {
    FillRect $bmp ($ox+7)  ($oy+8)  18 16 $body          # main body
    FillRect $bmp ($ox+8)  ($oy+6)  16 3  $bodyDk        # roof
    FillRect $bmp ($ox+9)  ($oy+9)  14 6  $glass         # windshield
    FillRect $bmp ($ox+8)  ($oy+18) 16 3  $grill         # grille
    FillRect $bmp ($ox+8)  ($oy+21) 3  3  $light         # headlights
    FillRect $bmp ($ox+21) ($oy+21) 3  3  $light
    FillRect $bmp ($ox+7)  ($oy+24) 18 2  $bumper        # bumper
    FillRect $bmp ($ox+5)  ($oy+22) 3  7  $wheel         # wheels
    FillRect $bmp ($ox+24) ($oy+22) 3  7  $wheel
    if ($wheelPhase -eq 0) { FillRect $bmp ($ox+6) ($oy+24) 1 1 $hub; FillRect $bmp ($ox+25) ($oy+24) 1 1 $hub }
    else { FillRect $bmp ($ox+6) ($oy+26) 1 1 $hub; FillRect $bmp ($ox+25) ($oy+26) 1 1 $hub }
}

# ---- one 32x32 frame: side view (facing right) ----
function DrawSide([int]$ox, [int]$oy, [int]$wheelPhase) {
    FillRect $bmp ($ox+2)  ($oy+15) 12 9  $body          # bed
    FillRect $bmp ($ox+3)  ($oy+16) 10 3  $bedIn         # bed interior (open box)
    FillRect $bmp ($ox+2)  ($oy+14) 12 2  $bodyDk        # bed rim
    FillRect $bmp ($ox+14) ($oy+8)  9  9  $bodyDk        # cab
    FillRect $bmp ($ox+15) ($oy+10) 6  6  $glass         # cab window
    FillRect $bmp ($ox+14) ($oy+16) 16 8  $body          # lower body + hood
    FillRect $bmp ($ox+23) ($oy+13) 7  4  $body          # hood slope
    FillRect $bmp ($ox+29) ($oy+17) 2  3  $light         # headlight
    FillRect $bmp ($ox+2)  ($oy+19) 2  3  $tail          # taillight (rear of bed)
    FillRect $bmp ($ox+2)  ($oy+23) 28 2  $bumper        # running board
    # wheels (front + rear)
    FillRect $bmp ($ox+5)  ($oy+23) 6  6  $wheel
    FillRect $bmp ($ox+21) ($oy+23) 6  6  $wheel
    if ($wheelPhase -eq 0) {
        FillRect $bmp ($ox+7)  ($oy+25) 2 2 $hub; FillRect $bmp ($ox+23) ($oy+25) 2 2 $hub
    } else {
        FillRect $bmp ($ox+6)  ($oy+25) 2 2 $hub; FillRect $bmp ($ox+22) ($oy+25) 2 2 $hub
    }
}

# ---- one 32x32 frame: rear view (facing up) ----
function DrawRear([int]$ox, [int]$oy, [int]$wheelPhase) {
    FillRect $bmp ($ox+7)  ($oy+8)  18 16 $body          # body
    FillRect $bmp ($ox+8)  ($oy+6)  16 3  $bodyDk        # roof
    FillRect $bmp ($ox+9)  ($oy+9)  14 4  $glass         # rear window
    FillRect $bmp ($ox+8)  ($oy+14) 16 8  $bodyDk        # tailgate
    FillRect $bmp ($ox+8)  ($oy+21) 3  2  $tail          # taillights
    FillRect $bmp ($ox+21) ($oy+21) 3  2  $tail
    FillRect $bmp ($ox+7)  ($oy+24) 18 2  $bumper
    FillRect $bmp ($ox+5)  ($oy+22) 3  7  $wheel
    FillRect $bmp ($ox+24) ($oy+22) 3  7  $wheel
    if ($wheelPhase -eq 0) { FillRect $bmp ($ox+6) ($oy+24) 1 1 $hub; FillRect $bmp ($ox+25) ($oy+24) 1 1 $hub }
    else { FillRect $bmp ($ox+6) ($oy+26) 1 1 $hub; FillRect $bmp ($ox+25) ($oy+26) 1 1 $hub }
}

for ($col = 0; $col -lt 7; $col++) {
    $phase = $col % 2
    DrawFront ($col * $F) 0  $phase     # row 0: down
    DrawSide  ($col * $F) $F $phase     # row 1: right
    DrawRear  ($col * $F) (2 * $F) $phase # row 2: up
    DrawFront ($col * $F) (3 * $F) 0    # row 3: idle (static)
}

$g.Dispose()
$outDir = Join-Path $PSScriptRoot "..\assets"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Force $outDir | Out-Null }
$out = Join-Path $outDir "truck.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "saved: $out"
