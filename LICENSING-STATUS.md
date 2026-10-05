# Licensing status for public releases

This file records the publication status of third-party map artwork used during development of DynamicMaps Extended.

## Safe to publish now

- DynamicMaps Extended C# source: **MIT** (this repository's LICENSE).
- The v1.5.0 **source-only** archive under `releases/v1.5.0/`: **safe to publish** because it contains no third-party raster/SVG artwork.
- DynamicMaps itself is a dependency and is not redistributed by DynamicMaps Extended.

## Tarkov.dev SVG artwork

Source: https://github.com/the-hideout/tarkov-dev-svg-maps

The repository states **CC BY-NC-SA 4.0** and also states that the artwork must not be used in software intended to facilitate cheating or an unfair advantage in Escape from Tarkov.

For non-commercial, non-cheat use, the repository grants sharing/adaptation rights subject to attribution and ShareAlike. If any SVG-derived artwork is bundled in a future release, it must remain clearly attributed and covered by the applicable CC BY-NC-SA terms rather than the MIT license used for this project's code.

## Tarkov.dev raster/tile artwork

The following development assets were sourced from `assets.tarkov.dev`:

- Ground Zero
- Customs
- Factory
- Woods
- Reserve
- Shoreline
- Interchange
- Labs
- Labyrinth

The Tarkov.dev website source code is MIT licensed, but that does **not** establish a redistribution license for every hosted map tile/image. Current map metadata identifies the map authors, but no explicit public redistribution license for the raster tiles was found during the v1.5.0 publication review.

**Status: do not bundle these raster/tile assets in a public release until permission is confirmed.**

## Icebreaker

Tarkov.dev's current map metadata credits the Icebreaker interactive map to **TarkovBOT.eu** while serving the tiles from `assets.tarkov.dev`.

No explicit public redistribution license for the Icebreaker raster tiles was found.

**Status: permission required before bundling the Icebreaker artwork in a public release.**

## Factory Classic supplied artwork

The Factory Classic artwork originally supplied for integration does not match the currently pinned Tarkov.dev Factory SVG path data, so its exact provenance/license was not verified during this review.

**Status: do not bundle the supplied Factory Classic artwork publicly until its source/permission is confirmed or it is replaced with artwork having clear redistribution terms.**

## Practical release policy

Until permissions are confirmed:

1. Publish source/code only.
2. Do not attach a full install ZIP containing the prepared raster/tile artwork.
3. Keep third-party artwork out of the repository/release assets.
4. If permission is granted later, include the permission scope, attribution and required license notices in the release.
