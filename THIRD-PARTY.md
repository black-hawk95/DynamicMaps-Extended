# Third-party artwork and licensing

The C# code in DynamicMaps Extended is covered by this project's MIT license. **Third-party artwork is not relicensed as MIT.**

## DynamicMaps

Source: https://github.com/mpstark/SPT-DynamicMaps

DynamicMaps Extended depends on DynamicMaps and does not redistribute its DLL. DynamicMaps remains responsible for the map UI, markers, coordinates, player tracking and native Vanilla artwork. The upstream DynamicMaps repository is MIT licensed; copyright belongs to its respective author(s).

## Tarkov.dev SVG maps

Source: https://github.com/the-hideout/tarkov-dev-svg-maps

The SVG-map repository is licensed under **CC BY-NC-SA 4.0** and carries its own attribution and conditions. The pinned source revision used for this project is `5a8b6115d1c0cf56f2ebaac1a96fa5ae3074d178`.

The pinned source snapshot and its upstream license are kept under `third-party/tarkov-dev-svg-maps/` for provenance and are **not copied wholesale into the runtime release ZIP**. Runtime Abstract choices derived from those maps remain third-party artwork under the applicable upstream terms.

## Tarkov.dev raster/tile artwork

Metadata/source capture revision: `the-hideout/tarkov-dev` commit `ef62766bbd7ffb294c7184f9b8bfe8ed0f18320e`.

The v2.0.0 release does **not** bundle the downloaded CDN raster cache. Before downloading missing artwork, the installed DLL verifies the current official Tarkov.dev map structure against the pinned v2.0.0 metadata lock (paths, zoom metadata, transform, bounds, rotation and raster layer paths). It requests only the frozen valid tile coordinates from `assets.tarkov.dev` and builds the local preview/warm/`NVTILES2` cache on the player's PC. Historical pixel hashes remain diagnostic because the upstream raster CDN can update artwork in place. No PowerShell, curl, helper executable, or child process is used at runtime.

This delivery method avoids redistributing the mutable CDN raster bytes in the release ZIP; it is not a claim that every upstream artwork source has the same license. Some artwork is credited to third parties, including TarkovBOT.eu for Icebreaker.

## Factory Classic

The Factory Classic map files in this project were sourced from an older DynamicMaps version. DynamicMaps is MIT licensed. These files retain their original attribution/source context and are not independently relicensed by DynamicMaps Extended.

## Manimal Interchange Abstract runtime copies

The PNG previews and `.tiles` packs under `Maps/Interchange_Backport/AbstractRaster/` are offline rasterizations of the pinned Tarkov.dev Interchange SVG noted above. They exist to avoid Unity.VectorGraphics rendering incompatibilities; no replacement artwork was invented.

## Provenance

Development capture/calibration reports are kept under `docs/provenance/` and are not copied into the runtime package.