using System;
using System.Collections.Generic;
using System.IO;

namespace DynamicMapsExtended
{
    internal static class IcebreakerArtwork
    {
        // Registration of the unchanged 9121 x 14774 raster canvas. Pixel Y grows south.
        // Measurements and limits are documented in Maps/Icebreaker/RASTER-CALIBRATION.md.
        internal static readonly RasterCalibrationManager.Bounds2D RasterBounds =
            new RasterCalibrationManager.Bounds2D((5024f - 9121f) / 47.4f, -8432f / 49f,
                5024f / 47.4f, (14774f - 8432f) / 49f);
        internal static readonly RasterCalibrationManager.Bounds2D VectorBounds =
            new RasterCalibrationManager.Bounds2D(-17.5f, -87f, 17.37f, 61.3f);
        // Keep zoom limits stable when switching style; transparent raster margins are not the ship.
        internal static readonly RasterCalibrationManager.Bounds2D ViewBounds =
            new RasterCalibrationManager.Bounds2D(-20f, -90f, 20f, 85f);

        private static readonly Dictionary<string, string> Decks =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["00-Control-Room.png"] = "Icebreaker-Deck_m3.svg",
                ["01-Engine-Room.png"] = "Icebreaker-Deck_m2.svg",
                ["02-Engine-Room-Upper.png"] = "Icebreaker-Deck_m2.svg",
                ["03-Fuel-Pumps-Lower.png"] = "Icebreaker-Deck_m1.svg",
                ["04-Fuel-Pumps.png"] = "Icebreaker-Deck_m1.svg",
                ["05-Storage-Security.png"] = "Icebreaker-Deck_0.svg",
                ["06-Infirmary.png"] = "Icebreaker-Deck_1.svg",
                ["07-Helipad.png"] = "Icebreaker-Deck_2.svg",
                ["08-Gym-Canteen.png"] = "Icebreaker-Deck_3.svg",
                ["09-Accommodation-Lower.png"] = "Icebreaker-Deck_4.svg",
                ["10-Accommodation-Mid.png"] = "Icebreaker-Deck_5.svg",
                ["11-Accommodation-Upper.png"] = "Icebreaker-Deck_6.svg",
                ["12-Officers-Deck.png"] = "Icebreaker-Deck_7.svg",
                ["13-Stairs-Blocked.png"] = "Icebreaker-Deck_8.svg",
                ["14-Bridge.png"] = "Icebreaker-Deck_9.svg",
                ["15-Bridge-Roof.png"] = "Icebreaker-Deck_10.svg",
            };

        internal static IEnumerable<string> RasterFiles => Decks.Keys;

        internal static bool TryResolve(string source, MapArtMode requested, out StyleResolution result)
        {
            result = default;
            if (!Decks.TryGetValue(Path.GetFileName(source), out var svg)) return false;
            var root = Path.Combine(Plugin.ExtensionRoot, "Maps", "Icebreaker", "Layers");
            var satellite = Path.Combine(root, Path.GetFileName(source));
            var vector = Path.Combine(root, svg);
            var wantVector = requested == MapArtMode.Original;
            var selected = wantVector ? vector : satellite;
            var available = File.Exists(selected);
            if (!available) selected = wantVector ? satellite : vector;
            var mode = selected.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? MapArtMode.Original : MapArtMode.Satellite;
            result = new StyleResolution(MapStyleKey.Icebreaker, requested, mode, source,
                File.Exists(selected) ? selected : source, !available, available);
            return true;
        }

        // Engine and fuel each have two raster subfloors but only one SVG deck.
        // Draw the shared SVG once while keeping DynamicMaps' marker floor statuses intact.
        internal static bool IsDuplicateUnderlay(string source, string status)
            => status == "Underneath" &&
               (string.Equals(Path.GetFileName(source), "01-Engine-Room.png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(source), "03-Fuel-Pumps-Lower.png", StringComparison.OrdinalIgnoreCase));
    }
}
