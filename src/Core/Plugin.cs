using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    [BepInPlugin("com.blackhawk.dynamicmapsextended", "DynamicMaps Extended", "2.0.0")]
    [BepInDependency("com.mpstark.dynamicmaps", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.lennoxp90.mapvariants", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.lennoxp90.factoryclassic", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.interchange", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.lighthouse", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.labsboiler", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.manimal.icebreaker", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft dependency is intentional: when Fika Headless is installed, BepInEx loads it
    // before this plugin so the guard at the very start of Awake() can disable all
    // DynamicMaps Extended client/UI work before patches or asset preparation begin.
    [BepInDependency("com.fika.headless", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.blackhawk.dynamicmapsextended";
        public const string Name = "DynamicMaps Extended";
        public const string Version = "2.0.0";
        public const string BuildLabel = "RELEASE";

        public const string DynamicMapsGuid = "com.mpstark.dynamicmaps";
        public const string MapVariantsGuid = "com.lennoxp90.mapvariants";
        public const string FactoryClassicGuid = "com.lennoxp90.factoryclassic";
        public const string ManimalInterchangeGuid = "com.manimal.interchange";
        public const string ManimalLighthouseGuid = "com.manimal.lighthouse";
        public const string ManimalLabsGuid = "com.manimal.labsboiler";
        public const string ManimalIcebreakerGuid = "com.manimal.icebreaker";
        public const string FikaCoreGuid = "com.fika.core";
        public const string FikaHeadlessGuid = "com.fika.headless";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }
        internal static ConfigEntry<bool> DebugLogging { get; private set; }
        internal static bool DebugEnabled => DebugLogging != null && DebugLogging.Value;

        internal static string ExtensionRoot { get; private set; }
        internal static string MapsRoot => System.IO.Path.Combine(ExtensionRoot, "Maps");

        private Harmony _harmony;
        private bool _disabledForHeadless;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            ExtensionRoot = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);

            // Fika Headless is a non-visual raid host. DynamicMaps Extended is a client/UI
            // extension and must not patch UI, start raster workers, or download/build map
            // artwork there. The soft dependency above guarantees Headless is already in
            // Chainloader.PluginInfos when both plugins are installed.
            if (PluginDetection.Loaded(FikaHeadlessGuid))
            {
                _disabledForHeadless = true;
                Logger.LogInfo($"{Name} {Version}: Fika Headless detected ({FikaHeadlessGuid}) - client/UI functionality disabled. No Harmony patches, map workers, or asset downloading/preparation were started.");
                enabled = false;
                return;
            }

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

            RasterWarmupManager.Initialize();
            TilePackManager.Initialize();
            AssetPreparationManager.Initialize();

            Logger.LogInfo($"{Name} {Version} {BuildLabel} loaded");
            Logger.LogInfo($"Extension root: {ExtensionRoot}");
            Logger.LogInfo("Artwork is resolved per floor. Recommended prefers locally prepared Satellite artwork; missing Satellite floors stay on their original DynamicMaps artwork. Raster loading is on-demand and asynchronous.");
            Logger.LogInfo("DynamicMaps keeps ownership of player tracking, quest/extract markers, minimap, floor selection and all marker providers.");
            Logger.LogInfo("Fika is not patched; this addon creates no Fika packets and contains no multiplayer synchronization code.");

            if (DebugEnabled)
                LogDiagnosticSnapshot("startup");
        }

        private void Update()
        {
            if (_disabledForHeadless) return;
            AssetPreparationManager.Update();
        }

        private void OnGUI()
        {
            if (_disabledForHeadless) return;
            AssetPreparationManager.OnGUI();
        }

        private void OnDestroy()
        {
            if (_disabledForHeadless)
            {
                Instance = null;
                return;
            }

            AssetPreparationManager.Shutdown();
            RaidWarmCacheManager.Shutdown();
            MapDefinitionRefreshManager.Shutdown();
            TilePackManager.Shutdown();
            RasterWarmupManager.Shutdown();
            RasterSpritePatch.ReleaseAll("plugin shutdown");
            _harmony?.UnpatchSelf();
            Instance = null;
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
            Debug($"Raster queue: {RasterWarmupManager.DescribeQueue()}");
            Debug($"High-res tiles: {TilePackManager.Describe()}");
            Debug($"Raid warm cache: {RaidWarmCacheManager.Describe()}");
            Debug($"Map style: {MapStyleManager.DescribeSettings()}");
            Debug($"Map style overrides: {MapStyleManager.DescribeOverrides()}");
            Debug($"Tracked DynamicMaps layers: {StyleRefreshManager.TrackedLayerCount}");
            Debug($"Factory Classic packaged map: {FactoryClassicAssets.Describe()}");
            Debug($"Extension asset readiness: {ExtensionAssetAvailability.Describe()}");
            Debug($"Asset preparation: {AssetPreparationManager.Describe()}");
        }
    }
}