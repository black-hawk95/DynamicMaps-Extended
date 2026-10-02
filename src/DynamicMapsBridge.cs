using System;
using System.IO;
using HarmonyLib;

namespace DynamicMapsExtended
{
    internal static class DynamicMapsBridge
    {
        private static string _pluginPath;

        internal static string PluginPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_pluginPath))
                    return _pluginPath;

                var type = AccessTools.TypeByName("DynamicMaps.Plugin");
                var pathField = type == null ? null : AccessTools.Field(type, "Path");
                _pluginPath = pathField?.GetValue(null) as string;

                if (string.IsNullOrEmpty(_pluginPath))
                    throw new InvalidOperationException("Could not resolve DynamicMaps.Plugin.Path.");

                Plugin.Debug($"Resolved DynamicMaps plugin path: {_pluginPath}");
                return _pluginPath;
            }
        }

        internal static string ExtensionMapsRoot => Plugin.MapsRoot;

        internal static string DescribeCurrentLocation()
        {
            try
            {
                var type = AccessTools.TypeByName("DynamicMaps.Utils.GameUtils");
                var method = type == null ? null : AccessTools.Method(type, "GetCurrentMapInternalName");
                if (method == null) return "DynamicMaps GameUtils unavailable";
                return method.Invoke(null, null) as string ?? "<not in raid>";
            }
            catch (Exception e)
            {
                return "read failed: " + e.Message;
            }
        }
    }
}
