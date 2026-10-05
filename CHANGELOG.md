# Changelog

## 1.5.0 — 2026-10-05

- Finalized the verified per-map artwork matrix and removed duplicate/meaningless choices.
- Added/updated Factory Classic, Icebreaker, Manimal Interchange, Manimal Labs, Reserve Satellite and Labyrinth support.
- Added separate calibration profiles where the same source artwork is used by different map variants.
- Reworked Manimal Interchange Abstract to use an offline-rasterized runtime path instead of Unity SVG rendering.
- Added asynchronous raster previews, local high-resolution tile packs and grouped progressive sharpening.
- Added a delayed ~3K whole-floor warm atlas for the active/default floor.
- Added cache reuse and slow-frame protection so minimap opening does not synchronously decode native-resolution maps.
- Fixed Icebreaker level selector handling for all 16 valid levels (0–15).
- Improved high-resolution refinement so sharpening begins earlier and reveals coherent regions.
- Preserved Fika compatibility without custom network packets or synchronization hooks.
- Public source snapshot excludes raster/tile artwork whose redistribution rights have not been verified.
