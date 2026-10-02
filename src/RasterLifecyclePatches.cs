using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    [HarmonyPatch]
    internal static class RasterPrecacheScopePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen")
                ?? throw new MissingMemberException("DynamicMaps.UI.ModdedMapScreen not found");
            return AccessTools.Method(type, "PrecacheCoroutine")
                ?? throw new MissingMethodException(type.FullName, "PrecacheCoroutine");
        }

        private static void Postfix(ref IEnumerator __result)
        {
            if (__result != null)
                __result = Wrap(__result);
        }

        private static IEnumerator Wrap(IEnumerator inner)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    RasterSpritePatch.InDynamicMapsPrecache = true;
                    try
                    {
                        moved = inner.MoveNext();
                    }
                    finally
                    {
                        RasterSpritePatch.InDynamicMapsPrecache = false;
                    }

                    if (!moved)
                        yield break;

                    yield return inner.Current;
                }
            }
            finally
            {
                RasterSpritePatch.InDynamicMapsPrecache = false;
                if (inner is IDisposable disposable)
                    disposable.Dispose();
            }
        }
    }

    [HarmonyPatch]
    internal static class RasterMapLayerCreateScopePatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapLayer")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapLayer not found");
            return AccessTools.Method(type, "Create")
                ?? throw new MissingMethodException(type.FullName, "Create");
        }

        private static void Prefix()
        {
            RasterSpritePatch.InMapLayerCreate = true;
        }

        private static void Postfix()
        {
            RasterSpritePatch.InMapLayerCreate = false;
        }

        private static Exception Finalizer(Exception __exception)
        {
            RasterSpritePatch.InMapLayerCreate = false;
            return __exception;
        }
    }

    [HarmonyPatch]
    internal static class RasterVisibleLayerLoadPatch
    {
        private static PropertyInfo _statusProperty;
        private static PropertyInfo _defaultProperty;
        private static PropertyInfo _levelProperty;

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapLayer")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapLayer not found");
            return AccessTools.Method(type, "OnTopLevelSelected", new[] { typeof(int) })
                ?? throw new MissingMethodException(type.FullName, "OnTopLevelSelected");
        }

        private static void Postfix(object __instance, int __0)
        {
            if (__instance == null) return;

            StyleRefreshManager.RegisterLayer(__instance);

            try
            {
                var type = __instance.GetType();
                _statusProperty ??= AccessTools.Property(type, "Status");
                _defaultProperty ??= AccessTools.Property(type, "IsOnDefaultLevel");
                _levelProperty ??= AccessTools.Property(type, "Level");

                var status = _statusProperty?.GetValue(__instance)?.ToString() ?? "Unknown";
                var isDefault = _defaultProperty?.GetValue(__instance) is bool b && b;
                var level = _levelProperty?.GetValue(__instance) is int l ? l : int.MinValue;

                if (!string.Equals(status, "Hidden", StringComparison.Ordinal) || isDefault)
                {
                    var loaded = RasterSpritePatch.EnsureLayerSprite(__instance, $"visible floor level={level}, selected={__0}, status={status}, default={isDefault}");
                    if (loaded)
                        Plugin.Debug($"Raster floor ready: level={level}, selected={__0}, status={status}, default={isDefault}");
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Raster visible-layer check failed: " + e);
            }
        }
    }

    [HarmonyPatch]
    internal static class RasterActiveMapPreloadPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "LoadMap")
                ?? throw new MissingMethodException(type.FullName, "LoadMap");
        }

        private static void Postfix(object __0)
        {
            if (__0 == null) return;

            try
            {
                var paths = RasterMapSwitchCleanupPatch.GetRasterPathsStatic(__0);
                if (paths.Count == 0) return;

                RasterSpritePatch.PreloadPaths(paths, "selected-map full preload");
            }
            catch (Exception e)
            {
                Plugin.Debug("Raster active-map preload failed: " + e);
            }
        }
    }

    [HarmonyPatch]
    internal static class RasterMapSwitchCleanupPatch
    {
        private static PropertyInfo _currentMapDefProperty;

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "LoadMap")
                ?? throw new MissingMethodException(type.FullName, "LoadMap");
        }

        private static void Prefix(object __instance, object __0, out List<string> __state)
        {
            __state = null;
            if (__instance == null || __0 == null) return;

            try
            {
                _currentMapDefProperty ??= AccessTools.Property(__instance.GetType(), "CurrentMapDef");
                var oldDef = _currentMapDefProperty?.GetValue(__instance);
                if (oldDef == null || ReferenceEquals(oldDef, __0)) return;

                var oldPaths = GetRasterPathsStatic(oldDef);
                if (oldPaths.Count == 0) return;

                var newPaths = new HashSet<string>(GetRasterPathsStatic(__0), StringComparer.OrdinalIgnoreCase);
                oldPaths.RemoveAll(p => newPaths.Contains(p));
                if (oldPaths.Count > 0)
                    __state = oldPaths;
            }
            catch (Exception e)
            {
                Plugin.Debug("Raster map-switch precheck failed: " + e);
            }
        }

        private static void Postfix(List<string> __state)
        {
            if (__state != null && __state.Count > 0)
                RasterSpritePatch.ReleasePaths(__state, "DynamicMaps map switch");
        }

        internal static List<string> GetRasterPathsStatic(object mapDef)
        {
            var result = new List<string>();
            if (mapDef == null) return result;

            var layersProp = AccessTools.Property(mapDef.GetType(), "Layers");
            if (!(layersProp?.GetValue(mapDef) is IDictionary layers))
                return result;

            foreach (DictionaryEntry pair in layers)
            {
                var layerDef = pair.Value;
                if (layerDef == null) continue;
                var imagePath = AccessTools.Property(layerDef.GetType(), "ImagePath")?.GetValue(layerDef) as string;
                if (string.IsNullOrEmpty(imagePath)) continue;

                var resolved = MapStyleManager.Resolve(imagePath).ResolvedPath;
                if (RasterSpritePatch.IsRasterPath(resolved))
                    result.Add(resolved);
            }

            return result;
        }
    }

    [HarmonyPatch]
    internal static class RasterRaidEndCleanupPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen")
                ?? throw new MissingMemberException("DynamicMaps.UI.ModdedMapScreen not found");
            return AccessTools.Method(type, "OnRaidEnd")
                ?? throw new MissingMethodException(type.FullName, "OnRaidEnd");
        }

        private static void Postfix()
        {
            StyleRefreshManager.Clear();
            RasterSpritePatch.ReleaseAll("raid end");
            if (Plugin.DebugEnabled)
                Plugin.LogDiagnosticSnapshot("raid end");
        }
    }
}
