using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    internal static class DynamicMapsBridge
    {
        private static string _pluginPath;
        private static MethodInfo _getCurrentMapInternalName;
        private static bool _locationMethodSearched;

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

        internal static string GetCurrentLocationOrNull()
        {
            try
            {
                if (!_locationMethodSearched)
                {
                    _locationMethodSearched = true;
                    var type = AccessTools.TypeByName("DynamicMaps.Utils.GameUtils");
                    _getCurrentMapInternalName = type == null ? null : AccessTools.Method(type, "GetCurrentMapInternalName");
                }

                if (_getCurrentMapInternalName == null)
                    return null;

                var value = _getCurrentMapInternalName.Invoke(null, null) as string;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch
            {
                return null;
            }
        }

        internal static string DescribeCurrentLocation()
            => GetCurrentLocationOrNull() ?? "<not in raid>";
    }
}
