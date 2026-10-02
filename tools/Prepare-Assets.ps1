param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\Maps"),
    [string]$CacheRoot = (Join-Path $PSScriptRoot ".asset-cache")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$TarkovDevSvgCommit = "5a8b6115d1c0cf56f2ebaac1a96fa5ae3074d178"

function Ensure-Dir([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Force -Path $Path | Out-Null
    }
}

function Download-File([string]$Url, [string]$Path) {
    Ensure-Dir (Split-Path -Parent $Path)
    if ((Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -gt 0) { return }
    $tmp = $Path + ".download"
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    Write-Host ("Downloading: " + $Url)
    Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $tmp -TimeoutSec 90
    if ((Get-Item -LiteralPath $tmp).Length -eq 0) { throw "Downloaded file is empty: $Url" }
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Try-Download([string]$Url, [string]$Path) {
    try {
        Download-File $Url $Path
        return $true
    }
    catch {
        $status = $null
        try { $status = [int]$_.Exception.Response.StatusCode } catch {}
        if ($status -eq 404) {
            Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
            return $false
        }
        throw
    }
}

function Split-SvgLayer([string]$InputPath, [string]$GroupId, [string]$OutputPath) {
    [xml]$xml = Get-Content -LiteralPath $InputPath -Raw
    $root = $xml.DocumentElement
    $found = $false

    foreach ($node in @($root.ChildNodes)) {
        if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element -or $node.LocalName -ne "g") { continue }
        $id = $node.GetAttribute("id")
        if ([string]::IsNullOrWhiteSpace($id)) { continue }
        $keepWith = $node.GetAttribute("data-keep-with-group")
        $keep = ($id -eq $GroupId) -or ($keepWith -eq $GroupId)
        if ($id -eq $GroupId) { $found = $true }
        if (-not $keep) { [void]$root.RemoveChild($node) }
    }

    if (-not $found) { throw "SVG group '$GroupId' not found in $InputPath" }
    Ensure-Dir (Split-Path -Parent $OutputPath)

    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Indent = $true
    $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
    $writer = [System.Xml.XmlWriter]::Create($OutputPath, $settings)
    try { $xml.Save($writer) } finally { $writer.Dispose() }
}

function Project-Point(
    [double]$WorldX, [double]$WorldY, [double]$Rotation,
    [double]$ScaleX, [double]$MarginX, [double]$ScaleY, [double]$MarginY,
    [int]$Zoom
) {
    $rad = $Rotation * [Math]::PI / 180.0
    $rx = $WorldX * [Math]::Cos($rad) - $WorldY * [Math]::Sin($rad)
    $ry = $WorldX * [Math]::Sin($rad) + $WorldY * [Math]::Cos($rad)
    $zs = [Math]::Pow(2.0, $Zoom)
    return [pscustomobject]@{
        X = ($ScaleX * $rx + $MarginX) * $zs
        Y = ((-$ScaleY) * $ry + $MarginY) * $zs
    }
}

function Stitch-Tiles(
    [string]$Name, [string]$Template,
    [int]$LogicalTile, [int]$PhysicalTile, [int]$Zoom,
    [double]$MinX, [double]$MinY, [double]$MaxX, [double]$MaxY,
    [double]$Rotation, [double[]]$Transform, [string]$OutputPath
) {
    Add-Type -AssemblyName System.Drawing
    $p = @(
        (Project-Point $MinX $MinY $Rotation $Transform[0] $Transform[1] $Transform[2] $Transform[3] $Zoom),
        (Project-Point $MinX $MaxY $Rotation $Transform[0] $Transform[1] $Transform[2] $Transform[3] $Zoom),
        (Project-Point $MaxX $MinY $Rotation $Transform[0] $Transform[1] $Transform[2] $Transform[3] $Zoom),
        (Project-Point $MaxX $MaxY $Rotation $Transform[0] $Transform[1] $Transform[2] $Transform[3] $Zoom)
    )

    $left = [Math]::Floor(($p | Measure-Object X -Minimum).Minimum)
    $right = [Math]::Ceiling(($p | Measure-Object X -Maximum).Maximum)
    $top = [Math]::Floor(($p | Measure-Object Y -Minimum).Minimum)
    $bottom = [Math]::Ceiling(($p | Measure-Object Y -Maximum).Maximum)

    $tx0 = [int][Math]::Floor($left / $LogicalTile)
    $tx1 = [int][Math]::Floor(($right - 1) / $LogicalTile)
    $ty0 = [int][Math]::Floor($top / $LogicalTile)
    $ty1 = [int][Math]::Floor(($bottom - 1) / $LogicalTile)
    $wide = $tx1 - $tx0 + 1
    $high = $ty1 - $ty0 + 1
    if ($wide -le 0 -or $high -le 0 -or $wide -gt 128 -or $high -gt 128) {
        throw "Invalid tile range for $Name"
    }

    $mosaic = [System.Drawing.Bitmap]::new($wide * $PhysicalTile, $high * $PhysicalTile, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($mosaic)
    $g.Clear([System.Drawing.Color]::Transparent)
    $count = 0

    try {
        for ($ty = $ty0; $ty -le $ty1; $ty++) {
            for ($tx = $tx0; $tx -le $tx1; $tx++) {
                $url = $Template.Replace("{z}", [string]$Zoom).Replace("{x}", [string]$tx).Replace("{y}", [string]$ty)
                $safe = $Name -replace '[^A-Za-z0-9_.-]', '_'
                $tile = Join-Path $CacheRoot ("tiles\" + $safe + "\z" + $Zoom + "\x" + $tx + "\y" + $ty + ".png")
                if (-not (Try-Download $url $tile)) { continue }

                $img = [System.Drawing.Image]::FromFile($tile)
                try {
                    if ($img.Width -ne $PhysicalTile -or $img.Height -ne $PhysicalTile) {
                        throw "Unexpected tile size $($img.Width)x$($img.Height) for $url"
                    }
                    $g.DrawImageUnscaled($img, ($tx - $tx0) * $PhysicalTile, ($ty - $ty0) * $PhysicalTile)
                    $count++
                }
                finally { $img.Dispose() }
            }
        }
    }
    finally { $g.Dispose() }

    if ($count -eq 0) {
        $mosaic.Dispose()
        throw "No tiles downloaded for $Name"
    }

    $scale = [double]$PhysicalTile / [double]$LogicalTile
    $cx = [int][Math]::Floor(($left - ($tx0 * $LogicalTile)) * $scale)
    $cy = [int][Math]::Floor(($top - ($ty0 * $LogicalTile)) * $scale)
    $cw = [int][Math]::Ceiling(($right - $left) * $scale)
    $ch = [int][Math]::Ceiling(($bottom - $top) * $scale)

    $rect = [System.Drawing.Rectangle]::new($cx, $cy, $cw, $ch)
    $crop = $mosaic.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $mosaic.Dispose()

    Ensure-Dir (Split-Path -Parent $OutputPath)
    try { $crop.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $crop.Dispose() }

    Write-Host ("Created " + $OutputPath + " (" + $cw + "x" + $ch + ")")
}

Ensure-Dir $OutputRoot
Ensure-Dir $CacheRoot

# Interchange SVG layers.
$svgCache = Join-Path $CacheRoot "svg"
$interchange = Join-Path $svgCache "Interchange.svg"
Download-File ("https://raw.githubusercontent.com/the-hideout/tarkov-dev-svg-maps/" + $TarkovDevSvgCommit + "/Interchange.svg") $interchange
Split-SvgLayer $interchange "Ground_Level" (Join-Path $OutputRoot "Interchange_Backport\Layers\Interchange-Ground_Level.svg")
Split-SvgLayer $interchange "First_Floor" (Join-Path $OutputRoot "Interchange_Backport\Layers\Interchange-First_Floor.svg")
Split-SvgLayer $interchange "Second_Floor" (Join-Path $OutputRoot "Interchange_Backport\Layers\Interchange-Second_Floor.svg")
Download-File ("https://raw.githubusercontent.com/the-hideout/tarkov-dev-svg-maps/" + $TarkovDevSvgCommit + "/LICENSE.md") (Join-Path $OutputRoot "TARKOVDEV-SVG-LICENSE.md")

# Current Labs.
$labs = [double[]](0.575, 281.2, 0.575, 193.7)
Stitch-Tiles "Labs-First" "https://assets.tarkov.dev/maps/labs_v4/1st/{z}/{x}/{y}.png" 175 256 4 -287 -477 -80 -193 270 $labs (Join-Path $OutputRoot "Labs_Backport\Layers\Labs-First.png")
Stitch-Tiles "Labs-Second" "https://assets.tarkov.dev/maps/labs_v4/2nd/{z}/{x}/{y}.png" 175 256 4 -287 -477 -80 -193 270 $labs (Join-Path $OutputRoot "Labs_Backport\Layers\Labs-Second.png")
Stitch-Tiles "Labs-Technical" "https://assets.tarkov.dev/maps/labs_v4/technical/{z}/{x}/{y}.png" 175 256 4 -287 -477 -80 -193 270 $labs (Join-Path $OutputRoot "Labs_Backport\Layers\Labs-Technical.png")

# Icebreaker.
$ice = [double[]](2, 125, 3.5, 91)
$iceLayers = @(
    @("00-Control-Room.png","00_control_room"), @("01-Engine-Room.png","01_engine_room"),
    @("02-Engine-Room-Upper.png","02_engine_room_upper"), @("03-Fuel-Pumps-Lower.png","03_fuel_pumps_lower"),
    @("04-Fuel-Pumps.png","04_fuel_pumps"), @("05-Storage-Security.png","05_storage_ecurity"),
    @("06-Infirmary.png","06_infirmary"), @("07-Helipad.png","07_helipad"),
    @("08-Gym-Canteen.png","08_gym-canteen"), @("09-Accommodation-Lower.png","09_accommodation_lower"),
    @("10-Accommodation-Mid.png","10_accommodation_mid"), @("11-Accommodation-Upper.png","11_accommodation_upper"),
    @("12-Officers-Deck.png","12_officers_deck"), @("13-Stairs-Blocked.png","13_stairs_blocked"),
    @("14-Bridge.png","14_bridge"), @("15-Bridge-Roof.png","15_bridge_roof")
)
foreach ($layer in $iceLayers) {
    $template = "https://assets.tarkov.dev/maps/icebreaker/" + $layer[1] + "/{z}/{x}/{y}.png"
    Stitch-Tiles ("Icebreaker-" + $layer[1]) $template 256 256 2 -65.5 -64.5 77 67.4 180 $ice (Join-Path $OutputRoot ("Icebreaker\Layers\" + $layer[0]))
}

# Verified existing Satellite layers.
$gz = [double[]](0.524,167.3,0.524,65.1)
Stitch-Tiles "GZ-Ground" "https://assets.tarkov.dev/maps/groundzero/main_summer/{z}/{x}/{y}.png" 256 256 3 -99 -124 249 364 180 $gz (Join-Path $OutputRoot "StyleAssets\GroundZero\Satellite\GroundZero-Ground.png")
Stitch-Tiles "GZ-Second" "https://assets.tarkov.dev/maps/groundzero/2nd/{z}/{x}/{y}.png" 256 256 3 -99 -124 249 364 180 $gz (Join-Path $OutputRoot "StyleAssets\GroundZero\Satellite\GroundZero-Second.png")
Stitch-Tiles "GZ-Third" "https://assets.tarkov.dev/maps/groundzero/3rd/{z}/{x}/{y}.png" 256 256 3 -99 -124 249 364 180 $gz (Join-Path $OutputRoot "StyleAssets\GroundZero\Satellite\GroundZero-Third.png")
Stitch-Tiles "GZ-Garage" "https://assets.tarkov.dev/maps/groundzero/garage/{z}/{x}/{y}.png" 256 256 3 -99 -124 249 364 180 $gz (Join-Path $OutputRoot "StyleAssets\GroundZero\Satellite\GroundZero-Garage.png")

$customs = [double[]](0.239,168.65,0.239,136.35)
foreach ($x in @(
    @("Ground","main"), @("Second","2nd"), @("Third","3rd"), @("Fourth","4th"), @("Underground","underground")
)) {
    Stitch-Tiles ("Customs-" + $x[0]) ("https://assets.tarkov.dev/maps/customs_0.16/" + $x[1] + "/{z}/{x}/{y}.png") 256 256 3 -372 -306 698 235 180 $customs (Join-Path $OutputRoot ("StyleAssets\Customs\Satellite\Customs-" + $x[0] + ".png"))
}

$factory = [double[]](1.629,119.9,1.629,139.3)
foreach ($x in @(@("Ground","main"),@("Second","2nd"),@("Third","3rd"),@("Tunnels","tunnels"))) {
    Stitch-Tiles ("Factory-" + $x[0]) ("https://assets.tarkov.dev/maps/factory/" + $x[1] + "/{z}/{x}/{y}.png") 256 256 3 -65 -64.5 77.6 67.2 90 $factory (Join-Path $OutputRoot ("StyleAssets\Factory\Satellite\Factory-" + $x[0] + ".png"))
}

$woods = [double[]](0.1855,112.95,0.1855,167.85)
Stitch-Tiles "Woods-Ground" "https://assets.tarkov.dev/maps/woods/main_0.16/{z}/{x}/{y}.png" 256 256 3 -756 -915 647 443 180 $woods (Join-Path $OutputRoot "StyleAssets\Woods\Satellite\Woods-Ground.png")

$reserve = [double[]](0.395,122,0.395,137.65)
Stitch-Tiles "Reserve-Ground" "https://assets.tarkov.dev/maps/reserve/main/{z}/{x}/{y}.png" 256 256 3 -303.5 -275 292 271.5 180 $reserve (Join-Path $OutputRoot "StyleAssets\Reserve\Satellite\Reserve-Ground.png")
Stitch-Tiles "Reserve-Bunkers" "https://assets.tarkov.dev/maps/reserve/bunkers/{z}/{x}/{y}.png" 256 256 3 -303.5 -275 292 271.5 180 $reserve (Join-Path $OutputRoot "StyleAssets\Reserve\Satellite\Reserve-Bunkers.png")

$shore = [double[]](0.16,83.2,0.16,111.1)
Stitch-Tiles "Shoreline-Ground" "https://assets.tarkov.dev/maps/shoreline/main_summer/{z}/{x}/{y}.png" 256 256 3 -1060 -415 508 622 180 $shore (Join-Path $OutputRoot "StyleAssets\Shoreline\Satellite\Shoreline-Ground.png")

$inter = [double[]](0.265,150.6,0.265,134.6)
Stitch-Tiles "Interchange-Ground" "https://assets.tarkov.dev/maps/interchange/main/{z}/{x}/{y}.png" 256 256 3 -433 -442 598 426 180 $inter (Join-Path $OutputRoot "StyleAssets\Interchange_Backport\Satellite\Interchange-Ground.png")

Set-Content -LiteralPath (Join-Path $OutputRoot ".style-assets-v070.ready") -Value "DynamicMaps Extended 0.7.0 assets prepared" -Encoding UTF8
Write-Host "Asset preparation complete."
