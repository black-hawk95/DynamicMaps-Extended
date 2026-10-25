# DynamicMaps Extended 2.5.2

## What's new

- **Labyrinth Satellite alignment fixed.** The Satellite artwork for the Labyrinth was drawn 180° out of phase with the map's coordinate rotation, so the player arrow and every extract, lever and locked-door marker sat on the opposite side of the layout. The artwork layer is now rotated to match.
- Only the Satellite style was affected. Native DynamicMaps artwork was always correct, and there is no change on any other map.
- Markers, zoom, floor selection and the high-resolution tile overlay are unchanged. Switching between Satellite and native artwork still works during a raid.

## Installation

**A clean install is preferred when using the full ZIP.**

1. Close the game.
2. Remove the old `BepInEx/plugins/DynamicMaps-Extended` folder.
3. Extract the full release ZIP into your SPT folder.

All map artwork is included. Requires **DynamicMaps** and **MapVariants**. Faction mods are installed separately.

**Using the small Update ZIP?** Extract over an existing complete installation; do not delete its artwork folders.

If F12 displays an older version after updating, close the game and remove `BepInEx/cache/chainloader_typeloader.dat`, then restart to rebuild BepInEx's plugin cache.

## Bug reports

Include the map, faction, artwork style, whether you use Fika, and steps to reproduce. Enable **Debug → EnableDebugLogging** before reproducing the issue and attach `BepInEx/LogOutput.log`.
