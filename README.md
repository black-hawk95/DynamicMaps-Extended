# DynamicMaps Extended

An extension for **DynamicMaps** on **SPT 4.1.x**.

It adds map support for newer/backported layouts and lets you switch between **Abstract** and **Satellite** map artwork when both styles are available.

## Features

- Adds DynamicMaps support for **Factory Classic**
- Adds support for **Icebreaker**
- Adds support for **Manimal Interchange**
- Adds support for **Manimal Labs**
- **Abstract / Satellite** style selector in F12
- Per-map style overrides
- Style can be changed **during a raid**
- If a floor does not have the selected style, it automatically uses the artwork that exists
- Keeps DynamicMaps player tracking, quest markers, extracts, floors, minimap and other normal features unchanged
- Does not patch or replace Fika player synchronization

## Requirements

- [DynamicMaps](https://github.com/acidphantasm/SPT-DynamicMaps)
- [MapVariants](https://github.com/LennoxP90/SPT-MapVariants)

The related map/backport mods are required only for the maps you want to use.

## Install

Copy:

```text
DynamicMaps-Extended
```

into:

```text
BepInEx/plugins/
```

To uninstall, delete the **DynamicMaps-Extended** folder.

## Build from source

Run:

```text
build.ps1
```

The builder will ask for your SPT folder and create the release ZIP.

See **THIRD-PARTY.md** for map artwork credits and licenses.
