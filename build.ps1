param(
    [string]$SptRoot,
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$SourceRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ExitCode = 0

function Rel([string]$Path) { return $Path.Replace('/', [IO.Path]::DirectorySeparatorChar) }

try {
    Write-Host '============================================================='
    Write-Host ' DynamicMaps Extended 2.1.0'
    Write-Host '============================================================='
    Write-Host ''
    Write-Host 'Build is OFFLINE. It does not download map artwork.'
    Write-Host 'Complete release ZIPs must include prepared map artwork. The DLL never downloads or generates maps.'
    Write-Host ''

    if ([string]::IsNullOrWhiteSpace($SptRoot)) { $SptRoot = Read-Host 'Enter your SPT 4.1.x root folder' }
    if ([string]::IsNullOrWhiteSpace($SptRoot)) { throw 'No SPT folder was provided.' }
    $SptRoot = [IO.Path]::GetFullPath($SptRoot.Trim('"'))
    if (-not (Test-Path -LiteralPath $SptRoot -PathType Container)) { throw "SPT folder does not exist: $SptRoot" }

    $pluginsRoot = Join-Path $SptRoot 'BepInEx\plugins'
    $dynamicMapsDll = Get-ChildItem -LiteralPath $pluginsRoot -Filter 'DynamicMaps.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dynamicMapsDll) { throw 'DynamicMaps.dll was not found under BepInEx\plugins.' }
    $mapVariantsDll = Get-ChildItem -LiteralPath $pluginsRoot -Filter 'MapVariants.Client.dll' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $mapVariantsDll) { throw 'MapVariants.Client.dll was not found under BepInEx\plugins.' }

    Write-Host "DynamicMaps: $($dynamicMapsDll.FullName)"
    Write-Host "MapVariants: $($mapVariantsDll.FullName)"

    $managedDir = Join-Path $SptRoot 'EscapeFromTarkov_Data\Managed'
    $newtonsoft = Join-Path $managedDir 'Newtonsoft.Json.dll'
    if (-not (Test-Path -LiteralPath $newtonsoft -PathType Leaf)) { throw "Required JSON assembly is missing: $newtonsoft" }
    Write-Host 'Validated Newtonsoft.Json for the built-in asset manifest reader.' -ForegroundColor Green

    $mapsRoot = Join-Path $SourceRoot 'Maps'
    $assetMetaRoot = Join-Path $SourceRoot 'Assets'
    $manifestPath = Join-Path $assetMetaRoot 'DMEXT-ASSET-MANIFEST-v2.0.0.json'
    $structurePath = Join-Path $assetMetaRoot 'DMEXT-UPSTREAM-STRUCTURE-v2.0.0.json'
    if (-not (Test-Path -LiteralPath $mapsRoot -PathType Container)) { throw 'Maps folder is missing.' }
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Runtime asset manifest is missing.' }
    if (-not (Test-Path -LiteralPath $structurePath -PathType Leaf)) { throw 'Upstream structure lock is missing.' }

    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json }
    catch { throw "Invalid runtime asset manifest: $($_.Exception.Message)" }
    if ([string]$manifest.format -ne 'DynamicMapsExtended.RuntimeAssetManifest.v1') { throw 'Unexpected runtime asset manifest format.' }
    $runtimeAssets = @($manifest.assets)
    if ($runtimeAssets.Count -ne 42) { throw "Runtime asset manifest should contain 42 raster layers, found $($runtimeAssets.Count)." }

    $runtimePreviewPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $runtimePackPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $runtimeWarmPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($asset in $runtimeAssets) {
        [void]$runtimePreviewPaths.Add(([string]$asset.preview.outputPath).Replace('\','/'))
        [void]$runtimePackPaths.Add(([string]$asset.pack.outputPath).Replace('\','/'))
        if ($null -ne $asset.PSObject.Properties['warm']) { [void]$runtimeWarmPaths.Add(([string]$asset.warm.outputPath).Replace('\','/')) }
        $coordCount = 0
        foreach ($xr in $asset.validTilesByX) {
            foreach ($range in $xr.yRanges) { $coordCount += ([int]$range[1] - [int]$range[0] + 1) }
        }
        if ($coordCount -ne [int]$asset.validTileCount) { throw "Runtime tile-coordinate count mismatch for $($asset.id): $coordCount/$($asset.validTileCount)." }
        if ([string]$asset.pack.format -ne 'NVTILES2') { throw "Unexpected pack format for $($asset.id)." }
        if ([string]::IsNullOrWhiteSpace([string]$asset.sourceFingerprintSha256) -or [string]::IsNullOrWhiteSpace([string]$asset.sourceVisualFingerprintSha256) -or [string]::IsNullOrWhiteSpace([string]$asset.pack.expectedSha256)) { throw "Missing historical fingerprint/provenance hash for $($asset.id)." }
        if ([string]$asset.sourceVisualFingerprintSha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid historical visual fingerprint for $($asset.id)." }
    }
    Write-Host 'Validated runtime manifest: 42 raster layers with exact coordinates.' -ForegroundColor Green

    try { $structure = Get-Content -LiteralPath $structurePath -Raw | ConvertFrom-Json }
    catch { throw "Invalid upstream structure lock: $($_.Exception.Message)" }
    if ([string]$structure.format -ne 'DynamicMapsExtended.UpstreamStructureLock.v1') { throw 'Unexpected upstream structure-lock format.' }
    $structureMaps = @($structure.maps)
    if ($structureMaps.Count -ne 10) { throw "Upstream structure lock should contain 10 raster maps, found $($structureMaps.Count)." }
    foreach ($m in $structureMaps) {
        if ([string]::IsNullOrWhiteSpace([string]$m.normalizedName) -or [string]::IsNullOrWhiteSpace([string]$m.tilePath)) { throw 'Upstream structure-lock map is missing name/tilePath.' }
        if (@($m.transform).Count -ne 4) { throw "Upstream structure-lock transform must contain 4 numbers: $($m.normalizedName)" }
        if (@($m.bounds).Count -ne 2 -or @($m.bounds[0]).Count -ne 2 -or @($m.bounds[1]).Count -ne 2) { throw "Upstream structure-lock bounds are invalid: $($m.normalizedName)" }
        if ([int]$m.minZoom -lt 0 -or [int]$m.maxZoom -lt [int]$m.minZoom) { throw "Upstream structure-lock zoom range is invalid: $($m.normalizedName)" }
    }
    foreach ($asset in $runtimeAssets) {
        $lockedMap = $structureMaps | Where-Object { [string]$_.normalizedName -eq [string]$asset.map } | Select-Object -First 1
        if (-not $lockedMap) { throw "No upstream structure lock exists for runtime asset map: $($asset.map)" }
        $pathMatches = ([string]$lockedMap.tilePath -eq [string]$asset.tilePathTemplate) -or (@($lockedMap.layers) | Where-Object { [string]$_.name -eq [string]$asset.layer -and [string]$_.tilePath -eq [string]$asset.tilePathTemplate } | Select-Object -First 1)
        if (-not $pathMatches) { throw "Runtime asset tile path is not represented by its upstream structure lock: $($asset.id)" }
    }
    Write-Host 'Validated upstream structural lock: 10 maps and all 42 runtime tile paths (zoom/transform/bounds/rotation/layers).' -ForegroundColor Green

    # Validate map definitions. Prepared cache PNGs are intentionally absent from this source;
    # all other extension-owned artwork must still be present at build time.
    $mapDefFiles = Get-ChildItem -LiteralPath $mapsRoot -Filter '*.jsonc' -File -Recurse
    foreach ($mapDefFile in $mapDefFiles) {
        try { $mapDef = Get-Content -LiteralPath $mapDefFile.FullName -Raw | ConvertFrom-Json }
        catch { throw "Invalid map definition: $($mapDefFile.FullName) : $($_.Exception.Message)" }
        foreach ($layerProp in $mapDef.Layers.PSObject.Properties) {
            $imagePath = [string]$layerProp.Value.ImagePath
            if ($imagePath.StartsWith('DMROOT/', [StringComparison]::OrdinalIgnoreCase)) {
                $nativeRel = $imagePath.Substring(7).Replace('/', [IO.Path]::DirectorySeparatorChar)
                $nativeAsset = Join-Path $dynamicMapsDll.Directory.FullName $nativeRel
                if (-not (Test-Path -LiteralPath $nativeAsset -PathType Leaf) -or (Get-Item -LiteralPath $nativeAsset).Length -eq 0) {
                    throw "Required DynamicMaps artwork referenced by $($mapDefFile.Name) is missing: $nativeAsset"
                }
                continue
            }
            if (-not $imagePath.StartsWith('Maps/', [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected image path in $($mapDefFile.FullName): $imagePath" }
            $normalized = $imagePath.Replace('\','/')
            if ($runtimePreviewPaths.Contains($normalized)) { continue }
            $assetPath = Join-Path $SourceRoot (Rel $normalized)
            if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or (Get-Item -LiteralPath $assetPath).Length -eq 0) { throw "Required static map asset is missing: $assetPath" }
            if ($normalized.EndsWith('.png', [StringComparison]::OrdinalIgnoreCase) -and $normalized.IndexOf('/Factory_Classic/', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                $tilePath = [IO.Path]::ChangeExtension($assetPath, '.tiles')
                if (-not (Test-Path -LiteralPath $tilePath -PathType Leaf) -or (Get-Item -LiteralPath $tilePath).Length -le 24) { throw "Static raster tile pack is missing/empty: $tilePath" }
            }
        }
    }

    $factoryClassicRequired = @(
        'Maps/Factory_Classic/Factory_Classic.jsonc',
        'Maps/Factory_Classic/Layers/factory_layer_-1.png',
        'Maps/Factory_Classic/Layers/factory_layer_0.png',
        'Maps/Factory_Classic/Layers/factory_layer_1.png',
        'Maps/Factory_Classic/Layers/factory_layer_2.png'
    )
    foreach ($rel in $factoryClassicRequired) {
        $p = Join-Path $SourceRoot (Rel $rel)
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) { throw "Required Factory Classic file is missing: $p" }
    }

    # Keep current calibrated/Abstract implementation intact.
    $staticAbstract = @(
        'Maps/Interchange_Backport/AbstractRaster/Interchange-Ground.png',
        'Maps/Interchange_Backport/AbstractRaster/Interchange-Ground.tiles',
        'Maps/Interchange_Backport/AbstractRaster/Interchange-First.png',
        'Maps/Interchange_Backport/AbstractRaster/Interchange-First.tiles',
        'Maps/Interchange_Backport/AbstractRaster/Interchange-Second.png',
        'Maps/Interchange_Backport/AbstractRaster/Interchange-Second.tiles'
    )
    foreach ($rel in $staticAbstract) {
        $p=Join-Path $SourceRoot (Rel $rel)
        if (-not (Test-Path -LiteralPath $p -PathType Leaf) -or (Get-Item -LiteralPath $p).Length -eq 0) { throw "Required Manimal Abstract asset is missing: $p" }
    }

    Write-Host ''
    Write-Host "Building plugin against: $SptRoot"
    $project = Join-Path $SourceRoot 'DynamicMaps.Extended.csproj'
    $artifactsRoot = Join-Path $SourceRoot 'artifacts'
    if (-not $artifactsRoot.StartsWith($SourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe artifact directory.' }
    $buildOut = Join-Path $artifactsRoot 'bin'
    $objOut = Join-Path $artifactsRoot 'obj'
    New-Item -ItemType Directory -Force -Path $buildOut,$objOut | Out-Null

    # Use a forward-slash terminator for the MSBuild intermediate path.
    # A trailing Windows backslash in a native PowerShell argument can be consumed
    # while dotnet/MSBuild reconstructs its command line, which makes MSBuild reject
    # BaseIntermediateOutputPath as not ending in a directory separator.
    $objOutMsBuild = ($objOut -replace '\\', '/') + '/'
    & dotnet build $project -c Release --output $buildOut "-p:SptRoot=$SptRoot" "-p:BaseIntermediateOutputPath=$objOutMsBuild"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
    $dll = Join-Path $buildOut 'DynamicMaps.Extended.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Build succeeded but DLL was not found: $dll" }

    $releaseRoot=Join-Path $artifactsRoot 'release'
    $stageRoot=Join-Path $releaseRoot 'DynamicMaps-Extended'
    if (-not $stageRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe stage directory.' }
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
    $pluginOut=Join-Path $stageRoot 'BepInEx\plugins\DynamicMaps-Extended'
    $mapsOut=Join-Path $pluginOut 'Maps'
    $assetMetaOut=Join-Path $pluginOut 'Assets'
    New-Item -ItemType Directory -Force -Path $mapsOut,$assetMetaOut | Out-Null
    Copy-Item -LiteralPath $dll -Destination $pluginOut
    Get-ChildItem -LiteralPath $mapsRoot -Force | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $mapsOut -Recurse -Force }
    Copy-Item -LiteralPath $manifestPath -Destination $assetMetaOut
    Copy-Item -LiteralPath $structurePath -Destination $assetMetaOut

    # Safety cleanup for legacy generated metadata if an older working tree is overlaid.
    foreach ($rel in @('BUNDLED-RASTER-ASSETS.json','CORE-ASSET-HASHES.json','ASSET-MANIFEST.json')) {
        $p=Join-Path $mapsOut $rel
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
    }

    Copy-Item -LiteralPath (Join-Path $SourceRoot 'README.md') -Destination $pluginOut
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'LICENSE') -Destination $pluginOut
    foreach ($notice in @('THIRD-PARTY.md','LICENSING-STATUS.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $SourceRoot $notice) -Destination $pluginOut }
    Copy-Item -LiteralPath (Join-Path $SourceRoot 'third-party/tarkov-dev-svg-maps/LICENSE.md') -Destination (Join-Path $pluginOut 'ARTWORK-LICENSE.md')
    $docsOut = Join-Path $pluginOut 'docs'
    New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
    foreach ($doc in @('ARTWORK-MATRIX.md','BUNDLED-MAPS.md','TEST-STATUS.md')) { Copy-Item -LiteralPath (Join-Path $SourceRoot ('docs/' + $doc)) -Destination $docsOut }

    if (-not (Test-Path -LiteralPath $releaseRoot)) { New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null }
    $zip=Join-Path $releaseRoot 'DynamicMaps-Extended-2.1.0-base.zip'
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $zip -Force

    Write-Host ''
    Write-Host 'BUILD COMPLETE' -ForegroundColor Green
    Write-Host "Release ZIP: $zip"
    Write-Host "All generated build files are contained under: $artifactsRoot"
    Write-Host 'This is a developer base ZIP, not a complete installation.'
    Write-Host 'Run tools/bundle-maps.py with a verified prepared plugin folder to produce the complete release ZIP.'
}
catch {
    $ExitCode=1
    Write-Host ''
    Write-Host 'BUILD FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
}
finally {
    Write-Host ''
    if (-not $NoPause) { [void](Read-Host 'Press ENTER to close') }
}
exit $ExitCode
