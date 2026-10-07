# Changelog

## 2.1.0

- Bundle all 42 prepared raster layers in the complete installation ZIP.
- Remove runtime downloading, asset generation, download progress UI and F12 download settings.
- Restore sharp Woods, Factory and Streets Abstract SVG rendering while reusing cached sprites instead of repeatedly refreshing them.
- Preserve full-map/minimap zoom, viewed coordinate and live markers when changing artwork, including during an interrupted zoom animation.
- Restore native artwork when switching away from Extended styles, including Interchange variants.
- Bound tile selection and decoded texture caches; reuse tile rendering objects and skip unchanged calibration writes.
- Retain high-resolution Satellite content and original artwork alignment.
- Add optional frame/viewport diagnostics and clean obsolete code, experimental artwork copies and release documentation.

## 2.0.0

Major architecture and feature release for SPT 4.1.x.

- Added built-in C# first-run raster preparation with in-game progress.
- Added pinned Tarkov.dev structural verification and frozen exact valid tile coordinates.
- Added local `NVTILES2` high-resolution raster packs, bounded asynchronous tile decoding, and warm-atlas loading.
- Added one-folder persistent cache under `DynamicMaps-Extended/AssetCache/`.
- Added automatic migration from the temporary sibling cache used by pre-release 1.5 test builds.
- Added seam-free preview/warm-atlas compositing.
- Fixed repeated asset-completion refreshes/cache clears.
- Removed repeated invalid `MapDef.Name` reflection lookup warnings.
- Added Fika Headless guard before patching/workers/asset preparation.
- Added Icebreaker 16-floor slider normalization/safety.
- Preserved meaningful per-map artwork choices and runtime map-variant integration.
- Release archives no longer bundle Tarkov.dev raster downloads; they are prepared locally.

## 1.0.0

Initial public release.