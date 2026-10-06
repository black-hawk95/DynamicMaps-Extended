# First-run raster assets

DynamicMaps Extended prepares raster map artwork inside `DynamicMaps.Extended.dll`; no PowerShell, curl, CMD, helper EXE, or child process is launched.

Runtime flow:
1. Check/migrate existing local cache.
2. Verify the current official Tarkov.dev structural metadata against the v2.0.0 lock.
3. Request only the frozen known-valid tile coordinates for missing layers.
4. Validate downloaded PNGs and build the compact `NVTILES2` pack.
5. Build preview and warm-atlas PNGs locally.
6. Validate the prepared outputs.
7. Delete successful loose source PNG staging.
8. Keep the pack/readiness/staging data under `BepInEx/plugins/DynamicMaps-Extended/AssetCache/`.

The temporary v1.5 sibling cache at `BepInEx/plugins/DynamicMaps-Extended-AssetCache/` is automatically migrated into the one-folder cache with no network download.

Detailed log:
`BepInEx/plugins/DynamicMaps-Extended/AssetCache/State/asset-preparation.log`