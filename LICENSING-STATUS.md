# Licensing status for DynamicMaps Extended v2.0.0

DynamicMaps Extended v2.0.0 separates the distributable mod from the large mutable Tarkov.dev raster cache.

## Project code

DynamicMaps Extended C# source is released under this repository's MIT license.

## DynamicMaps

DynamicMaps is a dependency and its DLL is not redistributed by this project. The upstream DynamicMaps repository is MIT licensed. See `THIRD-PARTY.md` for attribution.

## Tarkov.dev SVG artwork

Source: `the-hideout/tarkov-dev-svg-maps`

The SVG-map repository is distributed under CC BY-NC-SA 4.0 and includes additional project-specific usage conditions. SVG-derived artwork bundled by DynamicMaps Extended remains third-party content and is not relicensed under this project's MIT license.

## Tarkov.dev raster/tile artwork

The v2.0.0 release ZIP does **not** bundle the downloaded Tarkov.dev raster cache.

At runtime, if a usable local cache is absent, the installed DLL verifies the current official map structure against the pinned metadata lock and then downloads only the frozen known-valid tile coordinates from the official Tarkov.dev asset host. The resulting previews, warm atlases and `NVTILES2` packs are created locally under `DynamicMaps-Extended/AssetCache/`.

This design avoids redistributing the mutable CDN raster bytes as GitHub release assets. It does not claim ownership of those upstream images or change their original licensing/attribution.

## Icebreaker

Current Tarkov.dev metadata credits the Icebreaker interactive map to TarkovBOT.eu. DynamicMaps Extended does not bundle the downloaded Icebreaker raster cache in its release ZIP.

## Factory Classic

Factory Classic files used by this project came from an older DynamicMaps version. The DynamicMaps code repository is MIT licensed; artwork/source attribution is retained in `THIRD-PARTY.md` and is not relicensed by DynamicMaps Extended.