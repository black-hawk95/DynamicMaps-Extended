using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    // Preserve DynamicMaps' own native/SVG precache, but mark the scope so extension-owned
    // raster layers can be skipped and warmed only for the active map.
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

    // DynamicMaps decides floor visibility first. If the resolved raster is cached, apply it.
    // Otherwise queue only that visible/default floor; no synchronous PNG decode or hidden-floor preload.
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

                var isOnTop = string.Equals(status, "OnTop", StringComparison.Ordinal) ||
                              (isDefault && !string.Equals(status, "Hidden", StringComparison.Ordinal) &&
                               !string.Equals(status, "Underneath", StringComparison.Ordinal));
                var isUnderneath = string.Equals(status, "Underneath", StringComparison.Ordinal);

                if (isOnTop || isUnderneath)
                {
                    var reason = $"floor level={level}, selected={__0}, status={status}, default={isDefault}";
                    var imagePath = RasterSpritePatch.GetLayerImagePath(__instance);
                    if (!string.IsNullOrEmpty(imagePath))
                    {
                        var resolution = MapStyleManager.Resolve(imagePath);
                        RasterCalibrationManager.ApplyLayer(__instance, resolution);
                        if (RasterSpritePatch.IsExtensionOwnedRaster(resolution.ResolvedPath))
                        {
                            RasterSpritePatch.EnsureLayerSprite(__instance, reason, background: isUnderneath && !isOnTop);
                        }
                        else if ((isOnTop || isUnderneath) && RasterSpritePatch.NeedsNativeRefresh(__instance))
                        {
                            // Needed after switching away from a DMExt raster/placeholder. Native
                            // Visible underneath floors also need restoration after a style change.
                            RasterSpritePatch.RefreshLayerSprite(__instance, reason);
                        }
                    }

                    // Optional high-resolution tile packs refine only the actually selected floor.
                    // If no .tiles pack is bundled this is a no-op.
                    TilePackManager.ReconcileLayer(__instance);
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Raster visible-layer check failed: " + e);
            }
        }
    }

    // Raster artwork is loaded on demand per resolved live layer.

    [HarmonyPatch]
    internal static class RasterMapSwitchCleanupPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "LoadMap")
                ?? throw new MissingMethodException(type.FullName, "LoadMap");
        }

        private static void Prefix(object __instance, object __0)
        {
            try
            {
                // DynamicMaps returns immediately for the already loaded definition. Mirror
                // that guard before resetting queues/tracking or releasing any live texture.
                var current = AccessTools.Property(__instance.GetType(), "CurrentMapDef")?.GetValue(__instance);
                if (__0 == null || ReferenceEquals(current, __0))
                    return;
                // Resolve map/layer geometry before DynamicMaps computes root size, minimum zoom and
                // clamp ranges. This prevents a correctly calibrated Satellite layer from being
                // squeezed or clipped by stale SVG map bounds.
                RasterCalibrationManager.PrepareMapDef(__0);
                var newPaths = GetRasterPathsStatic(__0);

                // Stop work from the previous map before DynamicMaps starts creating the new layers.
                RasterWarmupManager.BeginMapSelection(newPaths);
                TilePackManager.OnMapSelection(newPaths);
                StyleRefreshManager.Clear();

                // Keep only textures that belong to the map being selected. This prevents old menu
                // browsing caches from accumulating and avoids old background uploads during a raid.
                RasterSpritePatch.ReleaseAllExcept(newPaths, "DynamicMaps map switch");
            }
            catch (Exception e)
            {
                Plugin.Debug("Raster map-switch precheck failed: " + e);
            }
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
                if (RasterSpritePatch.IsExtensionOwnedRaster(resolved))
                    result.Add(resolved);
            }

            return result;
        }
    }

    // Debug-only synchronous timing for the DynamicMaps map construction path. This lets the next
    // test distinguish remaining DynamicMaps UI/marker cost from DMExt texture loading.
    [HarmonyPatch]
    internal static class MapViewLoadTimingPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "LoadMap")
                ?? throw new MissingMethodException(type.FullName, "LoadMap");
        }

        private static void Prefix(out long __state)
        {
            __state = Plugin.DebugEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        private static void Postfix(long __state)
        {
            if (__state == 0)
                return;

            var elapsedMs = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= 3.0)
                Plugin.Debug($"DynamicMaps MapView.LoadMap synchronous time: {elapsedMs:F1} ms");
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
            RaidWarmCacheManager.OnRaidEnd();
            RasterWarmupManager.OnRaidEnd();
            TilePackManager.OnRaidEnd();
            StyleRefreshManager.Clear();
            RasterSpritePatch.ReleaseAll("raid end");

            if (Plugin.DebugEnabled)
                Plugin.LogDiagnosticSnapshot("raid end");
        }
    }
}
