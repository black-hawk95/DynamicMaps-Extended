# DynamicMaps Extended

An addon for **DynamicMaps on SPT 4.1.x** that adds alternate map artwork, support for backported maps, and custom faction marker colours.

[Download the latest release](https://github.com/black-hawk95/DynamicMaps-Extended/releases/latest) · [Release history](https://github.com/black-hawk95/DynamicMaps-Extended/releases)

## New in 2.5.1

These updates supersede the older Black Division colour and outline information below.

- **Brighter Black Division markers:** default grey is now `#777777`. The extra outline and its F12 control have been removed; the original DynamicMaps border remains. Previous defaults upgrade once, while other custom colours are preserved.
- **Fika faction colours:** recognises remote AI bots for faction colours and existing UNTAR leader/officer boss stars.
- **ISB and White Tusks:** separate colour groups, defaulting to teal (`#1ABC9C`) and off-white (`#E8E8F0`).
- **Automatic discovery:** custom roles registered through MoreBotsAPI receive colour controls at startup. Known roles are grouped automatically; unfamiliar roles get their own colour entry.
- **Optional grouping:** leave **Group with** on **Automatic** for normal use. Select another group to share its colour, or **DynamicMaps default** to use native colours. This changes colours only—not teams, hostility, spawning or boss icons.
- **Tidier F12 settings:** Enable faction colours comes first and Debug is last.
- Faction mods must be installed separately. Restart after adding new mods. Automatic discovery cannot distinguish mods that reuse vanilla bot identities without separate registered roles.

For this release, download **DynamicMaps-Extended-2.5.1-SPT4.1.zip**. A clean install is preferred when using the full ZIP. The small Update ZIP requires an existing complete installation; keep its artwork folders.

If F12 displays an older version after updating, close the game and remove `BepInEx/cache/chainloader_typeloader.dat`, then restart to rebuild BepInEx's plugin cache.

## Features

### Map artwork and styles

- Choose **Recommended**, **DynamicMaps Vanilla**, **Satellite**, or **Abstract**, where available.
- Set a global preference or choose a different style for each map in **F12 → DynamicMaps Extended**.
- Change artwork during a raid or in the Character → Map screen.
- Style switching preserves the current floor, zoom, position, markers and player trail.
- Floors without the selected artwork fall back to an available style.
- High-resolution Satellite artwork uses local detail tiles for close-up viewing.
- Woods, Factory and Streets Abstract artwork uses SVG rendering for sharp lines when zooming.
- All prepared artwork is bundled in the release ZIP. No first-launch artwork downloads or separate map downloads are required.

### Map and floor support

- **Factory Classic:** separate artwork for the classic layout.
- **Manimal Interchange:** support for the backported layout and its floors.
- **Manimal Labs / LabsBoiler:** support for the expanded layout, with prepared detail levels to improve clarity while moving and zooming the map.
- **Icebreaker:** Original Satellite and RE3MR artwork options, aligned deck layers, and a 16-floor selector.
- **MapVariants integration:** selects the appropriate supported map definition for the active variant.
- Additional artwork for supported standard maps, including Customs, Ground Zero, Factory, Woods, Interchange, The Lab, Reserve, Shoreline, Streets and Labyrinth. Style availability differs by map and floor.
- Lighthouse uses the available DynamicMaps Vanilla artwork.

The relevant map mods must be installed separately. Extended supplies map-display support; it does not install playable locations.

### Faction marker colours

Recognised addon bots share a faction colour across soldiers, leaders and followers. Change the colours in **F12 → DynamicMaps Extended → Faction Colours**.

| Faction | Default colour |
|---|---|
| Black Division | Grey — `#555555` |
| UNTAR | Cyan-blue — `#00BFFF` |
| RUAF | Olive green — `#899B45` |
| Remnant role from RUAF Come Home | Olive green — `#899B45` |

- Black Division markers have an adjustable light outline for visibility on dark artwork.
- UNTAR leaders and officers use DynamicMaps' boss-star icons and native boss visibility handling. UNTAR followers keep arrows.
- Other factions retain their native marker classification and shapes.
- Colour changes apply to existing faction markers. Disabling faction colours restores native colours.
- Existing custom colours are preserved during updates. Unchanged older defaults migrate to the new palette once.
- Unsupported roles retain their native colours. Faction mods are optional and are not bundled.

### DynamicMaps integration

DynamicMaps continues to handle live player and bot tracking, friendly-player markers, quest markers, extracts, transits, player trails, automatic floor selection and minimap behaviour. Its visibility settings still apply.

Extended adds its files in a separate plugin folder and does not replace the original DynamicMaps installation. It does not patch Fika networking or replace player synchronisation. The client/UI addon disables itself on Fika Headless.

### Performance and diagnostics

- Prepared local artwork, asynchronous raster loading and bounded detail-tile caches.
- Reuse of loaded artwork to reduce temporary blur when quickly peeking at the map.
- Map and corner-minimap refresh handling when switching artwork.
- Optional debug logging for artwork loading, map selection, frame timing, faction recognition and errors.

Initial artwork loading or switching can still cause a brief hitch; performance depends on the map, hardware and other installed mods.

## Requirements

- **SPT 4.1.x**
- [DynamicMaps](https://github.com/acidphantasm/SPT-DynamicMaps)
- [MapVariants](https://github.com/LennoxP90/SPT-MapVariants)

Install Factory Classic, Manimal map mods and faction mods separately if you want their corresponding features.

## Installation and updates

**A clean install is preferred.**

1. Close the game.
2. If updating, remove the old `BepInEx/plugins/DynamicMaps-Extended` folder. Keep your dependency mods installed.
3. Download the full `DynamicMaps-Extended-2.5.0-SPT4.1.zip` from Releases, rather than GitHub's automatically generated source-code ZIP.
4. Extract its `BepInEx` folder into your SPT game folder.
5. Keep the included `Maps`, `Assets` and `AssetCache` folders alongside `DynamicMaps.Extended.dll`.
6. Launch the game and configure artwork and faction colours in F12.

Your F12 configuration is stored separately in `BepInEx/config/com.blackhawk.dynamicmapsextended.cfg`. Removing only the plugin folder preserves those settings.

To uninstall, delete `BepInEx/plugins/DynamicMaps-Extended`. The original DynamicMaps installation remains separate.

## Reporting problems

Enable **Debug → EnableDebugLogging** in DynamicMaps Extended before reproducing the problem. Include:

- Mod version, map, floor and selected artwork style.
- What happened and the steps to reproduce it.
- A screenshot where useful.
- `BepInEx/LogOutput.log` from that session.

Disable detailed logging again after testing. Report issues through [GitHub Issues](https://github.com/black-hawk95/DynamicMaps-Extended/issues).

## Building from source

Install a .NET SDK that supports the project and run `build.ps1 -SptRoot "C:/SPT" -NoPause`.

The source build creates a base package; the large prepared artwork cache is supplied in the full release ZIP. See [bundling instructions](docs/BUNDLED-MAPS.md). Building does not install the mod or publish a release.

## Credits and licences

Project code is MIT licensed. Third-party artwork retains its original licences and attribution. See [THIRD-PARTY.md](THIRD-PARTY.md), [LICENSING-STATUS.md](LICENSING-STATUS.md), and the attribution/licence files included with Icebreaker artwork.

For version-specific changes, see the [release notes](https://github.com/black-hawk95/DynamicMaps-Extended/releases).


---

### Support / Donations

If you'd like to support my work, you can [buy me a coffee](https://ko-fi.com/its_blackhawk) ☕

**Solana (SOL)**  
`RtkZyhpCD7rAmcsocf3RWPizCCzEdGNuUAX7sBcD1pF`

**Bitcoin (BTC)**  
`bc1qj983ved6kp4uz6r6t285j0r5tyf5h6jph9eajl`

**Ethereum (ETH)**  
`0xEaA910a5A779C85Fb9F07CA22bd2d2847a5969A5`

Please double-check the address and network before sending.
