using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace DynamicMapsExtended
{
    internal enum MapArtMode
    {
        Recommended,
        Original,
        Satellite,
        Abstract
    }

    internal enum MapStyleKey
    {
        GroundZero,
        Customs,
        Factory,
        FactoryClassic,
        Woods,
        Interchange,
        InterchangeManimal,
        Labs,
        LabsManimal,
        Lighthouse,
        Icebreaker,
        Reserve,
        Shoreline,
        Streets,
        Labyrinth
    }

    internal readonly struct StyleResolution
    {
        internal readonly MapStyleKey? Map;
        internal readonly MapArtMode Requested;
        internal readonly MapArtMode Selected;
        internal readonly string OriginalPath;
        internal readonly string ResolvedPath;
        internal readonly bool UsedFallback;
        internal readonly bool HasRequestedAsset;

        internal StyleResolution(
            MapStyleKey? map,
            MapArtMode requested,
            MapArtMode selected,
            string originalPath,
            string resolvedPath,
            bool usedFallback,
            bool hasRequestedAsset)
        {
            Map = map;
            Requested = requested;
            Selected = selected;
            OriginalPath = originalPath;
            ResolvedPath = resolvedPath;
            UsedFallback = usedFallback;
            HasRequestedAsset = hasRequestedAsset;
        }

        public override string ToString()
            => $"map={(Map?.ToString() ?? "Unknown")}, requested={Requested}, selected={Selected}, fallback={UsedFallback}, asset={ResolvedPath}";
    }

    internal static class MapStyleManager
    {
        private const string GlobalRecommended = "Recommended";
        private const string GlobalOriginal = "DynamicMaps Vanilla";
        private const string GlobalSatellite = "Satellite";
        private const string GlobalAbstract = "Abstract";
        private const string LegacyOriginal = "Original DynamicMaps";

        private const string OverrideUseGlobal = "Use Global";
        private const string OverrideOriginal = "DynamicMaps Vanilla";
        private const string OverrideOriginalOnly = "DynamicMaps Vanilla (Only Available)";
        private const string OverrideFactoryClassicOnly = "Factory Classic Artwork (Only Available)";
        private const string OverrideSatellite = "Satellite";
        private const string OverrideAbstract = "Abstract";

        internal static ConfigEntry<string> ArtworkMode { get; private set; }

        private static readonly Dictionary<MapStyleKey, ConfigEntry<string>> Overrides =
            new Dictionary<MapStyleKey, ConfigEntry<string>>();

        private static readonly HashSet<string> LoggedDecisions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, StyleResolution> ResolutionCache =
            new Dictionary<string, StyleResolution>(StringComparer.OrdinalIgnoreCase);


        // Extension alias source-layer -> DynamicMaps' own native SVG artwork. This is intentionally
        // separate from Abstract: Manimal Interchange can show the original DynamicMaps map while
        // still keeping its newer tarkov.dev Abstract/Satellite choices.
        private static readonly Dictionary<string, string> VanillaOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["maps/interchange_backport/abstract/interchange-ground.svg"] = "Maps/Interchange_TarkovDev/Layers/Interchange-Ground_Level.svg",
                ["maps/interchange_backport/abstract/interchange-first.svg"] = "Maps/Interchange_TarkovDev/Layers/Interchange-First_Floor.svg",
                ["maps/interchange_backport/abstract/interchange-second.svg"] = "Maps/Interchange_TarkovDev/Layers/Interchange-Second_Floor.svg"
            };

        // Native/extension source-layer -> bundled Satellite replacement.
        // Resolve() verifies every file before selecting it.
        private static readonly Dictionary<string, string> SatelliteOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Ground Zero
                ["maps/groundzero_tarkovdev/layers/groundzero-underground_level.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Garage.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-ground_level.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Ground.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-second_floor.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Second.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-third_floor.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Third.png",

                // Customs
                ["maps/customs_tarkovdev/layers/customs-underground_level.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Underground.png",
                ["maps/customs_tarkovdev/layers/customs-ground_level.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Ground.png",
                ["maps/customs_tarkovdev/layers/customs-second_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Second.png",
                ["maps/customs_tarkovdev/layers/customs-third_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Third.png",
                ["maps/customs_tarkovdev/layers/customs-fourth_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Fourth.png",

                // Factory
                ["maps/factory_tarkovdev/layers/factory-basement.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Tunnels.png",
                ["maps/factory_tarkovdev/layers/factory-ground_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Ground.png",
                ["maps/factory_tarkovdev/layers/factory-second_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Second.png",
                ["maps/factory_tarkovdev/layers/factory-third_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Third.png",

                // Woods
                ["maps/woods_tarkovdata/layers/woods-ground_level.svg"] = "Maps/StyleAssets/Woods/Satellite/Woods-Ground.png",

                // Interchange Satellite exists only for Ground.
                // Normal Interchange may use it as its Satellite choice; missing upper floors
                // fall back to DynamicMaps Vanilla. Manimal uses Satellite Ground with Abstract
                // fallback on upper floors.
                ["maps/interchange_tarkovdev/layers/interchange-ground_level.svg"] = "Maps/StyleAssets/Interchange_Backport/Satellite/Interchange-Ground.png",
                ["maps/interchange_backport/abstract/interchange-ground.svg"] = "Maps/StyleAssets/Interchange_Backport/Satellite/Interchange-Ground.png",

                // Labs: current Tarkov.dev Labs v4 is raster/tile artwork.
                ["maps/labs_tarkovdev/layers/labs_technical_level.svg"] = "Maps/Labs_Backport/Layers/Labs-Technical.png",
                ["maps/labs_tarkovdev/layers/labs_first_level.svg"] = "Maps/Labs_Backport/Layers/Labs-First.png",
                ["maps/labs_tarkovdev/layers/labs_second_level.svg"] = "Maps/Labs_Backport/Layers/Labs-Second.png",

                // Reserve uses its dedicated correctly-bounded Satellite MapDef; no native-layer
                // substitutions are made here.

                // Shoreline: current Satellite exists only for Ground.
                ["maps/shoreline_tarkovdata/layers/shoreline-ground_level.svg"] = "Maps/StyleAssets/Shoreline/Satellite/Shoreline-Ground.png",

                // Labyrinth: current Tarkov.dev is raster-only.
                ["maps/labyrinth/layers/labyrinth.svg"] = "Maps/StyleAssets/Labyrinth/Satellite/Labyrinth-Main.png"
            };

        // Native/extension source-layer -> bundled current Tarkov.dev Abstract SVG.
        // Only artwork that is meaningfully different from DynamicMaps Vanilla is exposed.
        // Duplicate Abstract copies for Ground Zero, Customs, Lighthouse, Reserve and Shoreline
        // are deliberately not mapped or shipped in release builds.
        private static readonly Dictionary<string, string> AbstractOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Factory
                ["maps/factory_tarkovdev/layers/factory-basement.svg"] = "Maps/StyleAssets/Factory/Abstract/Factory-Tunnels.svg",
                ["maps/factory_tarkovdev/layers/factory-ground_floor.svg"] = "Maps/StyleAssets/Factory/Abstract/Factory-Ground.svg",
                ["maps/factory_tarkovdev/layers/factory-second_floor.svg"] = "Maps/StyleAssets/Factory/Abstract/Factory-Second.svg",
                ["maps/factory_tarkovdev/layers/factory-third_floor.svg"] = "Maps/StyleAssets/Factory/Abstract/Factory-Third.svg",

                // Woods
                ["maps/woods_tarkovdata/layers/woods-ground_level.svg"] = "Maps/StyleAssets/Woods/Abstract/Woods-Ground.svg",

                // Normal Interchange intentionally has no Abstract override.
                // Manimal Interchange keeps the newer/current Tarkov.dev Abstract artwork.
                // Unity.VectorGraphics does not render the current Manimal/Tarkov.dev source SVGs
                // reliably in-game. These are offline rasterizations of the verified vector artwork,
                // with sibling high-resolution .tiles packs. This makes Abstract deterministic while
                // retaining the correct current Manimal world bounds.
                ["maps/interchange_backport/abstract/interchange-ground.svg"] = "Maps/Interchange_Backport/AbstractRaster/Interchange-Ground.png",
                ["maps/interchange_backport/abstract/interchange-first.svg"] = "Maps/Interchange_Backport/AbstractRaster/Interchange-First.png",
                ["maps/interchange_backport/abstract/interchange-second.svg"] = "Maps/Interchange_Backport/AbstractRaster/Interchange-Second.png",

                // Streets keeps its meaningful current Tarkov.dev Abstract choice.
                ["maps/streets_tarkovdata/layers/streetsoftarkov-ground_level.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Ground.svg",
                ["maps/streets_tarkovdata/layers/streetsoftarkov-second_floor.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Second.svg",
                ["maps/streets_tarkovdata/layers/streetsoftarkov-third_floor.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Third.svg",
                ["maps/streets_tarkovdata/layers/streetsoftarkov-fourth_floor.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Fourth.svg",
                ["maps/streets_tarkovdata/layers/streetsoftarkov-fifth_floor.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Fifth.svg",
                ["maps/streets_tarkovdata/layers/streetsoftarkov-underground_level.svg"] = "Maps/StyleAssets/Streets/Abstract/Streets-Underground.svg"
            };

        internal static void Bind(ConfigFile config)
        {
            ArtworkMode = config.Bind(
                "Map Artwork",
                "Artwork Mode",
                GlobalRecommended,
                new ConfigDescription(
                    "Recommended prefers verified Satellite artwork, then real Tarkov.dev Abstract artwork, then DynamicMaps Vanilla. " +
                    "Satellite falls back per floor to Abstract/Vanilla. Abstract falls back per floor to Vanilla.",
                    new AcceptableValueList<string>(GlobalRecommended, GlobalOriginal, GlobalSatellite, GlobalAbstract)));

            BindOverrideIfUseful(config, MapStyleKey.GroundZero, "Ground Zero");
            BindOverrideIfUseful(config, MapStyleKey.Customs, "Customs");
            BindOverrideIfUseful(config, MapStyleKey.Factory, "Factory");
            if (PluginDetection.Loaded(Plugin.FactoryClassicGuid) && FactoryClassicAssets.Available)
                BindOverrideIfUseful(config, MapStyleKey.FactoryClassic, "Factory Classic");
            BindOverrideIfUseful(config, MapStyleKey.Woods, "Woods");
            BindOverrideIfUseful(config, MapStyleKey.Interchange, "Interchange");

            if (PluginDetection.Loaded(Plugin.ManimalInterchangeGuid))
                BindOverrideIfUseful(config, MapStyleKey.InterchangeManimal, "Interchange (Manimal)");

            // Always expose The Lab when there is a real Vanilla/Satellite choice. If the current
            // Manimal Labs replacement is installed, MapAliasPatch uses this same setting to decide
            // whether to show DynamicMaps Vanilla or the bundled current Tarkov.dev Satellite map.
            BindOverrideIfUseful(config, MapStyleKey.Labs, "The Lab");

            BindOverrideIfUseful(config, MapStyleKey.Reserve, "Reserve");
            BindOverrideIfUseful(config, MapStyleKey.Shoreline, "Shoreline");
            BindOverrideIfUseful(config, MapStyleKey.Streets, "Streets");
            BindOverrideIfUseful(config, MapStyleKey.Lighthouse, "Lighthouse");
            BindOverrideIfUseful(config, MapStyleKey.Labyrinth, "Labyrinth");

            ArtworkMode.SettingChanged += (_, __) => OnStyleSettingChanged("global artwork mode");
            foreach (var pair in Overrides)
                pair.Value.SettingChanged += (_, __) => OnStyleSettingChanged($"override {pair.Key}");
        }

        private static void BindOverrideIfUseful(ConfigFile config, MapStyleKey key, string displayName)
        {
            var realChoices = new List<string>();
            if (HasVanillaChoice(key)) realChoices.Add(OverrideOriginal);
            if (HasBundledSatelliteChoice(key)) realChoices.Add(OverrideSatellite);
            if (HasBundledAbstractChoice(key)) realChoices.Add(OverrideAbstract);

            if (realChoices.Count == 0)
            {
                Plugin.Debug($"F12 override hidden for {displayName}: no verified artwork source is available.");
                return;
            }

            // Keep single-artwork maps visible so the player can see that the map is supported,
            // while making it explicit that there is nothing meaningful to switch to. A fixed
            // row also prevents the global style selector from pretending a duplicate style exists.
            string defaultValue;
            string[] values;
            if (realChoices.Count == 1)
            {
                defaultValue = OnlyAvailableLabel(key, realChoices[0]);
                values = new[] { defaultValue };
            }
            else
            {
                defaultValue = OverrideUseGlobal;
                values = new[] { OverrideUseGlobal }.Concat(realChoices).ToArray();
            }

            var entry = config.Bind(
                "Map Artwork Overrides",
                displayName,
                defaultValue,
                new ConfigDescription(
                    realChoices.Count == 1
                        ? $"{displayName} has only one meaningful artwork source in this build."
                        : $"Artwork override for {displayName}. Only verified/available artwork types are listed.",
                    new AcceptableValueList<string>(values)));

            // Old test builds may have saved values that no longer exist (for example Abstract on
            // Ground Zero). Normalize them immediately instead of leaving a misleading stale value.
            if (!values.Any(v => string.Equals(v, entry.Value, StringComparison.OrdinalIgnoreCase)))
                entry.Value = defaultValue;

            Overrides[key] = entry;
        }

        private static string OnlyAvailableLabel(MapStyleKey key, string choice)
        {
            if (key == MapStyleKey.FactoryClassic)
                return OverrideFactoryClassicOnly;
            if (string.Equals(choice, OverrideOriginal, StringComparison.OrdinalIgnoreCase))
                return OverrideOriginalOnly;
            return choice + " (Only Available)";
        }

        private static bool HasVanillaChoice(MapStyleKey key)
        {
            switch (key)
            {
                case MapStyleKey.LabsManimal:
                case MapStyleKey.Icebreaker:
                    return false;
                default:
                    return true;
            }
        }

        private static bool HasBundledSatelliteChoice(MapStyleKey key)
        {
            // v2.0.0: first-run raster assets are prepared after the plugin loads. F12 must
            // still expose the final supported artwork matrix before those files exist locally.
            // Resolve() continues to require the actual file before selecting it, so a player
            // who opens a map while preparation is still running safely falls back to Vanilla/Abstract.
            if (AssetPreparationManager.KnownSatelliteChoice(key))
                return true;

            return false;
        }

        private static bool HasBundledAbstractChoice(MapStyleKey key)
        {
            foreach (var pair in AbstractOverrides)
            {
                if (MapForMappingKey(pair.Key) != key)
                    continue;

                if (File.Exists(ExtensionPath(pair.Value)))
                    return true;
            }

            return false;
        }

        private static MapStyleKey? MapForMappingKey(string normalizedSource)
            => DetectMap(normalizedSource, false);

        private static void OnStyleSettingChanged(string reason)
        {
            LoggedDecisions.Clear();
            ResolutionCache.Clear();

            Plugin.Debug($"Map artwork changed: {reason}. Current floor updates first; other floors stay on demand.");
            RasterWarmupManager.OnStyleChanged("artwork setting changed: " + reason);
            TilePackManager.OnStyleChanged();
            RaidWarmCacheManager.OnStyleChanged();
            StyleRefreshManager.RefreshTrackedLayers("artwork setting changed: " + reason);
            MapDefinitionRefreshManager.OnArtworkSettingChanged();
        }

        internal static void OnAssetsPrepared()
        {
            LoggedDecisions.Clear();
            ResolutionCache.Clear();

            Plugin.Debug("Raster asset preparation reached a terminal state; refreshing artwork availability once.");
            RasterWarmupManager.OnStyleChanged("one-time raster assets prepared");
            TilePackManager.OnStyleChanged();
            RaidWarmCacheManager.OnStyleChanged();
            StyleRefreshManager.RefreshTrackedLayers("one-time raster assets prepared");
            MapDefinitionRefreshManager.OnArtworkSettingChanged();
            ExtensionMapLoaderPatch.RefreshAfterAssetsPrepared();
        }

        internal static StyleResolution Resolve(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return new StyleResolution(null, MapArtMode.Recommended, MapArtMode.Original, imagePath, imagePath, false, false);

            var normalized = Normalize(imagePath);
            var map = DetectMap(normalized, Path.IsPathRooted(imagePath));
            var requested = map.HasValue ? RequestedFor(map.Value) : GlobalMode();
            var cacheKey = normalized + "|" + requested;

            if (ResolutionCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var sourceIsBundledRaster = RasterSpritePatch.IsExtensionOwnedRaster(imagePath);
            var vanillaReplacement = FindExistingVanillaReplacement(normalized);
            var satelliteReplacement = FindExistingReplacement(normalized, SatelliteOverrides, "Satellite");
            var abstractReplacement = FindExistingReplacement(normalized, AbstractOverrides, "Abstract");

            StyleResolution result;

            if (requested == MapArtMode.Original)
            {
                if (!string.IsNullOrEmpty(vanillaReplacement))
                {
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, vanillaReplacement, false, true);
                }
                else if (map.HasValue && HasVanillaChoice(map.Value))
                {
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, imagePath, false, true);
                }
                else if (!string.IsNullOrEmpty(abstractReplacement))
                {
                    result = new StyleResolution(map, requested, MapArtMode.Abstract, imagePath, abstractReplacement, true, false);
                }
                else if (sourceIsBundledRaster)
                {
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, imagePath, true, false);
                }
                else
                {
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, imagePath, true, false);
                }
            }
            else if (requested == MapArtMode.Satellite)
            {
                if (!string.IsNullOrEmpty(satelliteReplacement))
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, satelliteReplacement, false, true);
                else if (sourceIsBundledRaster)
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, imagePath, false, true);
                else if (!string.IsNullOrEmpty(abstractReplacement))
                    result = new StyleResolution(map, requested, MapArtMode.Abstract, imagePath, abstractReplacement, true, false);
                else
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, imagePath, true, false);
            }
            else if (requested == MapArtMode.Abstract)
            {
                if (!string.IsNullOrEmpty(abstractReplacement))
                    result = new StyleResolution(map, requested, MapArtMode.Abstract, imagePath, abstractReplacement, false, true);
                else if (!sourceIsBundledRaster)
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, imagePath, true, false);
                else
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, imagePath, true, false);
            }
            else // Recommended
            {
                if (!string.IsNullOrEmpty(satelliteReplacement))
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, satelliteReplacement, false, true);
                else if (sourceIsBundledRaster)
                    result = new StyleResolution(map, requested, MapArtMode.Satellite, imagePath, imagePath, false, true);
                else if (!string.IsNullOrEmpty(abstractReplacement))
                    result = new StyleResolution(map, requested, MapArtMode.Abstract, imagePath, abstractReplacement, false, true);
                else
                    result = new StyleResolution(map, requested, MapArtMode.Original, imagePath, imagePath, false, true);
            }

            ResolutionCache[cacheKey] = result;
            LogResolution(result);
            return result;
        }

        private static string FindExistingVanillaReplacement(string normalizedSource)
        {
            foreach (var pair in VanillaOverrides)
            {
                if (!normalizedSource.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var absolute = Path.GetFullPath(Path.Combine(
                    DynamicMapsBridge.PluginPath,
                    pair.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(absolute))
                    return absolute;

                Plugin.Debug($"Configured DynamicMaps Vanilla asset is missing on disk: {absolute}");
                return null;
            }

            return null;
        }

        private static string FindExistingReplacement(
            string normalizedSource,
            Dictionary<string, string> replacements,
            string label)
        {
            foreach (var pair in replacements)
            {
                if (!normalizedSource.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                var absolute = ExtensionPath(pair.Value);
                if (File.Exists(absolute))
                    return absolute;

                Plugin.Debug($"Configured {label} asset is missing on disk: {absolute}");
                return null;
            }

            return null;
        }

        private static void LogResolution(StyleResolution resolution)
        {
            if (!Plugin.DebugEnabled) return;

            var key = $"{resolution.OriginalPath}|{resolution.Requested}|{resolution.Selected}|{resolution.HasRequestedAsset}";
            if (LoggedDecisions.Add(key))
                Plugin.Debug($"Artwork decision: {resolution}");
        }

        internal static string DescribeSettings()
            => $"global={ArtworkMode?.Value ?? "<unbound>"}, per-map choices=verified meaningful artwork only";

        internal static bool WantsSatelliteMapDef(MapStyleKey map)
            => WantsSatelliteArtwork(map);

        internal static bool WantsSatelliteArtwork(MapStyleKey map)
        {
            var requested = RequestedFor(map);
            if (requested == MapArtMode.Satellite)
                return HasBundledSatelliteChoice(map);
            if (requested == MapArtMode.Recommended)
                return HasBundledSatelliteChoice(map);
            return false;
        }

        internal static MapArtMode RequestedFor(MapStyleKey map)
        {
            if (Overrides.TryGetValue(map, out var entry))
            {
                if (string.Equals(entry.Value, OverrideOriginal, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value, OverrideOriginalOnly, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value, OverrideFactoryClassicOnly, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value, LegacyOriginal, StringComparison.OrdinalIgnoreCase))
                    return MapArtMode.Original;
                if (string.Equals(entry.Value, OverrideSatellite, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value, OverrideSatellite + " (Only Available)", StringComparison.OrdinalIgnoreCase))
                    return MapArtMode.Satellite;
                if (string.Equals(entry.Value, OverrideAbstract, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Value, OverrideAbstract + " (Only Available)", StringComparison.OrdinalIgnoreCase))
                    return MapArtMode.Abstract;
            }

            return GlobalMode();
        }

        private static MapArtMode GlobalMode()
        {
            var value = ArtworkMode?.Value;
            if (string.Equals(value, GlobalOriginal, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, LegacyOriginal, StringComparison.OrdinalIgnoreCase))
                return MapArtMode.Original;
            if (string.Equals(value, GlobalSatellite, StringComparison.OrdinalIgnoreCase))
                return MapArtMode.Satellite;
            if (string.Equals(value, GlobalAbstract, StringComparison.OrdinalIgnoreCase))
                return MapArtMode.Abstract;
            return MapArtMode.Recommended;
        }

        internal static string DescribeOverrides()
        {
            var parts = new List<string>();
            foreach (var pair in Overrides)
            {
                if (!string.Equals(pair.Value.Value, OverrideUseGlobal, StringComparison.OrdinalIgnoreCase))
                    parts.Add($"{pair.Key}={pair.Value.Value}");
            }

            return parts.Count == 0 ? "none (all Use Global)" : string.Join(", ", parts);
        }

        internal static string DescribeResolution(string imagePath)
            => Resolve(imagePath).ToString();

        private static string ExtensionPath(string relative)
            => Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

        private static MapStyleKey? DetectMap(string p, bool rooted)
        {
            if (p.Contains("/groundzero_tarkovdev/") || p.Contains("/styleassets/groundzero/")) return MapStyleKey.GroundZero;
            if (p.Contains("/customs_tarkovdev/") || p.Contains("/styleassets/customs/")) return MapStyleKey.Customs;
            if (p.Contains("/factory_classic/")) return MapStyleKey.FactoryClassic;
            if (p.Contains("/factory_tarkovdev/") || p.Contains("/styleassets/factory/")) return MapStyleKey.Factory;
            if (p.Contains("/woods_tarkovdata/") || p.Contains("/styleassets/woods/")) return MapStyleKey.Woods;

            if (p.Contains("/interchange_backport/") || p.Contains("/styleassets/interchange_backport/"))
                return MapStyleKey.InterchangeManimal;

            if (p.Contains("/interchange_tarkovdev/") || p.Contains("/styleassets/interchange/"))
                return MapStyleKey.Interchange;

            if (p.Contains("/labs_backport/")) return MapStyleKey.LabsManimal;
            if (p.Contains("/labs_tarkovdev/")) return MapStyleKey.Labs;
            if (p.Contains("/lighthouse_tarkovdata/") || p.Contains("/styleassets/lighthouse/")) return MapStyleKey.Lighthouse;
            if (p.Contains("/icebreaker/")) return MapStyleKey.Icebreaker;
            if (p.Contains("/reserve_tarkovdata/") || p.Contains("/reserve_satellite/") || p.Contains("/styleassets/reserve/")) return MapStyleKey.Reserve;
            if (p.Contains("/shoreline_tarkovdata/") || p.Contains("/styleassets/shoreline/")) return MapStyleKey.Shoreline;
            if (p.Contains("/streets_tarkovdata/") || p.Contains("/styleassets/streets/")) return MapStyleKey.Streets;
            if (p.Contains("/labyrinth/") || p.Contains("/labyrinth_fallback/") || p.Contains("/styleassets/labyrinth/")) return MapStyleKey.Labyrinth;
            return null;
        }

        private static string Normalize(string path)
            => path.Replace('\\', '/').Trim().ToLowerInvariant();
    }
}