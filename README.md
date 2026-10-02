# DynamicMaps Extended 0.7.0

A self-contained extension for **DynamicMaps** on **SPT 4.1.x / EFT 40743**.

It adds support for newer/backported map layouts while leaving the original DynamicMaps installation untouched and preserving DynamicMaps' normal player tracking, minimap, floor selection, quest markers, extracts, transits and other marker providers.

## One-folder install / uninstall

The release contains one extension folder:

```text
BepInEx/
└─ plugins/
   ├─ mpstark-dynamicmaps/
   │  └─ ...original DynamicMaps files only
   │
   └─ DynamicMaps-Extended/
      ├─ DynamicMaps.Extended.dll
      ├─ Maps/
      │  ├─ Icebreaker/
      │  ├─ Interchange_Backport/
      │  ├─ Labs_Backport/
      │  ├─ Factory_Classic/             (packaged static Classic map)
      │  └─ StyleAssets/                 (verified alternate artwork)
      ├─ README.md
      ├─ LICENSE
      └─ THIRD-PARTY.md
```

**Install:** copy `DynamicMaps-Extended` into `BepInEx/plugins/`.

**Uninstall:** delete `BepInEx/plugins/DynamicMaps-Extended/`.

The extension does **not** copy files into `mpstark-dynamicmaps`, MapVariants or any backport mod.

## Dependencies

Required:
- DynamicMaps
- MapVariants

Optional:
- FactoryClassic
- Manimal Interchange
- Manimal Lighthouse
- Manimal Labs / LabsBoiler
- Manimal Icebreaker

## Included map support

- Icebreaker
- Manimal Interchange
- Manimal Labs / LabsBoiler expansion
- Factory Classic
- original/backport selection through MapVariants where MapVariants manages the location
- Manimal Lighthouse continues using DynamicMaps' native Lighthouse artwork until a verified/generated Manimal-specific overhead map exists

**Terminal is not included in 0.7.0.**

## DynamicMaps integration

DynamicMaps continues loading its own `Maps` directory normally.

`DynamicMaps.Extended.dll` adds the extension's separate `Maps` folder to DynamicMaps' map-definition collection at runtime. The extension map image paths are resolved to absolute paths inside `DynamicMaps-Extended`, so both SVG and PNG layers remain self-contained in this folder.

The real EFT map ID is not replaced globally. Private aliases are only used at DynamicMaps' map-filter step where variant selection requires them.

DynamicMaps still owns:
- live player tracking
- friendly/Fika player markers
- minimap updates
- automatic floor selection
- quest markers
- extracts/transits
- corpse/backpack/loot markers
- all other DynamicMaps marker providers

## Abstract / Satellite artwork selector

F12 contains:

```text
DynamicMaps Extended
├─ Map Style
│  └─ Preferred Style = Abstract / Satellite
├─ Map Style Overrides
│  ├─ Ground Zero = UseGlobal / Abstract / Satellite
│  ├─ Customs = UseGlobal / Abstract / Satellite
│  ├─ Factory = UseGlobal / Abstract / Satellite
│  ├─ Factory Classic = UseGlobal / Abstract / Satellite
│  ├─ Woods = UseGlobal / Abstract / Satellite
│  ├─ Interchange = UseGlobal / Abstract / Satellite
│  ├─ Labs = UseGlobal / Abstract / Satellite
│  ├─ Lighthouse = UseGlobal / Abstract / Satellite
│  ├─ Icebreaker = UseGlobal / Abstract / Satellite
│  ├─ Reserve = UseGlobal / Abstract / Satellite
│  ├─ Shoreline = UseGlobal / Abstract / Satellite
│  ├─ Streets = UseGlobal / Abstract / Satellite
│  └─ Labyrinth = UseGlobal / Abstract / Satellite
└─ Debug
   └─ EnableDebugLogging
```

The style switch changes **artwork only**. **No missing style is generated or fabricated.** Satellite PNGs prepared by the builder are stitched only from real existing Tarkov.dev tile assets.

Style selection is **per floor/layer**, so mixed maps are supported. For example, Interchange can use Satellite for the ground floor while upper floors stay Abstract when only Abstract artwork exists.

Style changes apply immediately to the currently loaded DynamicMaps map during a raid when possible. Only existing layer sprites are refreshed; player tracking, Fika markers, extracts, quests, zoom, map position, selected floor and DynamicMaps marker systems remain intact.

## Fika

The extension does not patch Fika DLLs, create Fika packets, or replace multiplayer synchronization/player tracking.

MapVariants / FactoryClassic remain responsible for their own variant synchronization. DynamicMaps continues handling live map/player markers.

## Raster performance

PNG/JPG layers are skipped during DynamicMaps' global all-map startup precache.

When an extension raster map is selected, raster floors for **that selected map only** are preloaded. Raster textures use `markNonReadable=true`, are cached for the raid, and are released on map switch / raid end. The extension never calls `Resources.UnloadUnusedAssets()` during a raid.

DynamicMaps' SVG cache remains untouched.

## F12 debug

Enable **Debug -> EnableDebugLogging** before reproducing a problem and send `BepInEx/LogOutput.log`.

Diagnostics include current map, dependency/Fika detection, MapVariants/FactoryClassic state, alias decisions, extension MapDef loading, live artwork-switch decisions, layer/fallback details, raster decode timing/memory, cache state, and aspect-ratio warnings that can expose bad artwork crop/alignment.

## Factory Classic

Factory Classic uses the supplied static map package under:

```text
DynamicMaps-Extended/Maps/Factory_Classic/
```

It no longer generates a map from NavMesh at runtime.

The MapDef is isolated behind the private `dmext_factory_classic` alias, so current Factory continues using DynamicMaps' normal definition. When FactoryClassic reports Classic selected/loaded, DynamicMaps filtering is redirected to the packaged Classic definition.

DynamicMaps 1.2.1 requires `TesselationIndex` on every `MapLayerDef`; v0.7.0 includes `"TesselationIndex": 0` on all four Classic layers so Factory Classic loads in the Character -> Maps dropdown and in raids.

The supplied Factory Classic PNG layers are treated as **Abstract**. If Satellite is selected, Factory Classic remains on its supplied Abstract artwork because no separate Satellite set is provided.

## Building

Run `build.ps1`.

The builder asks for your SPT root, prepares missing Tarkov.dev map assets, builds `DynamicMaps.Extended.dll`, and creates:

```text
release\DynamicMaps-Extended-0.7.0-SPT4.1.zip
```

The builder does not install anything into SPT.

## One-time migration from old test builds

If you used the old `DynamicMaps-BackportCompat` builds, clean them once:

1. delete `BepInEx/plugins/DynamicMaps-BackportCompat/`
2. delete `BepInEx/plugins/mpstark-dynamicmaps/Maps/BackportCompat/`
3. install `BepInEx/plugins/DynamicMaps-Extended/`

After that, update/uninstall is one-folder only.

See `THIRD-PARTY.md` for credits and licenses.
