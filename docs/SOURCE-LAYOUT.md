# Source layout

- `src/Core/`: startup, dependency/headless guard, diagnostics and bridge helpers.
- `src/Assets/`: read-only installation check, calibrated sprite cache, tile renderer and artwork workers.
- `src/Maps/`: artwork selection and in-place map-definition/viewport refresh.
- `src/Patches/`: DynamicMaps integration hooks.
- `src/Integration/`: MapVariants and Factory Classic integration.
- `src/UI/`: live-layer artwork refresh.
- `Assets/`: frozen asset manifest and provenance metadata; v2.0.0 filenames identify the unchanged asset set, not the plugin version.
- `Maps/`: static artwork and map definitions.
- `tools/`: offline bundling, regression checks and SVG geometry inspection.
- `docs/provenance/` and `third-party/`: original capture records and upstream SVG source/license.

Complete installation ZIPs add 42 preview images, 10 warm atlases and 42 tile packs. Packs are stored under `AssetCache/Packs/` inside the plugin folder. Static Manimal Abstract packs live beside their previews. No runtime download, generation or cache migration is performed.

Build outputs stay under ignored `artifacts/`; they are excluded from source archives. Historical experiments and generated raster siblings are not distributed.
