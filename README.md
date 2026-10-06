# DynamicMaps Extended v2.0.0

DynamicMaps Extended is an extension for **DynamicMaps 1.2.1** on **SPT 4.1.x**. It adds verified alternate map artwork, high-resolution local raster refinement, map-variant support, and compatibility fixes while leaving DynamicMaps in control of player tracking, quest/extract markers, minimap behavior, floor selection, and marker providers.

## Highlights

- **Built-in C# first-run asset preparation** — no PowerShell, curl, helper executable, or child process at runtime.
- **One-folder cache** under `BepInEx/plugins/DynamicMaps-Extended/AssetCache/`.
- Normal updates are simply **extract → overwrite → launch**; the cache is preserved because release ZIPs do not contain `AssetCache/`.
- Clean uninstall: delete `BepInEx/plugins/DynamicMaps-Extended/`.
- **42 verified raster layers** using a pinned Tarkov.dev structural lock and frozen known-valid tile coordinates.
- Local `NVTILES2` high-resolution tile packs with asynchronous, bounded decoding and cache limits.
- Seam-free preview/warm-atlas compositor.
- F12 artwork selection with only meaningful options.
- Icebreaker 16-floor slider safety and map-level normalization.
- Fika compatible; **Fika Headless is detected and the client/UI plugin disables itself before workers or Harmony patches start**.
- Migration support for the temporary pre-2.0 sibling cache folder `DynamicMaps-Extended-AssetCache`.

## Install

Requirements:

- SPT 4.1.x
- DynamicMaps 1.2.1
- MapVariants 1.0.1

Extract `DynamicMaps-Extended-2.0.0-SPT4.1.zip` into your SPT root and allow overwrite.

On first launch, if no valid cache exists, DynamicMaps Extended verifies the current official Tarkov.dev map structure against the pinned metadata lock, downloads only the frozen valid raster tile coordinates, and builds local previews, warm atlases, and high-resolution tile packs. Later launches use the local cache.

## Updating

Extract the new release ZIP into the SPT root and allow overwrite. Do **not** delete `DynamicMaps-Extended/AssetCache/` unless you intentionally want the raster cache rebuilt/redownloaded.

## Uninstall

Delete:

```text
BepInEx/plugins/DynamicMaps-Extended/
```

This removes both the mod and its locally prepared cache.

## Cache and diagnostics

```text
BepInEx/plugins/DynamicMaps-Extended/AssetCache/
├── Packs/
├── State/
└── Staging/
```

Preparation log:

```text
BepInEx/plugins/DynamicMaps-Extended/AssetCache/State/asset-preparation.log
```

## Build from source

Run `build.cmd`, select your SPT 4.1.x root, and use the ZIP created under `artifacts/release/`.

The source build itself is offline. Runtime first-use asset preparation requires access to the official Tarkov.dev metadata and tile hosts.

## Licensing and third-party content

See [THIRD-PARTY.md](THIRD-PARTY.md) for attribution and third-party licensing notes. Tarkov.dev raster artwork is **not bundled in the release ZIP**; it is prepared locally on the player's PC after structural verification.