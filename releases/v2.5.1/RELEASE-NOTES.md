# DynamicMaps Extended 2.5.1

## What's new

- Fixed faction colours for bots controlled by a Fika host or headless client.
- Black Division now uses brighter grey. Removed the extra outline and its setting.
- Added separate ISB and White Tusks colour options.
- Added automatic colour controls for custom bot roles registered through MoreBotsAPI.
- Added optional "Group with" dropdowns to share colours between roles. Leave these on Automatic for normal use.
- Reorganised F12: faction colours on/off comes first; Debug is at the bottom.
- Existing custom colours are preserved. Previous Black Division defaults upgrade automatically.

Faction grouping only changes map colours. It does not change bot behaviour, teams or boss icons.

## Installation

**A clean install is preferred when using the full ZIP.**

1. Close the game.
2. Remove the old `BepInEx/plugins/DynamicMaps-Extended` folder.
3. Extract the full release ZIP into your SPT folder.

All map artwork is included. Requires **DynamicMaps** and **MapVariants**. Faction mods are installed separately.

**Using the small Update ZIP?** Extract over an existing complete installation; do not delete its artwork folders.

## Bug reports

Include the map, faction, artwork style, whether you use Fika, and steps to reproduce. Enable **Debug → EnableDebugLogging** before reproducing the issue and attach `BepInEx/LogOutput.log`.
