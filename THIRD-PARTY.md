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

The 2.1.0 complete ZIP includes prepared raster previews, warm atlases and NVTILES2 tile packs from the captured artwork set. The DLL loads local files only. Frozen metadata and historical fingerprints are retained for provenance; no live metadata request, downloader or runtime map generation remains.

Artwork retains its original source attribution and applicable terms. Some content is credited to third parties, including TarkovBOT.eu for Icebreaker. Prepared packaging does not establish additional redistribution permissions.

## Factory Classic

The Factory Classic map files in this project were sourced from an older DynamicMaps version. DynamicMaps is MIT licensed. These files retain their original attribution/source context and are not independently relicensed by DynamicMaps Extended.

## Manimal Interchange Abstract runtime copies

The PNG previews and `.tiles` packs under `Maps/Interchange_Backport/AbstractRaster/` are offline rasterizations of the pinned Tarkov.dev Interchange SVG noted above. They exist to avoid Unity.VectorGraphics rendering incompatibilities; no replacement artwork was invented.

## Woods, Factory and Streets Abstract

These maps use the original bundled SVGs through DynamicMaps' vector loader and retain the upstream terms above. Earlier experimental PNG/tile siblings are not included in 2.1.0. Original viewBoxes, margins and calibration are preserved.

## Provenance records

Development capture/calibration reports are kept under `docs/provenance/` and are not copied into the runtime package.
