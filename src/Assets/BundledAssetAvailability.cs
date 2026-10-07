using System;
using System.IO;
using Newtonsoft.Json;
namespace DynamicMapsExtended
{
    // Read-only installation check. Missing files require reinstalling the complete map ZIP.
    internal static class BundledAssetAvailability
    {
        private static int _present;
        private static int _total;
        internal static void Initialize()
        {
            try
            {
                var manifest = JsonConvert.DeserializeObject<RuntimeAssetManifest>(File.ReadAllText(
                    Path.Combine(Plugin.ExtensionRoot, "Assets/DMEXT-ASSET-MANIFEST-v2.0.0.json")));
                foreach (var asset in manifest.Assets)
                {
                    _total++;
                    var preview = Path.Combine(Plugin.ExtensionRoot, asset.Preview.OutputPath);
                    var pack = PersistentAssetCache.GetPackPath(asset.Pack.OutputPath);
                    var warm = asset.Warm == null ? null : Path.Combine(Plugin.ExtensionRoot, asset.Warm.OutputPath);
                    if (Exists(preview) && Exists(pack) && (warm == null || Exists(warm))) _present++;
                    else Plugin.Log?.LogWarning($"Bundled artwork missing for {asset.Id}. Re-extract the complete map ZIP; available artwork remains usable.");
                }
                Plugin.Log?.LogInfo($"Bundled map artwork: {_present}/{_total} layers installed. No runtime downloads or generation.");
            }
            catch (Exception e) { Plugin.Log?.LogWarning("Cannot check bundled artwork: " + e.Message); }
        }
        private static bool Exists(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
        internal static string Describe() => $"installed={_present}/{_total}, read-only";
        internal static bool KnownSatelliteChoice(MapStyleKey key)
        {
            switch (key)
            {
                case MapStyleKey.GroundZero:
                case MapStyleKey.Customs:
                case MapStyleKey.Factory:
                case MapStyleKey.Woods:
                case MapStyleKey.Interchange:
                case MapStyleKey.InterchangeManimal:
                case MapStyleKey.Labs:
                case MapStyleKey.LabsManimal:
                case MapStyleKey.Icebreaker:
                case MapStyleKey.Reserve:
                case MapStyleKey.Shoreline:
                case MapStyleKey.Labyrinth:
                    return true;
                default:
                    return false;
            }
        }

    }
}
