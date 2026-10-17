# DynamicMaps Extended 2.5.0

- Added faction colours: Black Division grey, UNTAR cyan-blue, and RUAF/Remnant olive green. Change them in F12.
- UNTAR leaders and officers now use boss stars and DynamicMaps boss visibility settings. Followers keep arrows.
- Black Division markers have a light outline for visibility on dark maps.
- Improved map clarity, including Labs, and reduced temporary blur when quickly peeking at the map.
- Fixed map/minimap refresh problems when switching artwork.
- Includes updated Icebreaker artwork options and alignment from the preceding test releases.
- Added debug logging to help investigate problems.

## Installation
A clean install is preferred. Close the game, remove the old DynamicMaps-Extended plugin folder, then extract BepInEx into your SPT game folder. Keep your required dependency mods installed.
All map artwork is bundled; no separate artwork downloads are needed.
Requires DynamicMaps and MapVariants. Icebreaker requires Manimal Icebreaker. Faction mods are optional and are not included.

Existing custom faction colours are preserved. Unchanged old default colours automatically move to the new palette once. RUAF/Remnant use #899B45, UNTAR #00BFFF and Black Division #555555.
Only UNTAR leader/officer boss classification is extended. Other factions retain their native marker shapes and visibility rules.

For bug reports, enable Debug / EnableDebugLogging in F12, reproduce the issue and include BepInEx/LogOutput.log with the map, floor, artwork style and a screenshot.
Artwork credits and licences are included.

---

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