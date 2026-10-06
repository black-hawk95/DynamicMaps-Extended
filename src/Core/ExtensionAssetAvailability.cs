using System;
using System.IO;

namespace DynamicMapsExtended
{
    internal static class ExtensionAssetAvailability
    {
        private static bool ExistsExtension(string relative)
        {
            try
            {
                var path = Path.Combine(Plugin.ExtensionRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch { return false; }
        }


        private static bool ExistsPackForPreview(string relativePreview)
        {
            try
            {
                var preview = Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, relativePreview.Replace('/', Path.DirectorySeparatorChar)));
                var pack = PersistentAssetCache.ResolvePackForPreview(preview);
                return !string.IsNullOrWhiteSpace(pack) && File.Exists(pack) && new FileInfo(pack).Length > 0;
            }
            catch { return false; }
        }
        private static bool ExistsDynamicMaps(string relative)
        {
            try
            {
                var path = Path.Combine(DynamicMapsBridge.PluginPath, relative.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path) && new FileInfo(path).Length > 0;
            }
            catch { return false; }
        }

        internal static bool ManimalInterchangeReady =>
            ExistsExtension("Maps/Interchange_Backport/AbstractRaster/Interchange-Ground.png") &&
            ExistsPackForPreview("Maps/Interchange_Backport/AbstractRaster/Interchange-Ground.png") &&
            ExistsExtension("Maps/Interchange_Backport/AbstractRaster/Interchange-First.png") &&
            ExistsPackForPreview("Maps/Interchange_Backport/AbstractRaster/Interchange-First.png") &&
            ExistsExtension("Maps/Interchange_Backport/AbstractRaster/Interchange-Second.png") &&
            ExistsPackForPreview("Maps/Interchange_Backport/AbstractRaster/Interchange-Second.png") &&
            ExistsExtension("Maps/StyleAssets/Interchange_Backport/Satellite/Interchange-Ground.png") &&
            ExistsPackForPreview("Maps/StyleAssets/Interchange_Backport/Satellite/Interchange-Ground.png");

        internal static bool ManimalLabsReady =>
            ExistsExtension("Maps/Labs_Backport/Layers/Labs-Technical.png") &&
            ExistsExtension("Maps/Labs_Backport/Layers/Labs-First.png") &&
            ExistsExtension("Maps/Labs_Backport/Layers/Labs-Second.png");

        internal static bool IcebreakerReady
        {
            get
            {
                for (var i = 0; i < 16; i++)
                {
                    var prefix = i.ToString("00") + "-";
                    var dir = Path.Combine(Plugin.ExtensionRoot, "Maps", "Icebreaker", "Layers");
                    try
                    {
                        if (!Directory.Exists(dir) || Directory.GetFiles(dir, prefix + "*.png").Length == 0)
                            return false;
                    }
                    catch { return false; }
                }
                return true;
            }
        }


        internal static bool ReserveSatelliteReady =>
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Ground.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Ground.png") &&
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Second.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Second.png") &&
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Third.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Third.png") &&
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Fourth.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Fourth.png") &&
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Fifth.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Fifth.png") &&
            ExistsExtension("Maps/Reserve_Satellite/Layers/Reserve-Bunkers.png") &&
            ExistsPackForPreview("Maps/Reserve_Satellite/Layers/Reserve-Bunkers.png");

        internal static bool LabyrinthSatelliteReady =>
            ExistsExtension("Maps/StyleAssets/Labyrinth/Satellite/Labyrinth-Main.png") &&
            ExistsPackForPreview("Maps/StyleAssets/Labyrinth/Satellite/Labyrinth-Main.png");

        internal static string Describe()
            => $"InterchangeManimal={ManimalInterchangeReady}, LabsManimal={ManimalLabsReady}, Icebreaker={IcebreakerReady}, ReserveSatellite={ReserveSatelliteReady}, LabyrinthSatellite={LabyrinthSatelliteReady}";
    }
}