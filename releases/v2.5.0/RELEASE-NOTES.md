# DynamicMaps Extended 2.5.0

- Added faction colours: Black Division grey, UNTAR cyan-blue, and RUAF/Remnant olive green. Change them in F12.
- UNTAR leaders and officers now use boss stars and DynamicMaps boss visibility settings. Followers keep arrows.
- Black Division markers have a light outline for visibility on dark maps.
- Improved map clarity, including Labs, and reduced temporary blur when quickly peeking at the map.
- Fixed map/minimap refresh problems when switching artwork.
- Includes updated Icebreaker artwork options and alignment from the preceding test releases.
- Added debug logging to help investigate problems.

## Installation
A clean install is preferred. Close the game, remove the old DynamicMaps-Extended plugin folder, then extract BepInEx into your SPT game folder. Keep your required dependency mods installed.
All map artwork is bundled; no separate artwork downloads are needed.
Requires DynamicMaps and MapVariants. Icebreaker requires Manimal Icebreaker. Faction mods are optional and are not included.

Existing custom faction colours are preserved. Unchanged old default colours automatically move to the new palette once. RUAF/Remnant use #899B45, UNTAR #00BFFF and Black Division #555555.
Only UNTAR leader/officer boss classification is extended. Other factions retain their native marker shapes and visibility rules.

For bug reports, enable Debug / EnableDebugLogging in F12, reproduce the issue and include BepInEx/LogOutput.log with the map, floor, artwork style and a screenshot.
Artwork credits and licences are included.

