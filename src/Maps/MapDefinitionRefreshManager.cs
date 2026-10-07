using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Artwork can carry different authoritative world bounds from the SVG it replaces. A live F12
    // change therefore needs one controlled geometry update so DynamicMaps recalculates map root
    // size, min/max zoom and clamping from the newly calibrated bounds. This is user-triggered
    // configuration work, never minimap-open work.
    internal static class MapDefinitionRefreshManager
    {
        private static MethodInfo _killTweenMethod;
        private static Type _viewType;
        private static Type _screenType;
        private static FieldInfo _dropdownField;
        private static MethodInfo _filterMethod;
        private static MethodInfo _loadFirstMethod;
        private static PropertyInfo _currentMapDefProperty;
        private static FieldInfo _maskTransformField;

        private sealed class ViewportState
        {
            internal object MainZoom;
            internal object MiniZoom;
            internal Vector2 MainPosition;
            internal Vector2 VisiblePosition;
            internal bool IsMini;
            internal float DisplayedZoom;
        }

        private static ViewportState CaptureViewport(object candidate)
        {
            if (!(candidate is Component view) || !(view.transform is RectTransform rect))
                return null;
            var type = candidate.GetType();
            return new ViewportState
            {
                MainZoom = AccessTools.Property(type, "ZoomMain").GetValue(candidate),
                MiniZoom = AccessTools.Property(type, "ZoomMini").GetValue(candidate),
                MainPosition = (Vector2)AccessTools.Property(type, "MainMapPos").GetValue(candidate),
                VisiblePosition = rect.anchoredPosition,
                IsMini = (bool)AccessTools.Property(type, "IsMiniMapActive").GetValue(candidate),
                DisplayedZoom = rect.localScale.x
            };
        }

        private static void RestoreViewport(object candidate, ViewportState state)
        {
            if (candidate == null || state == null) return;
            var type = candidate.GetType();
            StopViewTweens(candidate);
            var zoom = AccessTools.Method(type, "SetMapZoom");
            zoom.Invoke(candidate, new object[] { state.MainZoom, 0f, true, false });
            zoom.Invoke(candidate, new object[] { state.MiniZoom, 0f, false, true });
            var mainZoom = (float)AccessTools.Property(type, "ZoomMain").GetValue(candidate);
            var miniZoom = (float)AccessTools.Property(type, "ZoomMini").GetValue(candidate);
            var activeZoom = state.IsMini ? miniZoom : mainZoom;
            var visiblePosition = state.VisiblePosition * ZoomRatio(activeZoom, state.DisplayedZoom);
            // Public zoom setters schedule DOTweens. Cancel them without completing stale
            // destinations, then commit the actual transform and marker scales immediately.
            StopViewTweens(candidate);
            var rect = (RectTransform)((Component)candidate).transform;
            rect.localScale = Vector3.one * activeZoom;
            rect.anchoredPosition = visiblePosition;
            var mainPosition = state.IsMini
                ? state.MainPosition * ZoomRatio(mainZoom, (float)state.MainZoom)
                : visiblePosition;
            AccessTools.Property(type, "MainMapPos").SetValue(candidate, mainPosition);
            AccessTools.Field(type, "_immediateMapAnchor")?.SetValue(candidate, visiblePosition);
            foreach (Transform child in MarkerTransforms(candidate))
                child.localScale = Vector3.one / activeZoom;
            SynchronizeScreen(candidate, mainPosition);
            Plugin.Debug($"Viewport committed: mini={state.IsMini}, mainZoom={mainZoom:F4}, miniZoom={miniZoom:F4}, displayed={rect.localScale.x:F4}, position={visiblePosition}");
        }

        private static IEnumerable MarkerTransforms(object candidate)
        {
            var type = candidate.GetType();
            foreach (var name in new[] { "_markers", "_labels" })
            {
                if (!(AccessTools.Field(type, name)?.GetValue(candidate) is IEnumerable entries)) continue;
                foreach (var entry in entries)
                    if (entry is Component component && component != null)
                        yield return component.transform;
            }
        }

        private static void StopViewTweens(object candidate)
        {
            _killTweenMethod ??= AccessTools.Method(AccessTools.TypeByName("DG.Tweening.DOTween"),
                "Kill", new[] { typeof(object), typeof(bool) });
            if (_killTweenMethod == null) throw new MissingMethodException("DOTween.Kill(object,bool)");
            _killTweenMethod.Invoke(null, new object[] { ((Component)candidate).transform, false });
            foreach (var child in MarkerTransforms(candidate))
                _killTweenMethod.Invoke(null, new object[] { child, false });
        }

        private static void SynchronizeScreen(object mapView, Vector2 mainPosition)
        {
            var screenType = (_screenType ??= AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen"));
            if (screenType == null) return;
            var viewField = AccessTools.Field(screenType, "_mapView");
            foreach (var screen in Resources.FindObjectsOfTypeAll(screenType))
                if (ReferenceEquals(viewField?.GetValue(screen), mapView))
                {
                    AccessTools.Field(screenType, "_targetZoom")?.SetValue(screen, 0f);
                    AccessTools.Field(screenType, "_savedMainMapPos")?.SetValue(screen, mainPosition);
                }
        }

        private static float ZoomRatio(float current, float previous)
            => previous > 0f ? current / previous : 1f;

        internal static void OnArtworkSettingChanged()
        {
            if (Plugin.Instance != null) RefreshArtworkNow();
        }

        private static void RefreshArtworkNow()
        {
            try
            {
                var realLocationId = DynamicMapsBridge.GetCurrentLocationOrNull();
                var reserveLocationId = realLocationId;

                // Character -> Maps has no live EFT location ID. If Reserve is the map currently
                // displayed there, derive the canonical ID from the active DynamicMaps MapView so
                // F12 can still swap between native Reserve and the calibrated Satellite MapDef.
                if (string.IsNullOrWhiteSpace(reserveLocationId))
                    reserveLocationId = TryGetActiveReserveLocationId();

                var normalized = reserveLocationId?.Trim().ToLowerInvariant();
                if (normalized == "rezervbase" || normalized == "reserve")
                {
                    if (TryRefreshReserveViaDropdown(reserveLocationId))
                        return;
                }

                ReloadActiveMapViews();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not refresh map after artwork change: {e.Message}");
            }

        }


        private static string TryGetActiveReserveLocationId()
        {
            try
            {
                var viewType = (_viewType ??= AccessTools.TypeByName("DynamicMaps.UI.Components.MapView"));
                if (viewType == null)
                    return null;

                _currentMapDefProperty ??= AccessTools.Property(viewType, "CurrentMapDef");
                var views = Resources.FindObjectsOfTypeAll(viewType);
                if (views == null)
                    return null;

                foreach (var candidate in views)
                {
                    if (!(candidate is Component view) || !view.gameObject.activeInHierarchy)
                        continue;

                    var mapDef = _currentMapDefProperty?.GetValue(candidate);
                    if (mapDef == null)
                        continue;

                    var mapType = mapDef.GetType();
                    var names = AccessTools.Property(mapType, "MapInternalNames")?.GetValue(mapDef) as IEnumerable;
                    if (names != null)
                    {
                        foreach (var value in names)
                        {
                            var name = value as string;
                            if (string.Equals(name, "RezervBase", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, "Reserve", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, MapAliasPatch.ReserveSatelliteAlias, StringComparison.OrdinalIgnoreCase))
                                return "RezervBase";
                        }
                    }

                    var display = AccessTools.Property(mapType, "DisplayName")?.GetValue(mapDef) as string;
                    if (!string.IsNullOrWhiteSpace(display) &&
                        display.StartsWith("Reserve", StringComparison.OrdinalIgnoreCase))
                        return "RezervBase";
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Could not identify active Reserve map: " + e.Message);
            }

            return null;
        }

        private static bool TryRefreshReserveViaDropdown(string realLocationId)
        {
            if (string.IsNullOrWhiteSpace(realLocationId))
                return false;

            var screenType = (_screenType ??= AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen"));
            if (screenType == null)
                return false;

            _dropdownField ??= AccessTools.Field(screenType, "_mapSelectDropdown");
            var screens = Resources.FindObjectsOfTypeAll(screenType);
            if (screens == null || screens.Length == 0)
                return false;

            foreach (var candidate in screens)
            {
                if (!(candidate is Component screen) || !screen.gameObject.activeInHierarchy)
                    continue;

                var dropdown = _dropdownField?.GetValue(candidate);
                if (dropdown == null)
                    continue;

                var dropdownType = dropdown.GetType();
                _filterMethod ??= AccessTools.Method(dropdownType, "FilterByInternalMapName", new[] { typeof(string) });
                _loadFirstMethod ??= AccessTools.Method(dropdownType, "LoadFirstAvailableMap");
                if (_filterMethod == null || _loadFirstMethod == null)
                    continue;

                var mapView = AccessTools.Field(screenType, "_mapView")?.GetValue(candidate);
                var viewport = CaptureViewport(mapView);
                _filterMethod.Invoke(dropdown, new object[] { realLocationId });
                _loadFirstMethod.Invoke(dropdown, null);
                RestoreViewport(mapView, viewport);
                Plugin.Debug($"Reserve MapDef refreshed live after artwork change: {realLocationId}.");
                return true;
            }

            return false;
        }

        private static void ReloadActiveMapViews()
        {
            var viewType = (_viewType ??= AccessTools.TypeByName("DynamicMaps.UI.Components.MapView"));
            if (viewType == null)
                return;

            _currentMapDefProperty ??= AccessTools.Property(viewType, "CurrentMapDef");
            _maskTransformField ??= AccessTools.Field(viewType, "_maskTransform");
            if (_currentMapDefProperty == null || _maskTransformField == null)
                return;

            var views = Resources.FindObjectsOfTypeAll(viewType);
            if (views == null)
                return;

            var refreshed = 0;
            foreach (var candidate in views)
            {
                if (!(candidate is Component view) || !view.gameObject.activeInHierarchy)
                    continue;

                var mapDef = _currentMapDefProperty.GetValue(candidate);
                var mask = _maskTransformField.GetValue(candidate);
                if (mapDef == null || mask == null)
                    continue;

                RasterCalibrationManager.PrepareMapDef(mapDef);
                // UnloadMap destroys every marker, including the live player and provider-owned
                // trail. LoadMap only restores static markers. Update geometry in place instead.
                var bounds = AccessTools.Property(mapDef.GetType(), "Bounds")?.GetValue(mapDef);
                if (bounds == null || !(view.transform is RectTransform rect))
                    continue;
                var min = (Vector2)AccessTools.Property(bounds.GetType(), "Min").GetValue(bounds);
                var max = (Vector2)AccessTools.Property(bounds.GetType(), "Max").GetValue(bounds);
                var angle = (float)AccessTools.Property(viewType, "CoordinateRotation").GetValue(candidate) * Mathf.Deg2Rad;
                var c = Mathf.Abs(Mathf.Cos(angle));
                var s = Mathf.Abs(Mathf.Sin(angle));
                var size = max - min;
                var desiredSize = new Vector2(size.x * c + size.y * s, size.x * s + size.y * c);
                var viewport = CaptureViewport(candidate);
                if ((rect.sizeDelta - desiredSize).sqrMagnitude > 0.0001f)
                {
                    rect.sizeDelta = desiredSize;
                    var maskRect = (RectTransform)mask;
                    var minScaler = (float)AccessTools.Field(viewType, "_zoomMinScaler").GetValue(null);
                    var maxScaler = (float)AccessTools.Field(viewType, "_zoomMaxScaler").GetValue(null);
                    var minimum = Mathf.Min(maskRect.sizeDelta.x / desiredSize.x,
                        maskRect.sizeDelta.y / desiredSize.y) / minScaler;
                    // Match DynamicMaps' zoom limits without SetMinMaxZoom's reset/centering.
                    AccessTools.Property(viewType, "ZoomMin").SetValue(candidate, minimum);
                    AccessTools.Property(viewType, "ZoomMax").SetValue(candidate, minimum * maxScaler);
                }
                RestoreViewport(candidate, viewport);
                refreshed++;
            }

            if (refreshed > 0)
                Plugin.Debug($"Updated {refreshed} DynamicMaps view(s) in place after artwork/bounds change; live markers retained.");
        }
    }
}
