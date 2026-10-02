using System;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    [HarmonyPatch]
    internal static class MapAliasPatch
    {
        internal const string InterchangeAlias = "dmext_interchange_backport";
        internal const string LabsAlias = "dmext_laboratory_backport";
        internal const string FactoryClassicAlias = "dmext_factory_classic";

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

            if (FactoryClassicBridge.IsFactory(internalMapName))
            {
                var classicChosen = FactoryClassicBridge.IsClassicChosen;
                var classicLoaded = FactoryClassicBridge.ClassicSceneLoaded;
                var assetsReady = FactoryClassicAssets.Available;

                if ((classicLoaded || classicChosen) && assetsReady)
                    internalMapName = FactoryClassicAlias;
                else if ((classicLoaded || classicChosen) && !assetsReady)
                    Plugin.Log?.LogWarning($"Factory Classic is selected, but DynamicMaps Extended's packaged Factory Classic map is incomplete. {FactoryClassicAssets.Describe()}");

                Plugin.Debug($"Alias decision: requested={requested}, factoryClassic chosen={classicChosen}, loaded={classicLoaded}, packagedAssetsReady={assetsReady}, result={internalMapName}");
                return;
            }

            var normalized = internalMapName.Trim().ToLowerInvariant();
            string alias = null;
            string pluginGuid = null;

            switch (normalized)
            {
                case "interchange": alias = InterchangeAlias; pluginGuid = Plugin.ManimalInterchangeGuid; break;
                case "lighthouse": Plugin.Debug($"Alias decision: requested={requested}, Manimal Lighthouse uses native DynamicMaps artwork until a verified/generated map is available."); return;
                case "laboratory": alias = LabsAlias; pluginGuid = Plugin.ManimalLabsGuid; break;
                default: Plugin.Debug($"Alias decision: requested={requested}, not a shared-ID map; unchanged."); return;
            }

            var mv = MapVariantsBridge.Resolve(internalMapName);
            if (mv.ApiAvailable && mv.Managed)
            {
                var backportPresent = PluginDetection.Loaded(pluginGuid);
                if (mv.Decided && mv.IsBackport && backportPresent)
                    internalMapName = alias;
                else if (mv.Decided && mv.IsBackport && !backportPresent)
                    Plugin.Log?.LogWarning($"MapVariants selected the backport for {requested}, but {pluginGuid} is not loaded. DynamicMaps will stay on the original map definition.");

                Plugin.Debug($"Alias decision: requested={requested}, MapVariants({mv}), backportPlugin={pluginGuid}:{backportPresent}, result={internalMapName}");
                return;
            }

            var backportLoaded = PluginDetection.Loaded(pluginGuid);
            if (backportLoaded)
                internalMapName = alias;

            Plugin.Debug($"Alias decision: requested={requested}, MapVariants({mv}), fallbackPlugin={pluginGuid}:{backportLoaded}, result={internalMapName}");
        }
    }
}
