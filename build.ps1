param(
    [string]$SptRoot,
    [switch]$SkipAssetDownload,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ok = $false
$exitCode = 0

try {
    Write-Host "====================================================="
    Write-Host " DynamicMaps Extended 1.0.0 - SPT 4.1.x Builder"
    Write-Host "====================================================="
    Write-Host ""

    if ([string]::IsNullOrWhiteSpace($SptRoot)) {
        Write-Host "Enter your SPT 4.1.x root folder."
        Write-Host "Example: C:\SPT"
        $SptRoot = Read-Host "SPT path"
    }
    $SptRoot = [IO.Path]::GetFullPath($SptRoot.Trim('"'))

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet was not found. Install a .NET SDK first."
    }

    $managed = Join-Path $SptRoot "EscapeFromTarkov_Data\Managed"
    $bepCore = Join-Path $SptRoot "BepInEx\core"
    if (-not (Test-Path $managed) -or -not (Test-Path $bepCore)) {
        throw "This does not look like a valid SPT root: $SptRoot"
    }

    $dm = Get-ChildItem (Join-Path $SptRoot "BepInEx\plugins") -Filter "DynamicMaps.dll" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $dm) { throw "DynamicMaps.dll not found under BepInEx\plugins." }

    $mv = Get-ChildItem (Join-Path $SptRoot "BepInEx\plugins") -Filter "MapVariants.Client.dll" -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $mv) { throw "MapVariants.Client.dll not found. MapVariants is required." }

    Write-Host ("DynamicMaps: " + $dm.FullName)
    Write-Host ("MapVariants: " + $mv.FullName)

    # Factory Classic is packaged as supplied SVG artwork; no runtime generation.
    $factoryFiles = @(
        "Factory_Classic\Factory_Classic.jsonc",
        "Factory_Classic\Layers\SVG\Factory-Basement.svg",
        "Factory_Classic\Layers\SVG\Factory-Ground_Floor.svg",
        "Factory_Classic\Layers\SVG\Factory-Second_Floor.svg",
        "Factory_Classic\Layers\SVG\Factory-Third_Floor.svg"
    )
    foreach ($rel in $factoryFiles) {
        $path = Join-Path (Join-Path $SourceRoot "Maps") $rel
        if (-not (Test-Path -LiteralPath $path) -or (Get-Item $path).Length -eq 0) {
            throw "Factory Classic source asset missing: $path"
        }
    }

    $factoryJson = Get-Content (Join-Path $SourceRoot "Maps\Factory_Classic\Factory_Classic.jsonc") -Raw
    foreach ($name in @("Tunnels","Ground Floor","2nd Floor","3rd Floor")) {
        $pattern = [regex]::Escape('"' + $name + '"') + '\s*:\s*\{(?s:.*?)"TesselationIndex"\s*:\s*\d+'
        if ($factoryJson -notmatch $pattern) {
            throw "Factory Classic layer '$name' is missing TesselationIndex."
        }
    }

    if (-not $SkipAssetDownload) {
        $helper = Join-Path $SourceRoot "tools\Prepare-Assets.ps1"
        Unblock-File -LiteralPath $helper -ErrorAction SilentlyContinue
        & $helper -OutputRoot (Join-Path $SourceRoot "Maps")
    }

    # Validate every static MapDef image path after asset preparation.
    Get-ChildItem (Join-Path $SourceRoot "Maps") -Filter "*.jsonc" -File -Recurse | ForEach-Object {
        $def = Get-Content $_.FullName -Raw | ConvertFrom-Json
        foreach ($layer in $def.Layers.PSObject.Properties) {
            $img = [string]$layer.Value.ImagePath
            if (-not $img.StartsWith("Maps/", [StringComparison]::OrdinalIgnoreCase)) {
                throw "Unexpected ImagePath in $($_.FullName): $img"
            }
            $rel = $img.Substring(5).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $asset = Join-Path (Join-Path $SourceRoot "Maps") $rel
            if (-not (Test-Path -LiteralPath $asset) -or (Get-Item $asset).Length -eq 0) {
                throw "Required map asset missing: $asset"
            }
        }
    }

    $project = Join-Path $SourceRoot "DynamicMaps.Extended.csproj"
    & dotnet build $project -c Release "-p:SptRoot=$SptRoot"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }

    $dll = Join-Path $SourceRoot "bin\Release\netstandard2.1\DynamicMaps.Extended.dll"
    if (-not (Test-Path $dll)) { throw "Build succeeded but DLL not found: $dll" }

    $releaseRoot = Join-Path $SourceRoot "release\DynamicMaps-Extended"
    $pluginOut = Join-Path $releaseRoot "BepInEx\plugins\DynamicMaps-Extended"
    $mapsOut = Join-Path $pluginOut "Maps"
    Remove-Item $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $mapsOut | Out-Null

    Copy-Item $dll $pluginOut
    Get-ChildItem (Join-Path $SourceRoot "Maps") -Force | ForEach-Object {
        Copy-Item $_.FullName $mapsOut -Recurse -Force
    }
    Copy-Item (Join-Path $SourceRoot "README.md") $pluginOut
    Copy-Item (Join-Path $SourceRoot "LICENSE") $pluginOut
    Copy-Item (Join-Path $SourceRoot "THIRD-PARTY.md") $pluginOut

    $zip = Join-Path $SourceRoot "release\DynamicMaps-Extended-1.0.0-SPT4.1.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $releaseRoot "*") -DestinationPath $zip -Force

    Write-Host ""
    Write-Host "BUILD SUCCEEDED" -ForegroundColor Green
    Write-Host ("Release ZIP: " + $zip)
    $ok = $true
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "BUILD FAILED" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Write-Host ""
    if (-not $NoPause) { [void](Read-Host "Press ENTER to close this window") }
}

if ($exitCode -ne 0) { exit $exitCode }
