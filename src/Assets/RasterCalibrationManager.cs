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
                ["maps/icebreaker/layers/06-infirmary.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/07-helipad.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/08-gym-canteen.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/09-accommodation-lower.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/10-accommodation-mid.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/11-accommodation-upper.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/12-officers-deck.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/13-stairs-blocked.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/14-bridge.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/15-bridge-roof.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/00-control-room.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/01-engine-room.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/02-engine-room-upper.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/03-fuel-pumps-lower.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/04-fuel-pumps.png"] = IcebreakerArtwork.RasterBounds,
                ["maps/icebreaker/layers/05-storage-security.png"] = IcebreakerArtwork.RasterBounds,
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

        // Artwork authored in a different compass orientation than the DynamicMaps map definition
        // that requests it. The value is the rotation the artwork itself needs, in the same
        // convention as MapDef.CoordinateRotation.
        //
        // Labyrinth is the only current entry: the bundled Labyrinth.svg is authored for 90
        // (Labyrinth_TarkovData.jsonc declares 90), while the Tarkov.dev raster substituted for it
        // by the Satellite style is authored for 270. Swapping the sprite without correcting the
        // transform drew the map 180 degrees away from the live markers.
        //
        // The correction is applied to the map layer's RectTransform only. DynamicMaps keeps
        // ownership of marker placement, and the high-resolution tile canvas is a child of the same
        // layer, so it inherits the correction and cannot drift out of sync with the preview.
        // When the requested artwork is not listed, the map definition's own rotation is used, so
        // switching back to native artwork restores the original orientation.
        private static readonly Dictionary<string, float> ArtworkRotations =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["maps/styleassets/labyrinth/satellite/labyrinth-main.png"] = 270f,
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
            if (resolution.Map == MapStyleKey.Icebreaker)
            {
                bounds = resolution.ResolvedPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                    ? IcebreakerArtwork.VectorBounds : IcebreakerArtwork.RasterBounds;
                return true;
            }

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

        private static Type _mapViewType;
        private static PropertyInfo _coordinateRotationProperty;

        // Returns true when the layer rotation was changed. The desired value is always written when
        // it differs, so a style refresh back to native artwork restores the map definition's own
        // orientation without needing to remember a previous state.
        private static bool ApplyArtworkRotation(Component layer, RectTransform rect, StyleResolution resolution)
        {
            var view = FindMapView(layer);
            if (view == null)
                return false;

            _coordinateRotationProperty ??= AccessTools.Property(view.GetType(), "CoordinateRotation");
            if (!(_coordinateRotationProperty?.GetValue(view) is float mapRotation))
                return false;

            var artRotation = mapRotation;
            if (TryGetArtworkRotation(resolution.ResolvedPath, out var authored))
                artRotation = authored;

            // Counter-rotate by the artwork's own orientation rather than the map definition's.
            // For natively authored artwork the two are equal and this is the value DynamicMaps
            // already applied when it created the layer; for the swapped-in Labyrinth raster it
            // flips the layer 180 degrees so the picture matches the marker coordinates.
            var desired = -artRotation;

            var current = rect.localEulerAngles.z;
            if (Mathf.Abs(Mathf.DeltaAngle(current, desired)) < 0.01f)
                return false;

            rect.localRotation = Quaternion.Euler(0f, 0f, desired);
            return true;
        }

        private static Component FindMapView(Component layer)
        {
            if (_mapViewType == null)
            {
                _mapViewType = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
                if (_mapViewType == null)
                    return null;
            }

            // MapLayer sits under MapLayerContainer under MapView. Walk up rather than using
            // GetComponentInParent so hidden (inactive) floors are handled the same way.
            var transform = layer.transform;
            for (var depth = 0; depth < 8 && transform != null; depth++)
            {
                var view = transform.GetComponent(_mapViewType);
                if (view != null)
                    return view;
                transform = transform.parent;
            }

            return null;
        }

        private static bool TryGetArtworkRotation(string resolvedPath, out float rotation)
        {
            rotation = 0f;
            if (string.IsNullOrWhiteSpace(resolvedPath))
                return false;

            var p = Normalize(resolvedPath);
            foreach (var pair in ArtworkRotations)
            {
                if (p.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    rotation = pair.Value;
                    return true;
                }
            }

            return false;
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

            // Must run before the extent calculation below: a non-180 correction would change the
            // rotated footprint, and the layer rectangle has to be sized from the final angle.
            var rotationChanged = ApplyArtworkRotation(component, rect, resolution);

            var size = desired.Size;
            var angle = rect.localEulerAngles.z * Mathf.Deg2Rad;
            var c = Mathf.Abs(Mathf.Cos(angle));
            var s = Mathf.Abs(Mathf.Sin(angle));
            var rotated = new Vector2(size.x * c + size.y * s, size.x * s + size.y * c);

            var changed = rotationChanged ||
                          (rect.sizeDelta - rotated).sqrMagnitude > 0.0001f ||
                          (rect.anchoredPosition - desired.Midpoint).sqrMagnitude > 0.0001f;

            if (changed)
            {
                rect.sizeDelta = rotated;
                rect.anchoredPosition = desired.Midpoint;
            }

            if (changed && Plugin.DebugEnabled)
            {
                var key = RasterSpritePatch.GetLayerImagePath(mapLayer) + "|" + resolution.Selected + "|" + desired + "|" + rect.localEulerAngles.z.ToString("0.###");
                if (Logged.Add(key))
                    Plugin.Debug($"Artwork calibrated: map={resolution.Map}, style={resolution.Selected}, bounds={desired}, rotation={rect.localEulerAngles.z:0.###}");
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
                var icebreaker = false;
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
                    icebreaker |= resolution.Map == MapStyleKey.Icebreaker;
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
                SetBoundsObject(mapBounds, icebreaker ? IcebreakerArtwork.ViewBounds : new Bounds2D(min.x, min.y, max.x, max.y));
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