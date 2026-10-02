using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace DynamicMapsExtended
{
    [BepInPlugin("com.blackhawk.dynamicmapsextended", "DynamicMaps Extended", "0.7.0")]
    [BepInDependency("com.mpstark.dynamicmaps", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.lennoxp90.mapvariants", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.lennoxp90.factoryclassic", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.interchange", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.lighthouse", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.labsboiler", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.icebreaker", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.blackhawk.dynamicmapsextended";
        public const string Name = "DynamicMaps Extended";
        public const string Version = "0.7.0";

        public const string DynamicMapsGuid = "com.mpstark.dynamicmaps";
        public const string MapVariantsGuid = "com.lennoxp90.mapvariants";
        public const string FactoryClassicGuid = "com.lennoxp90.factoryclassic";
        public const string ManimalInterchangeGuid = "com.manimal.interchange";
        public const string ManimalLighthouseGuid = "com.manimal.lighthouse";
        public const string ManimalLabsGuid = "com.manimal.labsboiler";
        public const string ManimalIcebreakerGuid = "com.manimal.icebreaker";
        public const string FikaCoreGuid = "com.fika.core";
        public const string FikaHeadlessGuid = "com.fika.headless";

        internal static ManualLogSource Log { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static bool DebugEnabled => DebugLogging != null && DebugLogging.Value;

        internal static string ExtensionRoot { get; private set; }
        internal static string MapsRoot => System.IO.Path.Combine(ExtensionRoot, "Maps");

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            ExtensionRoot = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

            MapStyleManager.Bind(Config);

            DebugLogging = Config.Bind(
                "Debug",
                "EnableDebugLogging",
                false,
                "Detailed DynamicMaps Extended diagnostics. Enable this in F12 before reproducing a problem, then send LogOutput.log. This does not enable any gameplay/network hooks.");

            DebugLogging.SettingChanged += (_, __) =>
            {
                if (DebugEnabled)
                {
                    Logger.LogInfo("[DMExt:DBG] Debug logging enabled from F12.");
                    LogDiagnosticSnapshot("F12 enabled");
                }
                else
                {
                    Logger.LogInfo("[DMExt] Debug logging disabled.");
                }
            };

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();

            Logger.LogInfo($"{Name} {Version} loaded");
            Logger.LogInfo($"Extension root: {ExtensionRoot}");
            Logger.LogInfo("All extension map definitions/assets are loaded from this extension folder; DynamicMaps files are not modified.");
            Logger.LogInfo("DynamicMaps keeps ownership of player tracking, quest/extract markers, minimap, floor selection and all marker providers.");
            Logger.LogInfo("Fika is not patched; this addon creates no Fika packets and contains no multiplayer synchronization code.");

            if (DebugEnabled)
                LogDiagnosticSnapshot("startup");
        }

        private void OnDestroy()
        {
            RasterSpritePatch.ReleaseAll("plugin shutdown");
            _harmony?.UnpatchSelf();
        }

        internal static void Debug(string message)
        {
            if (DebugEnabled)
                Log?.LogInfo("[DMExt:DBG] " + message);
        }

        internal static void LogDiagnosticSnapshot(string reason)
        {
            if (!DebugEnabled) return;

            Debug($"Diagnostic snapshot ({reason})");
            Debug($"Location: {DynamicMapsBridge.DescribeCurrentLocation()}");
            Debug($"Dependencies: DynamicMaps={PluginDetection.Loaded(DynamicMapsGuid)}, MapVariants={PluginDetection.Loaded(MapVariantsGuid)}, FactoryClassic={PluginDetection.Loaded(FactoryClassicGuid)}, Interchange={PluginDetection.Loaded(ManimalInterchangeGuid)}, Lighthouse={PluginDetection.Loaded(ManimalLighthouseGuid)}, Labs={PluginDetection.Loaded(ManimalLabsGuid)}, Icebreaker={PluginDetection.Loaded(ManimalIcebreakerGuid)}");
            Debug($"Fika presence only (never patched): core={PluginDetection.Loaded(FikaCoreGuid)}, headless={PluginDetection.Loaded(FikaHeadlessGuid)}");
            Debug($"MapVariants API: {MapVariantsBridge.DescribeApi()}");
            Debug($"FactoryClassic API: {FactoryClassicBridge.DescribeApi()}");
            Debug($"Raster cache: {RasterSpritePatch.DescribeCache()}");
            Debug($"Map style: {MapStyleManager.DescribeSettings()}");
            Debug($"Map style overrides: {MapStyleManager.DescribeOverrides()}");
            Debug($"Tracked DynamicMaps layers: {StyleRefreshManager.TrackedLayerCount}");
            Debug($"Factory Classic packaged map: {FactoryClassicAssets.Describe()}");
        }
    }
}
