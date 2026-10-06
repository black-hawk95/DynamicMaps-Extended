using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Artwork can carry different authoritative world bounds from the SVG it replaces. A live F12
    // change therefore needs one controlled MapView rebuild so DynamicMaps recalculates map root
    // size, min/max zoom and clamping from the newly calibrated bounds. This is user-triggered
    // configuration work, never minimap-open work.
    internal static class MapDefinitionRefreshManager
    {
        private static Coroutine _pending;
        private static FieldInfo _dropdownField;
        private static MethodInfo _filterMethod;
        private static MethodInfo _loadFirstMethod;
        private static PropertyInfo _currentMapDefProperty;
        private static FieldInfo _maskTransformField;
        private static MethodInfo _unloadMapMethod;
        private static MethodInfo _loadMapMethod;

        internal static void OnArtworkSettingChanged()
        {
            if (Plugin.Instance == null)
                return;

            if (_pending != null)
                Plugin.Instance.StopCoroutine(_pending);
            _pending = Plugin.Instance.StartCoroutine(RefreshArtworkNextFrame());
        }

        internal static void Shutdown()
        {
            if (_pending != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_pending);
            _pending = null;
        }

        private static IEnumerator RefreshArtworkNextFrame()
        {
            yield return null;

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
                        yield break;
                }

                ReloadActiveMapViews();
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not refresh map after artwork change: {e.Message}");
            }
            finally
            {
                _pending = null;
            }
        }


        private static string TryGetActiveReserveLocationId()
        {
            try
            {
                var viewType = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
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

            var screenType = AccessTools.TypeByName("DynamicMaps.UI.ModdedMapScreen");
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

                _filterMethod.Invoke(dropdown, new object[] { realLocationId });
                _loadFirstMethod.Invoke(dropdown, null);
                Plugin.Debug($"Reserve MapDef refreshed live after artwork change: {realLocationId}.");
                return true;
            }

            return false;
        }

        private static void ReloadActiveMapViews()
        {
            var viewType = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            if (viewType == null)
                return;

            _currentMapDefProperty ??= AccessTools.Property(viewType, "CurrentMapDef");
            _maskTransformField ??= AccessTools.Field(viewType, "_maskTransform");
            _unloadMapMethod ??= AccessTools.Method(viewType, "UnloadMap");
            _loadMapMethod ??= AccessTools.Method(viewType, "LoadMap");
            if (_currentMapDefProperty == null || _maskTransformField == null || _unloadMapMethod == null || _loadMapMethod == null)
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
                _unloadMapMethod.Invoke(candidate, null);
                _loadMapMethod.Invoke(candidate, new[] { mapDef, mask });
                refreshed++;
            }

            if (refreshed > 0)
                Plugin.Debug($"Rebuilt {refreshed} active DynamicMaps view(s) after artwork/bounds change.");
        }
    }
}