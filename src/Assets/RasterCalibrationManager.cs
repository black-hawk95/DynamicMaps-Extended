using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Keeps raster artwork in the world-coordinate rectangle published by tarkov.dev instead of
    // stretching it into the SVG rectangle of whatever DynamicMaps map definition happened to
    // request the sprite. This is critical for Satellite artwork and for shared artwork sources
    // such as normal Interchange vs. Manimal Interchange.
    internal static class RasterCalibrationManager
    {
        internal readonly struct Bounds2D
        {
            internal readonly Vector2 Min;
            internal readonly Vector2 Max;
            internal Bounds2D(float minX, float minY, float maxX, float maxY)
            {
                Min = new Vector2(minX, minY);
                Max = new Vector2(maxX, maxY);
            }
            internal Vector2 Size => Max - Min;
            internal Vector2 Midpoint => (Min + Max) * 0.5f;
            public override string ToString() => $"[{Min.x:0.###},{Min.y:0.###}]..[{Max.x:0.###},{Max.y:0.###}]";
        }

        private static readonly Dictionary<string, Bounds2D> SatelliteBounds =
            new Dictionary<string, Bounds2D>(StringComparer.OrdinalIgnoreCase)
            {
                ["maps/styleassets/groundzero/satellite/groundzero-ground.png"] = new Bounds2D(-99.0f, -124.0f, 249.0f, 364.0f),
                ["maps/styleassets/groundzero/satellite/groundzero-second.png"] = new Bounds2D(-99.0f, -124.0f, 249.0f, 364.0f),
                ["maps/styleassets/groundzero/satellite/groundzero-third.png"] = new Bounds2D(-99.0f, -124.0f, 249.0f, 364.0f),
                ["maps/styleassets/groundzero/satellite/groundzero-garage.png"] = new Bounds2D(-99.0f, -124.0f, 249.0f, 364.0f),
                ["maps/styleassets/customs/satellite/customs-ground.png"] = new Bounds2D(-372.0f, -307.0f, 698.0f, 237.0f),
                ["maps/styleassets/customs/satellite/customs-second.png"] = new Bounds2D(-372.0f, -307.0f, 698.0f, 237.0f),
                ["maps/styleassets/customs/satellite/customs-third.png"] = new Bounds2D(-372.0f, -307.0f, 698.0f, 237.0f),
                ["maps/styleassets/customs/satellite/customs-fourth.png"] = new Bounds2D(-372.0f, -307.0f, 698.0f, 237.0f),
                ["maps/styleassets/customs/satellite/customs-underground.png"] = new Bounds2D(-372.0f, -307.0f, 698.0f, 237.0f),
                ["maps/styleassets/factory/satellite/factory-ground.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/styleassets/factory/satellite/factory-second.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/styleassets/factory/satellite/factory-third.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/styleassets/factory/satellite/factory-tunnels.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/styleassets/interchange_backport/satellite/interchange-ground.png"] = new Bounds2D(-433.0f, -442.0f, 598.0f, 426.0f),
                ["maps/labs_backport/layers/labs-first.png"] = new Bounds2D(-287.0f, -477.0f, -80.0f, -193.0f),
                ["maps/labs_backport/layers/labs-second.png"] = new Bounds2D(-287.0f, -477.0f, -80.0f, -193.0f),
                ["maps/labs_backport/layers/labs-technical.png"] = new Bounds2D(-287.0f, -477.0f, -80.0f, -193.0f),
                ["maps/icebreaker/layers/06-infirmary.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/07-helipad.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/08-gym-canteen.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/09-accommodation-lower.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/10-accommodation-mid.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/11-accommodation-upper.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/12-officers-deck.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/13-stairs-blocked.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/14-bridge.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/15-bridge-roof.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/00-control-room.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/01-engine-room.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/02-engine-room-upper.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/03-fuel-pumps-lower.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/04-fuel-pumps.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/icebreaker/layers/05-storage-security.png"] = new Bounds2D(-65.5f, -64.5f, 77.0f, 67.4f),
                ["maps/styleassets/woods/satellite/woods-ground.png"] = new Bounds2D(-761.0f, -914.0f, 646.0f, 442.0f),
                ["maps/reserve_satellite/layers/reserve-ground.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/reserve_satellite/layers/reserve-second.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/reserve_satellite/layers/reserve-third.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/reserve_satellite/layers/reserve-fourth.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/reserve_satellite/layers/reserve-fifth.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/reserve_satellite/layers/reserve-bunkers.png"] = new Bounds2D(-303.0f, -293.0f, 289.0f, 244.0f),
                ["maps/styleassets/shoreline/satellite/shoreline-ground.png"] = new Bounds2D(-1056.0f, -415.0f, 504.0f, 618.0f),
                ["maps/styleassets/labyrinth/satellite/labyrinth-main.png"] = new Bounds2D(-52.0f, -37.0f, 53.0f, 76.0f),
            };

        // DynamicMaps' normal Interchange SVGs are correctly calibrated to these older/native
        // bounds. Manimal "DynamicMaps Vanilla" intentionally uses those SVGs and therefore must
        // also use their bounds, even though Manimal Abstract/Satellite use the newer tarkov.dev
        // rectangle below.
        private static readonly Bounds2D NativeInterchangeBounds = new Bounds2D(-364f, -443f, 534f, 452f);
        private static readonly Bounds2D CurrentInterchangeBounds = new Bounds2D(-433f, -442f, 598f, 426f);

        private static FieldInfo _mapLayerDefField;
        private static PropertyInfo _imageBoundsProperty;
        private static PropertyInfo _mapBoundsProperty;
        private static PropertyInfo _layersProperty;
        private static PropertyInfo _imagePathProperty;
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool TryGetArtworkBounds(string originalImagePath, StyleResolution resolution, object layerDef, out Bounds2D bounds)
        {
            // Shared Manimal Interchange has three deliberately different artwork profiles.
            if (resolution.Map == MapStyleKey.InterchangeManimal)
            {
                if (resolution.Selected == MapArtMode.Original)
                {
                    bounds = NativeInterchangeBounds;
                    return true;
                }
                if (resolution.Selected == MapArtMode.Abstract)
                {
                    bounds = CurrentInterchangeBounds;
                    return true;
                }
            }

            if (resolution.Selected == MapArtMode.Satellite && TryGetSatelliteBounds(resolution.ResolvedPath, out bounds))
                return true;

            return TryGetDefImageBounds(layerDef, out bounds);
        }

        internal static void ApplyLayer(object mapLayer, StyleResolution resolution)
        {
            if (!(mapLayer is Component component))
                return;

            var layerDef = GetLayerDef(mapLayer);
            if (layerDef == null || !TryGetArtworkBounds(RasterSpritePatch.GetLayerImagePath(mapLayer), resolution, layerDef, out var desired))
                return;

            if (!(component.transform is RectTransform rect))
                return;

            var size = desired.Size;
            var angle = rect.localEulerAngles.z * Mathf.Deg2Rad;
            var c = Mathf.Abs(Mathf.Cos(angle));
            var s = Mathf.Abs(Mathf.Sin(angle));
            var rotated = new Vector2(size.x * c + size.y * s, size.x * s + size.y * c);

            var changed = (rect.sizeDelta - rotated).sqrMagnitude > 0.0001f ||
                          (rect.anchoredPosition - desired.Midpoint).sqrMagnitude > 0.0001f;

            if (changed)
            {
                rect.sizeDelta = rotated;
                rect.anchoredPosition = desired.Midpoint;
            }

            if (changed && Plugin.DebugEnabled)
            {
                var key = RasterSpritePatch.GetLayerImagePath(mapLayer) + "|" + resolution.Selected + "|" + desired;
                if (Logged.Add(key))
                    Plugin.Debug($"Artwork bounds calibrated: map={resolution.Map}, style={resolution.Selected}, bounds={desired}");
            }
        }

        // Called before DynamicMaps builds a MapView. The root map rectangle must contain the
        // calibrated rectangles of all resolved layers; otherwise minimum zoom/clamping can crop a
        // correctly aligned Satellite layer even though markers themselves use the right coordinates.
        internal static void PrepareMapDef(object mapDef)
        {
            if (mapDef == null)
                return;

            try
            {
                var type = mapDef.GetType();
                _layersProperty ??= AccessTools.Property(type, "Layers");
                _mapBoundsProperty ??= AccessTools.Property(type, "Bounds");
                if (!(_layersProperty?.GetValue(mapDef) is IDictionary layers) || layers.Count == 0)
                    return;

                var have = false;
                var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

                foreach (DictionaryEntry pair in layers)
                {
                    var layerDef = pair.Value;
                    if (layerDef == null) continue;

                    _imagePathProperty ??= AccessTools.Property(layerDef.GetType(), "ImagePath");
                    var imagePath = _imagePathProperty?.GetValue(layerDef) as string;
                    if (string.IsNullOrEmpty(imagePath)) continue;

                    var resolution = MapStyleManager.Resolve(imagePath);
                    if (!TryGetArtworkBounds(imagePath, resolution, layerDef, out var b))
                        continue;

                    min.x = Mathf.Min(min.x, b.Min.x);
                    min.y = Mathf.Min(min.y, b.Min.y);
                    max.x = Mathf.Max(max.x, b.Max.x);
                    max.y = Mathf.Max(max.y, b.Max.y);
                    have = true;
                }

                if (!have) return;
                var mapBounds = _mapBoundsProperty?.GetValue(mapDef);
                if (mapBounds == null) return;
                SetBoundsObject(mapBounds, new Bounds2D(min.x, min.y, max.x, max.y));
            }
            catch (Exception e)
            {
                Plugin.Debug("Map bounds calibration failed: " + e.Message);
            }
        }

        internal static bool TryGetSatelliteBounds(string resolvedPath, out Bounds2D bounds)
        {
            bounds = default;
            if (string.IsNullOrWhiteSpace(resolvedPath)) return false;
            var p = Normalize(resolvedPath);
            foreach (var pair in SatelliteBounds)
            {
                if (p.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    bounds = pair.Value;
                    return true;
                }
            }
            return false;
        }

        private static object GetLayerDef(object mapLayer)
        {
            if (mapLayer == null) return null;
            _mapLayerDefField ??= AccessTools.Field(mapLayer.GetType(), "_def");
            return _mapLayerDefField?.GetValue(mapLayer);
        }

        private static bool TryGetDefImageBounds(object layerDef, out Bounds2D bounds)
        {
            bounds = default;
            if (layerDef == null) return false;
            _imageBoundsProperty ??= AccessTools.Property(layerDef.GetType(), "ImageBounds");
            return TryReadBoundsObject(_imageBoundsProperty?.GetValue(layerDef), out bounds);
        }

        private static bool TryReadBoundsObject(object value, out Bounds2D bounds)
        {
            bounds = default;
            if (value == null) return false;
            var t = value.GetType();
            var minProp = AccessTools.Property(t, "Min");
            var maxProp = AccessTools.Property(t, "Max");
            if (!(minProp?.GetValue(value) is Vector2 min) || !(maxProp?.GetValue(value) is Vector2 max))
                return false;
            bounds = new Bounds2D(min.x, min.y, max.x, max.y);
            return true;
        }

        private static void SetBoundsObject(object value, Bounds2D bounds)
        {
            if (value == null) return;
            var t = value.GetType();
            AccessTools.Property(t, "Min")?.SetValue(value, bounds.Min);
            AccessTools.Property(t, "Max")?.SetValue(value, bounds.Max);
        }

        private static string Normalize(string path)
            => path.Replace('\\', '/').Trim().ToLowerInvariant();
    }
}