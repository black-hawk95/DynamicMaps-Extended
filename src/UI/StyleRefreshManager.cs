using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    internal static class StyleRefreshManager
    {
        private static readonly List<WeakReference> TrackedLayers = new List<WeakReference>();
        private static readonly object Sync = new object();

        private static PropertyInfo _statusProperty;
        private static PropertyInfo _defaultProperty;

        internal static void RegisterLayer(object mapLayer)
        {
            if (mapLayer == null) return;

            lock (Sync)
            {
                PruneDeadLocked();

                foreach (var wr in TrackedLayers)
                {
                    if (wr.IsAlive && ReferenceEquals(wr.Target, mapLayer))
                        return;
                }

                TrackedLayers.Add(new WeakReference(mapLayer));
            }
        }

        internal static void Clear()
        {
            lock (Sync)
                TrackedLayers.Clear();
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
            var sw = Stopwatch.StartNew();
            var alive = SnapshotAliveLayers();
            if (alive.Count == 0)
                return;

            // Selected floor first, underneath floors second, hidden floors last.
            alive.Sort((a, b) => VisibilityPriority(b).CompareTo(VisibilityPriority(a)));

            var changed = 0;
            var queued = 0;
            var native = 0;

            foreach (var layer in alive)
            {
                try
                {
                    var imagePath = RasterSpritePatch.GetLayerImagePath(layer);
                    if (string.IsNullOrEmpty(imagePath))
                        continue;

                    var resolution = MapStyleManager.Resolve(imagePath);
                    var priority = VisibilityPriority(layer);

                    // Artwork source and world calibration are separate. Satellite/current-map
                    // artwork can have different authoritative bounds from the SVG it replaces.
                    RasterCalibrationManager.ApplyLayer(layer, resolution);

                    // Reconcile optional high-resolution refinement for the selected floor.
                    // This is intentionally independent from preview loading and is a no-op when
                    // no bundled .tiles pack exists.
                    TilePackManager.ReconcileLayer(layer);

                    if (RasterSpritePatch.IsExtensionOwnedRaster(resolution.ResolvedPath))
                    {
                        // Hidden floors stay fully on-demand. The selected floor is foreground; an
                        // underneath floor may be needed for DynamicMaps compositing but is background.
                        if (priority <= 0)
                            continue;

                        if (RasterSpritePatch.TryApplyCachedRaster(layer, resolution.ResolvedPath))
                        {
                            changed++;
                        }
                        else
                        {
                            RasterWarmupManager.QueuePath(
                                resolution.ResolvedPath,
                                reason + (priority >= 2 ? " (selected)" : " (underneath)"),
                                background: priority < 2);
                            queued++;
                        }

                        continue;
                    }

                    // Non-raster artwork can be either DynamicMaps Vanilla or a bundled current
                    // Tarkov.dev Abstract SVG. Refresh the selected floor when entering Abstract,
                    // or when leaving any DMExt-owned raster/Abstract sprite.
                    if (priority < 2)
                        continue;

                    var wantsBundledAbstract = RasterSpritePatch.IsExtensionOwnedSvg(resolution.ResolvedPath);
                    if (!wantsBundledAbstract && !RasterSpritePatch.NeedsNativeRefresh(layer))
                        continue;

                    if (RasterSpritePatch.RefreshLayerSprite(layer, reason))
                    {
                        changed++;
                        native++;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Debug("Style refresh layer failed: " + e.Message);
                }
            }

            sw.Stop();
            Plugin.Debug(
                $"Style refresh: reason={reason}, tracked={alive.Count}, applied={changed}, native={native}, " +
                $"rasterQueued={queued}, elapsed={sw.ElapsedMilliseconds} ms");
        }

        internal static void ApplyReadyRaster(string resolvedPath)
        {
            if (string.IsNullOrEmpty(resolvedPath))
                return;

            var alive = SnapshotAliveLayers();
            var applied = 0;

            foreach (var layer in alive)
            {
                try
                {
                    var imagePath = RasterSpritePatch.GetLayerImagePath(layer);
                    if (string.IsNullOrEmpty(imagePath))
                        continue;

                    var currentResolution = MapStyleManager.Resolve(imagePath);
                    if (!string.Equals(currentResolution.ResolvedPath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    RasterCalibrationManager.ApplyLayer(layer, currentResolution);

                    if (RasterSpritePatch.TryApplyCachedRaster(layer, resolvedPath))
                    {
                        applied++;
                        TilePackManager.ReconcileLayer(layer);
                    }
                }
                catch
                {
                }
            }

            if (applied > 0)
                Plugin.Debug($"Applied async raster to {applied} live layer(s): {resolvedPath}");
        }

        internal static bool IsVisible(object layer)
            => VisibilityPriority(layer) > 0;

        internal static bool IsSelectedFloor(object layer)
            => VisibilityPriority(layer) >= 2;

        internal static bool IsUnderneathFloor(object layer)
            => VisibilityPriority(layer) == 1;

        private static int VisibilityPriority(object layer)
        {
            if (layer == null) return 0;

            try
            {
                var type = layer.GetType();
                _statusProperty ??= AccessTools.Property(type, "Status");
                _defaultProperty ??= AccessTools.Property(type, "IsOnDefaultLevel");

                var status = _statusProperty?.GetValue(layer)?.ToString() ?? "Unknown";
                var isDefault = _defaultProperty?.GetValue(layer) is bool b && b;

                if (string.Equals(status, "OnTop", StringComparison.Ordinal))
                    return 2;
                if (string.Equals(status, "Underneath", StringComparison.Ordinal))
                    return 1;

                // During initial construction the default layer can briefly report an unknown
                // status. Treat only that transient default layer as selected.
                if (isDefault && !string.Equals(status, "Hidden", StringComparison.Ordinal))
                    return 2;

                return 0;
            }
            catch
            {
                return 0;
            }
        }

        private static List<object> SnapshotAliveLayers()
        {
            var alive = new List<object>();

            lock (Sync)
            {
                for (var i = TrackedLayers.Count - 1; i >= 0; i--)
                {
                    var wr = TrackedLayers[i];
                    if (!wr.IsAlive || wr.Target == null)
                    {
                        TrackedLayers.RemoveAt(i);
                        continue;
                    }

                    alive.Add(wr.Target);
                }
            }

            alive.Reverse();
            return alive;
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