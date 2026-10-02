using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;

namespace DynamicMapsExtended
{
    internal enum MapArtStyle
    {
        Abstract,
        Satellite
    }

    internal enum MapArtStyleOverride
    {
        UseGlobal,
        Abstract,
        Satellite
    }

    internal enum MapStyleKey
    {
        GroundZero,
        Customs,
        Factory,
        FactoryClassic,
        Woods,
        Interchange,
        Labs,
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
        internal readonly MapArtStyle Requested;
        internal readonly MapArtStyle Source;
        internal readonly MapArtStyle Selected;
        internal readonly string OriginalPath;
        internal readonly string ResolvedPath;
        internal readonly bool UsedFallback;
        internal readonly bool HasRequestedAsset;

        internal StyleResolution(
            MapStyleKey? map,
            MapArtStyle requested,
            MapArtStyle source,
            MapArtStyle selected,
            string originalPath,
            string resolvedPath,
            bool usedFallback,
            bool hasRequestedAsset)
        {
            Map = map;
            Requested = requested;
            Source = source;
            Selected = selected;
            OriginalPath = originalPath;
            ResolvedPath = resolvedPath;
            UsedFallback = usedFallback;
            HasRequestedAsset = hasRequestedAsset;
        }

        public override string ToString()
            => $"map={(Map?.ToString() ?? "Unknown")}, requested={Requested}, source={Source}, selected={Selected}, fallback={UsedFallback}, asset={ResolvedPath}";
    }

    internal static class MapStyleManager
    {
        internal static ConfigEntry<MapArtStyle> PreferredStyle { get; private set; }

        private static readonly Dictionary<MapStyleKey, ConfigEntry<MapArtStyleOverride>> Overrides = new Dictionary<MapStyleKey, ConfigEntry<MapArtStyleOverride>>();
        private static readonly HashSet<string> LoggedDecisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, string> SatelliteOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["maps/groundzero_tarkovdev/layers/groundzero-underground_level.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Garage.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-ground_level.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Ground.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-second_floor.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Second.png",
                ["maps/groundzero_tarkovdev/layers/groundzero-third_floor.svg"] = "Maps/StyleAssets/GroundZero/Satellite/GroundZero-Third.png",
                ["maps/customs_tarkovdev/layers/customs-underground_level.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Underground.png",
                ["maps/customs_tarkovdev/layers/customs-ground_level.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Ground.png",
                ["maps/customs_tarkovdev/layers/customs-second_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Second.png",
                ["maps/customs_tarkovdev/layers/customs-third_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Third.png",
                ["maps/customs_tarkovdev/layers/customs-fourth_floor.svg"] = "Maps/StyleAssets/Customs/Satellite/Customs-Fourth.png",
                ["maps/factory_tarkovdev/layers/factory-basement.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Tunnels.png",
                ["maps/factory_tarkovdev/layers/factory-ground_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Ground.png",
                ["maps/factory_tarkovdev/layers/factory-second_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Second.png",
                ["maps/factory_tarkovdev/layers/factory-third_floor.svg"] = "Maps/StyleAssets/Factory/Satellite/Factory-Third.png",
                ["maps/woods_tarkovdata/layers/woods-ground_level.svg"] = "Maps/StyleAssets/Woods/Satellite/Woods-Ground.png",
                ["maps/reserve_tarkovdata/layers/reserve-ground_level.svg"] = "Maps/StyleAssets/Reserve/Satellite/Reserve-Ground.png",
                ["maps/reserve_tarkovdata/layers/reserve-bunkers.svg"] = "Maps/StyleAssets/Reserve/Satellite/Reserve-Bunkers.png",
                ["maps/shoreline_tarkovdata/layers/shoreline-ground_level.svg"] = "Maps/StyleAssets/Shoreline/Satellite/Shoreline-Ground.png",
                ["maps/interchange_backport/layers/interchange-ground_level.svg"] = "Maps/StyleAssets/Interchange_Backport/Satellite/Interchange-Ground.png"
            };

        internal static void Bind(ConfigFile config)
        {
            PreferredStyle = config.Bind(
                "Map Style",
                "Preferred Style",
                MapArtStyle.Abstract,
                "Global preferred artwork style. Abstract keeps schematic/vector-style artwork; Satellite uses detailed top-down raster artwork where a verified matching asset exists.");

            BindOverride(config, MapStyleKey.GroundZero, "Ground Zero");
            BindOverride(config, MapStyleKey.Customs, "Customs");
            BindOverride(config, MapStyleKey.Factory, "Factory");
            BindOverride(config, MapStyleKey.FactoryClassic, "Factory Classic");
            BindOverride(config, MapStyleKey.Woods, "Woods");
            BindOverride(config, MapStyleKey.Interchange, "Interchange");
            BindOverride(config, MapStyleKey.Labs, "Labs");
            BindOverride(config, MapStyleKey.Lighthouse, "Lighthouse");
            BindOverride(config, MapStyleKey.Icebreaker, "Icebreaker");
            BindOverride(config, MapStyleKey.Reserve, "Reserve");
            BindOverride(config, MapStyleKey.Shoreline, "Shoreline");
            BindOverride(config, MapStyleKey.Streets, "Streets");
            BindOverride(config, MapStyleKey.Labyrinth, "Labyrinth");

            PreferredStyle.SettingChanged += (_, __) => OnStyleSettingChanged("global preferred style");
            foreach (var pair in Overrides)
                pair.Value.SettingChanged += (_, __) => OnStyleSettingChanged($"override {pair.Key}");
        }

        private static void BindOverride(ConfigFile config, MapStyleKey key, string displayName)
        {
            Overrides[key] = config.Bind(
                "Map Style Overrides",
                displayName,
                MapArtStyleOverride.UseGlobal,
                $"Per-map style override for {displayName}. UseGlobal follows Map Style -> Preferred Style.");
        }

        private static void OnStyleSettingChanged(string reason)
        {
            LoggedDecisions.Clear();
            Plugin.Debug($"Map style setting changed: {reason}. Applying to currently loaded DynamicMaps layers immediately when possible.");
            StyleRefreshManager.RefreshTrackedLayers("style setting changed: " + reason);
        }

        internal static StyleResolution Resolve(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return new StyleResolution(null, MapArtStyle.Abstract, MapArtStyle.Abstract, MapArtStyle.Abstract, imagePath, imagePath, false, false);

            var normalized = Normalize(imagePath);
            var map = DetectMap(normalized);
            var source = DetectSourceStyle(normalized, map);
            var requested = map.HasValue ? RequestedFor(map.Value) : PreferredStyle?.Value ?? MapArtStyle.Abstract;

            if (requested == source)
            {
                var native = new StyleResolution(map, requested, source, source, imagePath, imagePath, false, true);
                LogResolution(native);
                return native;
            }

            if (requested == MapArtStyle.Satellite)
            {
                var replacement = FindReplacement(normalized, SatelliteOverrides);
                if (!string.IsNullOrEmpty(replacement))
                {
                    var absolute = Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, replacement.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(absolute))
                    {
                        var selected = new StyleResolution(map, requested, source, MapArtStyle.Satellite, imagePath, absolute, false, true);
                        LogResolution(selected);
                        return selected;
                    }

                    Plugin.Debug($"Configured satellite asset is missing on disk: {absolute}");
                }
            }

            var fallback = requested != source;
            if (fallback)
                Plugin.Debug($"Layer style fallback: map={(map?.ToString() ?? "Unknown")}, requested={requested}, using existing {source}, layer={imagePath}");

            var safe = new StyleResolution(map, requested, source, source, imagePath, imagePath, fallback, false);
            LogResolution(safe);
            return safe;
        }

        private static void LogResolution(StyleResolution resolution)
        {
            if (!Plugin.DebugEnabled) return;

            var key = $"{resolution.OriginalPath}|{resolution.Requested}|{resolution.Selected}|{resolution.HasRequestedAsset}";
            if (LoggedDecisions.Add(key))
                Plugin.Debug($"Style decision: {resolution}");
        }

        internal static string DescribeSettings()
        {
            var global = PreferredStyle?.Value.ToString() ?? "<unbound>";
            return $"global={global}, per-layer-fallback=Always";
        }

        internal static MapArtStyle RequestedFor(MapStyleKey map)
        {
            if (Overrides.TryGetValue(map, out var entry))
            {
                switch (entry.Value)
                {
                    case MapArtStyleOverride.Abstract:
                        return MapArtStyle.Abstract;
                    case MapArtStyleOverride.Satellite:
                        return MapArtStyle.Satellite;
                }
            }

            return PreferredStyle?.Value ?? MapArtStyle.Abstract;
        }

        internal static string DescribeOverrides()
        {
            var parts = new List<string>();
            foreach (MapStyleKey key in Enum.GetValues(typeof(MapStyleKey)))
            {
                if (!Overrides.TryGetValue(key, out var entry))
                    continue;

                if (entry.Value != MapArtStyleOverride.UseGlobal)
                    parts.Add($"{key}={entry.Value}");
            }

            return parts.Count == 0 ? "none (all UseGlobal)" : string.Join(", ", parts);
        }

        internal static string DescribeResolution(string imagePath)
        {
            var r = Resolve(imagePath);
            return r.ToString();
        }

        private static string FindReplacement(string normalizedSource, Dictionary<string, string> mappings)
        {
            foreach (var pair in mappings)
            {
                if (normalizedSource.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }
            return null;
        }

        private static MapStyleKey? DetectMap(string p)
        {
            if (p.Contains("/groundzero_tarkovdev/") || p.Contains("/styleassets/groundzero/")) return MapStyleKey.GroundZero;
            if (p.Contains("/customs_tarkovdev/") || p.Contains("/styleassets/customs/")) return MapStyleKey.Customs;
            if (p.Contains("/factory_classic/")) return MapStyleKey.FactoryClassic;
            if (p.Contains("/factory_tarkovdev/") || p.Contains("/styleassets/factory/")) return MapStyleKey.Factory;
            if (p.Contains("/woods_tarkovdata/") || p.Contains("/styleassets/woods/")) return MapStyleKey.Woods;
            if (p.Contains("/interchange_backport/") || p.Contains("/interchange_tarkovdev/") || p.Contains("/styleassets/interchange_backport/")) return MapStyleKey.Interchange;
            if (p.Contains("/labs_backport/") || p.Contains("/labs_tarkovdev/")) return MapStyleKey.Labs;
            if (p.Contains("/lighthouse_tarkovdata/")) return MapStyleKey.Lighthouse;
            if (p.Contains("/icebreaker/")) return MapStyleKey.Icebreaker;
            if (p.Contains("/reserve_tarkovdata/") || p.Contains("/styleassets/reserve/")) return MapStyleKey.Reserve;
            if (p.Contains("/shoreline_tarkovdata/") || p.Contains("/styleassets/shoreline/")) return MapStyleKey.Shoreline;
            if (p.Contains("/streets_tarkovdata/")) return MapStyleKey.Streets;
            if (p.Contains("/labyrinth/")) return MapStyleKey.Labyrinth;
            return null;
        }

        private static MapArtStyle DetectSourceStyle(string p, MapStyleKey? map)
        {
            if (map == MapStyleKey.FactoryClassic)
                return MapArtStyle.Abstract;

            if (p.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                return MapArtStyle.Satellite;

            return MapArtStyle.Abstract;
        }

        private static string Normalize(string path)
            => path.Replace('\', '/').Trim().ToLowerInvariant();
    }
}
