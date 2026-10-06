# Source layout

- `src/Core/` — plugin startup, dependency/headless guard, bridge helpers
- `src/Assets/` — built-in downloader, structural verification, raster cache, tile renderer
- `src/Maps/` — artwork selection/calibration/map-definition refresh
- `src/Patches/` — DynamicMaps integration patches
- `src/Integration/` — MapVariants / Factory Classic integration
- `src/UI/` — style refresh behavior
- `Assets/` — frozen runtime asset manifest + upstream structure lock
- `Maps/` — shipped static artwork/map definitions only

At runtime, generated preview/warm PNGs stay at their expected paths under the installed `Maps/` tree. Large downloaded `NVTILES2` packs, ready state, and resumable staging live under the **same mod folder** at:

`BepInEx/plugins/DynamicMaps-Extended/AssetCache/`

The release ZIP does not contain `AssetCache/`, so normal drag-and-drop overwrite updates preserve it. Deleting the entire `DynamicMaps-Extended` folder intentionally performs a complete uninstall including downloaded assets.