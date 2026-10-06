# DynamicMaps Extended v2.0.0

Major architecture release for SPT 4.1.x.

## Highlights

- Built-in C# first-run raster preparation with in-game progress.
- One-folder cache at `BepInEx/plugins/DynamicMaps-Extended/AssetCache/`.
- Normal overwrite updates preserve the local raster cache.
- 42 structurally verified raster layers using frozen known-valid Tarkov.dev tile coordinates.
- Local high-resolution `NVTILES2` refinement with bounded asynchronous decoding.
- Seam-free preview and warm-atlas generation.
- Icebreaker 16-floor slider normalization and safety fixes.
- Fika Headless guard before UI patches/workers/asset preparation.
- Fixed repeated terminal asset refresh/cache clear behavior.
- Removed invalid `MapDef.Name` reflection warning spam.
- Migration from temporary pre-release sibling cache builds without network redownload.

## Install

Extract `DynamicMaps-Extended-2.0.0-SPT4.1.zip` into the SPT root and allow overwrite.

Tarkov.dev raster cache files are not bundled in the release. If no valid local cache exists, the DLL prepares them locally on first launch after verifying upstream structure.