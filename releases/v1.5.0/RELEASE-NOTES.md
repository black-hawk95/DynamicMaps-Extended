# DynamicMaps Extended v1.5.0

Source snapshot for SPT 4.1.x / DynamicMaps 1.2.1.

## Final validation
- Icebreaker live level list repaired to 16 levels.
- Icebreaker slider normalized to levels 0..15 and exercised across the full range.
- Whole-floor warm atlas loaded successfully.
- Progressive high-resolution refinement activated successfully on tested raster maps, including Woods and Icebreaker.
- No DynamicMaps Extended errors were present in the final debug log.

## Main changes
- Final verified per-map Vanilla / Satellite / Abstract artwork matrix.
- Factory Classic, Icebreaker, Manimal Interchange, Manimal Labs, Reserve Satellite and Labyrinth support.
- Separate calibration profiles for variants that share source imagery.
- Offline-rasterized Manimal Interchange Abstract runtime path.
- Asynchronous preview loading and delayed ~3K active-floor warm atlas.
- Earlier coherent high-resolution tile refinement while zooming.
- Bounded cache/decode limits and slow-frame protection.
- Fika-compatible with no custom synchronization packets.

## Public asset note
This GitHub snapshot intentionally does not include prepared raster/tile artwork whose public redistribution rights have not been confirmed. See THIRD-PARTY.md inside the source archive.
