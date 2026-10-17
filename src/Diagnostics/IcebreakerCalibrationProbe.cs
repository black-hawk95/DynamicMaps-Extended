using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Read-only, opt-in landmark capture. Calibration cannot be inferred from screen pixels alone.
    internal static class IcebreakerCalibrationProbe
    {
        private static ConfigEntry<KeyboardShortcut> _capture;
        internal static void Bind(ConfigFile config)
        {
            _capture = config.Bind("Icebreaker Calibration", "LogLandmarkCoordinates",
                new KeyboardShortcut(KeyCode.None),
                "Diagnostic build: bind a free key. On Icebreaker, stand at a recognizable landmark, press it, and send LogOutput.log plus a screenshot showing that landmark on the map. Logs coordinates only; does not move markers or artwork.");
        }

        internal static void Update()
        {
            if (_capture == null || !_capture.Value.IsDown()) return;
            if (!string.Equals(DynamicMapsBridge.GetCurrentLocationOrNull(), "icebreaker", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                var type = AccessTools.TypeByName("DynamicMaps.Utils.GameUtils");
                var method = type == null ? null : AccessTools.Method(type, "GetMainPlayer");
                if (!(method?.Invoke(null, null) is Component player))
                {
                    Plugin.Log?.LogWarning("Icebreaker landmark: main player unavailable.");
                    return;
                }
                var p = player.transform.position;
                Plugin.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "Icebreaker landmark: UTC={0:O}, world=({1:F4},{2:F4},{3:F4}), mapXZ=({1:F4},{3:F4}), artwork={4}, build={5}",
                    DateTime.UtcNow, p.x, p.y, p.z, MapStyleManager.DescribeOverrides(), Plugin.BuildLabel));
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Icebreaker landmark capture failed: " + e.Message);
            }
        }
    }
}
