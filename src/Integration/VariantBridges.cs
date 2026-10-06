using System;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace DynamicMapsExtended
{
    internal readonly struct VariantDecision
    {
        internal readonly bool ApiAvailable;
        internal readonly bool Managed;
        internal readonly bool Decided;
        internal readonly bool IsBackport;

        internal VariantDecision(bool apiAvailable, bool managed, bool decided, bool isBackport)
        {
            ApiAvailable = apiAvailable;
            Managed = managed;
            Decided = decided;
            IsBackport = isBackport;
        }

        public override string ToString()
            => $"api={ApiAvailable}, managed={Managed}, decided={Decided}, backport={IsBackport}";
    }

    internal static class PluginDetection
    {
        internal static bool Loaded(string guid)
            => !string.IsNullOrEmpty(guid) && Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(guid);
    }

    internal static class MapVariantsBridge
    {
        private static Type _type;
        private static bool _searched;

        private static Type ApiType
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _type = AccessTools.TypeByName("MapVariants.Client.Api.Maps");
                    Plugin.Debug($"MapVariants API type search: {(_type == null ? "not found" : _type.FullName)}");
                }
                return _type;
            }
        }

        internal static VariantDecision Resolve(string locationId)
        {
            var type = ApiType;
            if (type == null)
                return new VariantDecision(false, false, false, false);

            try
            {
                var isManaged = AccessTools.Method(type, "IsManaged", new[] { typeof(string) });
                var managed = isManaged != null && (bool)isManaged.Invoke(null, new object[] { locationId });
                if (!managed)
                    return new VariantDecision(true, false, false, false);

                var currentLocation = AccessTools.Property(type, "CurrentLocationId")?.GetValue(null) as string;
                if (string.IsNullOrEmpty(currentLocation) ||
                    !string.Equals(Normalize(currentLocation), Normalize(locationId), StringComparison.OrdinalIgnoreCase))
                {
                    // CurrentVariant is global session state in MapVariants. If the API cannot prove
                    // this decision belongs to THIS raid location, never consume it.
                    return new VariantDecision(true, true, false, false);
                }

                var decidedObj = AccessTools.Property(type, "Decided")?.GetValue(null);
                var decided = decidedObj is bool b && b;
                if (!decided)
                    return new VariantDecision(true, true, false, false);

                var backportObj = AccessTools.Property(type, "IsBackport")?.GetValue(null);
                var backport = backportObj is bool isBackport && isBackport;
                return new VariantDecision(true, true, true, backport);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"MapVariants API read failed; leaving DynamicMaps location unchanged: {e.Message}");
                Plugin.Debug(e.ToString());
                // API exists, so do not fall back to plugin-presence inference after a read failure.
                return new VariantDecision(true, true, false, false);
            }
        }

        internal static string DescribeApi()
        {
            try
            {
                var type = ApiType;
                if (type == null) return "type not found";
                var version = AccessTools.Property(type, "ApiVersion")?.GetValue(null) as string ?? "unknown";
                var location = AccessTools.Property(type, "CurrentLocationId")?.GetValue(null) as string ?? "<null>";
                var variant = AccessTools.Property(type, "CurrentVariant")?.GetValue(null) as string ?? "<null>";
                return $"version={version}, location={location}, variant={variant}";
            }
            catch (Exception e)
            {
                return "read failed: " + e.Message;
            }
        }

        private static string Normalize(string value)
            => value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    internal static class FactoryClassicBridge
    {
        private static Type _type;
        private static bool _searched;

        private static Type ApiType
        {
            get
            {
                if (!_searched)
                {
                    _searched = true;
                    _type = AccessTools.TypeByName("FactoryClassic.Client.Api.Factory");
                    Plugin.Debug($"FactoryClassic API type search: {(_type == null ? "not found" : _type.FullName)}");
                }
                return _type;
            }
        }

        internal static bool IsFactory(string locationId)
        {
            if (string.IsNullOrEmpty(locationId)) return false;
            return locationId.Equals("factory4_day", StringComparison.OrdinalIgnoreCase)
                || locationId.Equals("factory4_night", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ClassicSceneLoaded
        {
            get
            {
                var type = ApiType;
                if (type == null) return false;
                try
                {
                    var value = AccessTools.Property(type, "ClassicSceneLoaded")?.GetValue(null);
                    return value is bool b && b;
                }
                catch (Exception e)
                {
                    Plugin.Debug("FactoryClassic ClassicSceneLoaded read failed: " + e.Message);
                    return false;
                }
            }
        }

        internal static bool IsClassicChosen
        {
            get
            {
                var type = ApiType;
                if (type == null) return false;
                try
                {
                    var decided = AccessTools.Property(type, "Decided")?.GetValue(null);
                    if (!(decided is bool d) || !d) return false;
                    var classic = AccessTools.Property(type, "IsClassic")?.GetValue(null);
                    return classic is bool c && c;
                }
                catch (Exception e)
                {
                    Plugin.Debug("FactoryClassic IsClassic read failed: " + e.Message);
                    return false;
                }
            }
        }

        internal static string DescribeApi()
        {
            try
            {
                var type = ApiType;
                if (type == null) return "not installed/type not found";
                var version = AccessTools.Property(type, "ApiVersion")?.GetValue(null) as string ?? "unknown";
                var variant = AccessTools.Property(type, "CurrentVariant")?.GetValue(null) as string ?? "<null>";
                return $"version={version}, variant={variant}, classicChosen={IsClassicChosen}, sceneLoaded={ClassicSceneLoaded}";
            }
            catch (Exception e)
            {
                return "read failed: " + e.Message;
            }
        }
    }
}