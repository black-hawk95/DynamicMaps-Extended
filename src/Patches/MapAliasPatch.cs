using System;
using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;

namespace DynamicMapsExtended
{
    // Intentionally patch ONLY the dropdown filter. We do not alter
    // GameUtils.GetCurrentMapInternalName(), so DynamicMaps' map-item checks,
    // dump filenames, live player tracking, quest markers and every other
    // subsystem keep the real EFT location ID.
    [HarmonyPatch]
    internal static class MapAliasPatch
    {
        internal const string InterchangeAlias = "dmext_interchange_backport";
        internal const string LabsAlias = "dmext_laboratory_backport";
        internal const string FactoryClassicAlias = "dmext_factory_classic";
        internal const string ReserveSatelliteAlias = "dmext_reserve_satellite";

        private static readonly HashSet<string> LoggedAliasDecisions = new HashSet<string>(StringComparer.Ordinal);

        private static void DebugOnce(string key, string message)
        {
            if (!Plugin.DebugEnabled) return;
            if (LoggedAliasDecisions.Add(key))
                Plugin.Debug(message);
        }

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.MapSelectDropdown")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.MapSelectDropdown not found");
            return AccessTools.Method(type, "FilterByInternalMapName", new[] { typeof(string) })
                ?? throw new MissingMethodException(type.FullName, "FilterByInternalMapName");
        }

        private static void Prefix(object __instance, ref string internalMapName)
        {
            if (string.IsNullOrEmpty(internalMapName)) return;
            var requested = internalMapName;

            // FactoryClassic deliberately uses the same factory4_day/factory4_night IDs as vanilla.
            // The extension now ships a static Factory Classic MapDef/art set, so no runtime
            // NavMesh map generation is needed. Redirect only when FactoryClassic proves that
            // Classic is selected/loaded and the packaged map files are intact.
            if (FactoryClassicBridge.IsFactory(internalMapName))
            {
                var classicChosen = FactoryClassicBridge.IsClassicChosen;
                var classicLoaded = FactoryClassicBridge.ClassicSceneLoaded;
                var assetsReady = FactoryClassicAssets.Available;

                if ((classicLoaded || classicChosen) && assetsReady)
                    internalMapName = FactoryClassicAlias;
                else if ((classicLoaded || classicChosen) && !assetsReady)
                    Plugin.Log?.LogWarning($"Factory Classic is selected, but DynamicMaps Extended's packaged Factory Classic map is incomplete. {FactoryClassicAssets.Describe()}");

                DebugOnce($"factory|{requested}|{classicChosen}|{classicLoaded}|{assetsReady}|{internalMapName}", $"Alias decision: requested={requested}, factoryClassic chosen={classicChosen}, loaded={classicLoaded}, packagedAssetsReady={assetsReady}, result={internalMapName}");
                return;
            }

            var normalized = internalMapName.Trim().ToLowerInvariant();

            // Current Tarkov.dev Reserve Satellite has different authoritative map bounds from
            // DynamicMaps' older Reserve artwork. Select a dedicated alias MapDef instead of
            // stretching the new imagery over the old bounds. Original mode stays fully native.
            if (normalized == "rezervbase" || normalized == "reserve")
            {
                var wantsSatellite = MapStyleManager.WantsSatelliteMapDef(MapStyleKey.Reserve);
                var assetsReady = ExtensionAssetAvailability.ReserveSatelliteReady;
                if (wantsSatellite && assetsReady)
                    internalMapName = ReserveSatelliteAlias;
                else if (wantsSatellite && !assetsReady)
                    Plugin.Log?.LogWarning("Reserve Satellite was requested but its bundled map pack is incomplete; using the original DynamicMaps Reserve definition.");

                DebugOnce($"reserve|{requested}|{wantsSatellite}|{assetsReady}|{internalMapName}", $"Alias decision: requested={requested}, Reserve satelliteRequested={wantsSatellite}, assetsReady={assetsReady}, result={internalMapName}");
                return;
            }

            string alias = null;
            string pluginGuid = null;

            switch (normalized)
            {
                case "interchange":
                    alias = InterchangeAlias;
                    pluginGuid = Plugin.ManimalInterchangeGuid;
                    break;
                case "lighthouse":
                    // No verified Manimal-specific overhead artwork exists yet. Keep DynamicMaps'
                    // native Lighthouse MapDef rather than falsely labelling old artwork as a backport map.
                    // Live player/extract/quest markers still come from the actual raid.
                    DebugOnce($"lighthouse|{requested}", $"Alias decision: requested={requested}, Manimal Lighthouse uses native DynamicMaps artwork until a verified/generated map is available.");
                    return;
                case "laboratory":
                    alias = LabsAlias;
                    pluginGuid = Plugin.ManimalLabsGuid;
                    break;
                default:
                    // Icebreaker has its own real location ID, so no alias is required.
                    DebugOnce($"unchanged|{requested}", $"Alias decision: requested={requested}, not a shared-ID map; unchanged.");
                    return;
            }

            var mv = MapVariantsBridge.Resolve(internalMapName);
            if (mv.ApiAvailable)
            {
                var backportPresent = PluginDetection.Loaded(pluginGuid) && AliasAssetsReady(alias);

                if (mv.Managed)
                {
                    // MapVariants is authoritative for maps it manages. Never infer a backport
                    // merely because the backport plugin is installed.
                    if (mv.Decided && mv.IsBackport && backportPresent)
                        internalMapName = alias;
                    else if (mv.Decided && mv.IsBackport && !backportPresent)
                        Plugin.Log?.LogWarning($"MapVariants selected the backport for {requested}, but {pluginGuid} is not loaded. DynamicMaps will stay on the original map definition.");

                    DebugOnce($"managed|{requested}|{mv}|{pluginGuid}|{backportPresent}|{internalMapName}", $"Alias decision: requested={requested}, MapVariants({mv}), backportPlugin={pluginGuid}:{backportPresent}, result={internalMapName}");
                    return;
                }

                // Current Manimal Labs replaces the laboratory location rather than exposing a
                // MapVariants choice. Artwork selection is still user-controlled:
                // DynamicMaps Vanilla keeps the native Labs MapDef; Satellite/Recommended uses
                // the bundled current Tarkov.dev Labs v4 raster MapDef.
                var labsSatelliteRequested = normalized == "laboratory" &&
                                             MapStyleManager.WantsSatelliteArtwork(MapStyleKey.Labs);
                if (normalized == "laboratory" && backportPresent && labsSatelliteRequested)
                    internalMapName = alias;

                DebugOnce($"unmanaged|{requested}|{mv}|{pluginGuid}|{backportPresent}|{labsSatelliteRequested}|{internalMapName}", $"Alias decision: requested={requested}, MapVariants({mv}), labsSatelliteRequested={labsSatelliteRequested}, backportPlugin={pluginGuid}:{backportPresent}, result={internalMapName}");
                return;
            }

            // Defensive compatibility only when the MapVariants API itself cannot be resolved.
            // MapVariants is a hard dependency, so this should normally never execute.
            var backportLoaded = PluginDetection.Loaded(pluginGuid) && AliasAssetsReady(alias);
            var labsSatelliteFallback = normalized == "laboratory" &&
                                        MapStyleManager.WantsSatelliteArtwork(MapStyleKey.Labs);
            if (normalized == "laboratory" && backportLoaded && labsSatelliteFallback)
                internalMapName = alias;

            DebugOnce($"noapi|{requested}|{pluginGuid}|{backportLoaded}|{labsSatelliteFallback}|{internalMapName}", $"Alias decision: requested={requested}, MapVariants API unavailable, labsSatelliteRequested={labsSatelliteFallback}, backportPlugin={pluginGuid}:{backportLoaded}, result={internalMapName}");
        }

        private static bool AliasAssetsReady(string alias)
        {
            if (string.Equals(alias, InterchangeAlias, StringComparison.OrdinalIgnoreCase))
                return ExtensionAssetAvailability.ManimalInterchangeReady;
            if (string.Equals(alias, LabsAlias, StringComparison.OrdinalIgnoreCase))
                return ExtensionAssetAvailability.ManimalLabsReady;
            if (string.Equals(alias, FactoryClassicAlias, StringComparison.OrdinalIgnoreCase))
                return FactoryClassicAssets.Available;
            if (string.Equals(alias, ReserveSatelliteAlias, StringComparison.OrdinalIgnoreCase))
                return ExtensionAssetAvailability.ReserveSatelliteReady;
            return true;
        }
    }
}