using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    internal static class StyleRefreshManager
    {
        private static readonly List<WeakReference> TrackedLayers = new List<WeakReference>();
        private static readonly object Sync = new object();

        private static PropertyInfo _levelProperty;
        private static PropertyInfo _statusProperty;
        private static PropertyInfo _defaultProperty;
        private static FieldInfo _defField;

        internal static void RegisterLayer(object mapLayer)
        {
            if (mapLayer == null) return;

            var added = false;
            lock (Sync)
            {
                PruneDeadLocked();

                foreach (var wr in TrackedLayers)
                {
                    if (!wr.IsAlive) continue;
                    if (ReferenceEquals(wr.Target, mapLayer))
                        return;
                }

                TrackedLayers.Add(new WeakReference(mapLayer));
                added = true;
            }

            if (added && Plugin.DebugEnabled)
            {
                try
                {
                    Plugin.Debug($"Tracked DynamicMaps layer: {DescribeLayerIdentity(mapLayer)}, sprite={RasterSpritePatch.DescribeLayerSprite(mapLayer)}");
                }
                catch (Exception e)
                {
                    Plugin.Debug("Could not describe newly tracked layer: " + e.Message);
                }
            }
        }

        internal static void Clear()
        {
            int count;
            lock (Sync)
            {
                count = TrackedLayers.Count;
                TrackedLayers.Clear();
            }

            if (count > 0)
                Plugin.Debug($"Cleared {count} tracked DynamicMaps layer reference(s).");
        }

        internal static int TrackedLayerCount
        {
            get
            {
                lock (Sync)
                {
                    PruneDeadLocked();
                    return TrackedLayers.Count;
                }
            }
        }

        internal static void RefreshTrackedLayers(string reason)
        {
            var totalWatch = Stopwatch.StartNew();
            var alive = SnapshotAliveLayers(out var stalePruned);

            Plugin.Debug(
                $"Style refresh BEGIN: reason={reason}, location={DynamicMapsBridge.DescribeCurrentLocation()}, " +
                $"trackedAlive={alive.Count}, stalePruned={stalePruned}, settings=[{MapStyleManager.DescribeSettings()}], " +
                $"overrides=[{MapStyleManager.DescribeOverrides()}], cacheBefore=[{RasterSpritePatch.DescribeCache()}]");

            Plugin.Debug($"Style refresh context: MapVariants={MapVariantsBridge.DescribeApi()}");
            Plugin.Debug($"Style refresh context: FactoryClassic={FactoryClassicBridge.DescribeApi()}");

            if (alive.Count == 0)
            {
                totalWatch.Stop();
                Plugin.Debug($"Style refresh END: no live DynamicMaps layers, elapsed={totalWatch.ElapsedMilliseconds} ms.");
                return;
            }

            var refreshed = 0;
            var changed = 0;
            var unchanged = 0;
            var fallbacks = 0;
            var missingRequested = 0;
            var errors = 0;
            var aspectWarnings = 0;

            foreach (var layer in alive)
            {
                var sw = Stopwatch.StartNew();

                try
                {
                    var identity = DescribeLayerIdentity(layer);
                    var imagePath = GetOriginalImagePath(layer);
                    var resolution = string.IsNullOrEmpty(imagePath)
                        ? default(StyleResolution?)
                        : MapStyleManager.Resolve(imagePath);

                    if (resolution.HasValue)
                    {
                        if (resolution.Value.UsedFallback) fallbacks++;
                        if (!resolution.Value.HasRequestedAsset) missingRequested++;
                    }

                    var before = RasterSpritePatch.GetLayerSprite(layer);
                    var beforeDesc = RasterSpritePatch.DescribeSprite(before);

                    var ok = RasterSpritePatch.RefreshLayerSprite(layer, reason);

                    var after = RasterSpritePatch.GetLayerSprite(layer);
                    var afterDesc = RasterSpritePatch.DescribeSprite(after);
                    var didChange = !ReferenceEquals(before, after);

                    if (ok) refreshed++;
                    if (didChange) changed++; else unchanged++;

                    var aspectWarning = CheckAspectMismatch(before, after, out var aspectText);
                    if (aspectWarning) aspectWarnings++;

                    sw.Stop();

                    if (resolution.HasValue)
                    {
                        Plugin.Debug(
                            $"Style refresh layer: {identity}, requested={resolution.Value.Requested}, " +
                            $"selected={resolution.Value.Selected}, fallback={resolution.Value.UsedFallback}, " +
                            $"requestedAssetAvailable={resolution.Value.HasRequestedAsset}, changed={didChange}, " +
                            $"source={ShortPath(imagePath)}, resolved={ShortPath(resolution.Value.ResolvedPath)}, " +
                            $"before=[{beforeDesc}], after=[{afterDesc}], elapsed={sw.ElapsedMilliseconds} ms" +
                            (aspectWarning ? $", WARNING={aspectText}" : string.Empty));
                    }
                    else
                    {
                        Plugin.Debug(
                            $"Style refresh layer: {identity}, no ImagePath, changed={didChange}, " +
                            $"before=[{beforeDesc}], after=[{afterDesc}], elapsed={sw.ElapsedMilliseconds} ms");
                    }
                }
                catch (Exception e)
                {
                    errors++;
                    sw.Stop();
                    Plugin.Debug($"Style refresh ERROR after {sw.ElapsedMilliseconds} ms: {e}");
                }
            }

            totalWatch.Stop();

            Plugin.Debug(
                $"Style refresh END: refreshed={refreshed}/{alive.Count}, changed={changed}, unchanged={unchanged}, " +
                $"fallbackLayers={fallbacks}, requestedStyleUnavailable={missingRequested}, aspectWarnings={aspectWarnings}, " +
                $"errors={errors}, elapsed={totalWatch.ElapsedMilliseconds} ms, cacheAfter=[{RasterSpritePatch.DescribeCache()}]");
        }

        private static List<object> SnapshotAliveLayers(out int stalePruned)
        {
            var alive = new List<object>();
            stalePruned = 0;

            lock (Sync)
            {
                for (var i = TrackedLayers.Count - 1; i >= 0; i--)
                {
                    var wr = TrackedLayers[i];
                    if (!wr.IsAlive || wr.Target == null)
                    {
                        TrackedLayers.RemoveAt(i);
                        stalePruned++;
                        continue;
                    }

                    alive.Add(wr.Target);
                }
            }

            alive.Reverse();
            return alive;
        }

        private static string DescribeLayerIdentity(object layer)
        {
            if (layer == null) return "layer=<null>";

            var type = layer.GetType();
            _levelProperty ??= AccessTools.Property(type, "Level");
            _statusProperty ??= AccessTools.Property(type, "Status");
            _defaultProperty ??= AccessTools.Property(type, "IsOnDefaultLevel");

            var level = _levelProperty?.GetValue(layer)?.ToString() ?? "?";
            var status = _statusProperty?.GetValue(layer)?.ToString() ?? "?";
            var isDefault = _defaultProperty?.GetValue(layer) is bool b && b;

            return $"level={level}, status={status}, default={isDefault}";
        }

        private static string GetOriginalImagePath(object layer)
        {
            if (layer == null) return null;

            var type = layer.GetType();
            _defField ??= AccessTools.Field(type, "_def");
            var def = _defField?.GetValue(layer);
            return def == null ? null : AccessTools.Property(def.GetType(), "ImagePath")?.GetValue(def) as string;
        }

        private static bool CheckAspectMismatch(Sprite before, Sprite after, out string text)
        {
            text = null;
            if (before == null || after == null || ReferenceEquals(before, after))
                return false;

            var bw = before.rect.width;
            var bh = before.rect.height;
            var aw = after.rect.width;
            var ah = after.rect.height;

            if (bw <= 0 || bh <= 0 || aw <= 0 || ah <= 0)
                return false;

            var beforeAspect = bw / bh;
            var afterAspect = aw / ah;
            var delta = Math.Abs(afterAspect - beforeAspect) / Math.Max(0.0001f, beforeAspect);

            if (delta <= 0.02f)
                return false;

            text = $"sprite aspect changed {beforeAspect:F4}->{afterAspect:F4} ({delta * 100f:F1}%); verify map bounds/crop alignment";
            return true;
        }

        private static string ShortPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "<none>";

            try
            {
                if (!string.IsNullOrEmpty(Plugin.ExtensionRoot) &&
                    path.StartsWith(Plugin.ExtensionRoot, StringComparison.OrdinalIgnoreCase))
                    return "<Extension>" + path.Substring(Plugin.ExtensionRoot.Length);

                if (!string.IsNullOrEmpty(DynamicMapsBridge.PluginPath) &&
                    path.StartsWith(DynamicMapsBridge.PluginPath, StringComparison.OrdinalIgnoreCase))
                    return "<DynamicMaps>" + path.Substring(DynamicMapsBridge.PluginPath.Length);
            }
            catch
            {
            }

            return path;
        }

        private static void PruneDeadLocked()
        {
            for (var i = TrackedLayers.Count - 1; i >= 0; i--)
            {
                if (!TrackedLayers[i].IsAlive || TrackedLayers[i].Target == null)
                    TrackedLayers.RemoveAt(i);
            }
        }
    }
}
