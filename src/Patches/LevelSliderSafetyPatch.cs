using System;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    // Keep DynamicMaps from accepting a floor value that does not exist in the currently-loaded
    // MapView. Fail open if DynamicMaps internals ever change so other maps cannot be broken by
    // this compatibility guard.
    [HarmonyPatch]
    internal static class MapViewLevelGuardPatch
    {
        private static FieldInfo _layersField;
        private static PropertyInfo _levelProperty;
        private static int _lastRejectedLevel = int.MinValue;
        private static int _lastRejectedFrame = -1000;

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "SelectTopLevel", new[] { typeof(int) })
                ?? throw new MissingMethodException(type.FullName, "SelectTopLevel");
        }

        private static bool Prefix(object __instance, int level)
        {
            try
            {
                if (__instance == null)
                    return true;

                _layersField ??= AccessTools.Field(__instance.GetType(), "_layers");
                if (!(_layersField?.GetValue(__instance) is IList layers) || layers.Count == 0)
                    return true;

                for (var i = 0; i < layers.Count; i++)
                {
                    var layer = layers[i];
                    if (layer == null) continue;
                    _levelProperty ??= AccessTools.Property(layer.GetType(), "Level");
                    if (_levelProperty?.GetValue(layer) is int v && v == level)
                        return true;
                }

                if (level != _lastRejectedLevel || Time.frameCount - _lastRejectedFrame >= 30)
                {
                    _lastRejectedLevel = level;
                    _lastRejectedFrame = Time.frameCount;
                    Plugin.Debug($"Ignored invalid DynamicMaps floor selection: level={level}, availableLayers={layers.Count}.");
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Debug("Floor-selection guard failed open: " + e.Message);
                return true;
            }
        }
    }

    internal sealed class IcebreakerSliderState
    {
        internal bool IsIcebreaker;
    }

    // Icebreaker has 16 real levels (0..15). DynamicMaps' level selector works correctly for
    // ordinary maps, but the EFT Scrollbar/control combination can keep short-map state when this
    // unusually tall floor list is loaded. Normalize only Icebreaker and leave every other map on
    // DynamicMaps' original behavior.
    [HarmonyPatch]
    internal static class LevelSliderIcebreakerLoadPatch
    {
        private static readonly ConditionalWeakTable<object, IcebreakerSliderState> States =
            new ConditionalWeakTable<object, IcebreakerSliderState>();

        private static FieldInfo _levelsField;
        private static FieldInfo _scrollbarField;
        private static FieldInfo _selectedLevelBackingField;
        private static PropertyInfo _selectedLevelProperty;
        private static PropertyInfo _scrollbarSizeProperty;
        private static PropertyInfo _scrollbarStepsProperty;
        private static PropertyInfo _scrollbarValueProperty;
        private static MethodInfo _scrollbarSetValueWithoutNotifyMethod;
        private static PropertyInfo _displayNameProperty;
        private static PropertyInfo _nameProperty;
        private static PropertyInfo _layersProperty;
        private static PropertyInfo _imagePathProperty;
        private static bool _activeMapIsIcebreaker;
        private static bool _mapContextKnown;

        internal static bool IsIcebreaker(object slider)
            => slider != null && States.TryGetValue(slider, out var state) && state.IsIcebreaker;

        internal static void SetActiveMap(object mapDef)
        {
            var active = IsIcebreakerMap(mapDef);
            _activeMapIsIcebreaker = active;
            _mapContextKnown = true;
            if (active)
                Plugin.Debug("Icebreaker slider context armed from MapView.LoadMap.");
        }

        internal static bool EnsureIcebreakerContext(object slider)
        {
            if (slider == null)
                return false;

            var state = States.GetValue(slider, _ => new IcebreakerSliderState());
            var location = DynamicMapsBridge.GetCurrentLocationOrNull();
            var locationIsIcebreaker = !string.IsNullOrEmpty(location) &&
                                       location.Equals("icebreaker", StringComparison.OrdinalIgnoreCase);

            // MapView.LoadMap is authoritative when known. The location fallback covers in-raid
            // setup ordering before the map screen has finished loading. Recompute every time so
            // a slider instance reused after leaving Icebreaker never stays incorrectly armed.
            state.IsIcebreaker = _mapContextKnown ? _activeMapIsIcebreaker : locationIsIcebreaker;
            if (!state.IsIcebreaker && locationIsIcebreaker)
                state.IsIcebreaker = true;

            if (!state.IsIcebreaker)
                return false;

            NormalizeLiveLevels(slider);
            return true;
        }

        private static void NormalizeLiveLevels(object slider)
        {
            try
            {
                _levelsField ??= AccessTools.Field(slider.GetType(), "_levels");
                if (!(_levelsField?.GetValue(slider) is IList levels))
                    return;

                var alreadyCorrect = levels.Count == 16;
                if (alreadyCorrect)
                {
                    for (var i = 0; i < 16; i++)
                    {
                        if (!(levels[i] is int value) || value != i)
                        {
                            alreadyCorrect = false;
                            break;
                        }
                    }
                }

                if (alreadyCorrect)
                    return;

                var oldCount = levels.Count;
                levels.Clear();
                for (var level = 0; level <= 15; level++)
                    levels.Add(level);
                Plugin.Debug($"Icebreaker live slider levels repaired: oldCount={oldCount}, newCount=16.");
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker live level-list repair failed: " + e.Message);
            }
        }

        internal static int GetSelectedLevel(object slider)
        {
            if (slider == null)
                return int.MinValue;

            try
            {
                // DynamicMaps 1.2.1 uses SelectedLevel as an auto-property. An earlier implementation incorrectly
                // searched for a private _selectedLevel field, which does not exist in the DLL.
                _selectedLevelProperty ??= AccessTools.Property(slider.GetType(), "SelectedLevel");
                if (_selectedLevelProperty?.CanRead == true &&
                    _selectedLevelProperty.GetValue(slider) is int propertyValue)
                    return propertyValue;

                // Auto-property backing-field fallback if the getter ever becomes inaccessible.
                _selectedLevelBackingField ??= AccessTools.Field(slider.GetType(), "<SelectedLevel>k__BackingField");
                if (_selectedLevelBackingField?.GetValue(slider) is int fieldValue)
                    return fieldValue;
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker SelectedLevel read failed: " + e.Message);
            }

            return int.MinValue;
        }

        internal static bool InvokeSelection(object slider, int level)
        {
            if (slider == null)
                return false;

            try
            {
                var eventField = AccessTools.Field(slider.GetType(), "OnLevelSelectedBySlider");
                if (eventField?.GetValue(slider) is Action<int> callback)
                {
                    callback(level);
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker level-selection callback failed: " + e.Message);
            }

            return false;
        }

        internal static void SyncIcebreakerScrollbar(object slider, int selectedLevel)
        {
            if (slider == null || !EnsureIcebreakerContext(slider))
                return;

            try
            {
                selectedLevel = Mathf.Clamp(selectedLevel, 0, 15);

                _scrollbarField ??= AccessTools.Field(slider.GetType(), "_scrollbar");
                var scrollbar = _scrollbarField?.GetValue(slider);
                if (scrollbar == null)
                    return;

                var scrollbarType = scrollbar.GetType();
                _scrollbarSizeProperty ??= AccessTools.Property(scrollbarType, "size");
                _scrollbarStepsProperty ??= AccessTools.Property(scrollbarType, "numberOfSteps");
                _scrollbarValueProperty ??= AccessTools.Property(scrollbarType, "value");
                _scrollbarSetValueWithoutNotifyMethod ??=
                    AccessTools.Method(scrollbarType, "SetValueWithoutNotify", new[] { typeof(float) });

                _scrollbarStepsProperty?.SetValue(scrollbar, 16);
                _scrollbarSizeProperty?.SetValue(scrollbar, 0.10f);

                // Unity Scrollbar.value is normalized 0..1. Explicitly position the handle rather
                // than trusting DynamicMaps' normal short-map setter path. This keeps the UI and selected floor synchronized
                // change: Level 3 must sit at 3/15 of the track, not at/near the top.
                var normalized = selectedLevel / 15f;
                if (_scrollbarSetValueWithoutNotifyMethod != null)
                    _scrollbarSetValueWithoutNotifyMethod.Invoke(scrollbar, new object[] { normalized });
                else
                    _scrollbarValueProperty?.SetValue(scrollbar, normalized);
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker scrollbar synchronization failed: " + e.Message);
            }
        }

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.LevelSelectSlider")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.LevelSelectSlider not found");
            var mapDef = AccessTools.TypeByName("DynamicMaps.Data.MapDef")
                ?? throw new MissingMemberException("DynamicMaps.Data.MapDef not found");
            return AccessTools.Method(type, "OnLoadMap", new[] { mapDef, typeof(int) })
                ?? throw new MissingMethodException(type.FullName, "OnLoadMap");
        }

        private static void Postfix(object __instance, object __0, int __1)
        {
            try
            {
                if (__instance == null)
                    return;

                _levelsField ??= AccessTools.Field(__instance.GetType(), "_levels");
                if (!(_levelsField?.GetValue(__instance) is IList levels))
                    return;

                var state = States.GetValue(__instance, _ => new IcebreakerSliderState());
                state.IsIcebreaker = _activeMapIsIcebreaker || IsIcebreakerMap(__0) || LooksLikeIcebreakerLevels(levels);
                if (!state.IsIcebreaker)
                    return;

                NormalizeLiveLevels(__instance);

                _selectedLevelProperty ??= AccessTools.Property(__instance.GetType(), "SelectedLevel");
                var selected = Mathf.Clamp(__1, 0, 15);
                _selectedLevelProperty?.SetValue(__instance, selected);
                SyncIcebreakerScrollbar(__instance, selected);

                Plugin.Debug($"Icebreaker level slider normalized: levels=0..15, selected={selected}, handle={selected / 15f:F3}.");
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker level-slider normalization skipped: " + e.Message);
            }
        }

        private static bool IsIcebreakerMap(object mapDef)
        {
            if (mapDef == null)
                return false;

            try
            {
                _displayNameProperty ??= AccessTools.Property(mapDef.GetType(), "DisplayName");
                var displayName = _displayNameProperty?.GetValue(mapDef) as string;
                if (ContainsIcebreaker(displayName))
                    return true;

                _nameProperty ??= mapDef.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var name = _nameProperty?.GetValue(mapDef) as string;
                if (ContainsIcebreaker(name))
                    return true;

                // Some MapDef revisions do not expose a useful Name/DisplayName to reflection.
                // The extension-owned Icebreaker definition is still unambiguous from its layer paths.
                _layersProperty ??= AccessTools.Property(mapDef.GetType(), "Layers");
                if (_layersProperty?.GetValue(mapDef) is IDictionary layers)
                {
                    foreach (DictionaryEntry pair in layers)
                    {
                        var layerDef = pair.Value;
                        if (layerDef == null) continue;
                        _imagePathProperty ??= AccessTools.Property(layerDef.GetType(), "ImagePath");
                        var imagePath = _imagePathProperty?.GetValue(layerDef) as string;
                        if (ContainsIcebreaker(imagePath))
                            return true;
                    }
                }
            }
            catch
            {
                // Fall through to the already-populated level-shape/current-map checks.
            }

            return false;
        }

        private static bool LooksLikeIcebreakerLevels(IList levels)
        {
            if (levels == null || levels.Count != 16)
                return false;

            try
            {
                for (var i = 0; i < 16; i++)
                {
                    if (!(levels[i] is int value) || value != i)
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsIcebreaker(string value)
            => !string.IsNullOrEmpty(value) &&
               value.IndexOf("Icebreaker", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // MapView.LoadMap is the reliable map-switch hook already used elsewhere by DMExt.
    // Arm/disarm Icebreaker slider handling here instead of relying only on LevelSelectSlider.OnLoadMap,
    // whose live _levels list can already be truncated by the time the compatibility code sees it.
    [HarmonyPatch]
    internal static class IcebreakerMapContextPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Components.MapView")
                ?? throw new MissingMemberException("DynamicMaps.UI.Components.MapView not found");
            return AccessTools.Method(type, "LoadMap")
                ?? throw new MissingMethodException(type.FullName, "LoadMap");
        }

        private static void Prefix(object __0)
        {
            LevelSliderIcebreakerLoadPatch.SetActiveMap(__0);
        }
    }

    // DynamicMaps updates SelectedLevel both from manual controls and from MapView.OnLevelSelected.
    // After its normal setter runs, pin the Icebreaker scrollbar handle to the true normalized
    // 0..15 position. This prevents Level 3 from visually reaching the top of the track.
    [HarmonyPatch]
    internal static class IcebreakerSelectedLevelSetterPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.LevelSelectSlider")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.LevelSelectSlider not found");
            return AccessTools.PropertySetter(type, "SelectedLevel")
                ?? throw new MissingMethodException(type.FullName, "set_SelectedLevel");
        }

        private static void Postfix(object __instance, int value)
        {
            if (!LevelSliderIcebreakerLoadPatch.EnsureIcebreakerContext(__instance))
                return;

            LevelSliderIcebreakerLoadPatch.SyncIcebreakerScrollbar(__instance, value);
        }
    }

    [HarmonyPatch]
    internal static class IcebreakerScrollbarChangedPatch
    {
        private static int _lastLoggedLevel = int.MinValue;

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.LevelSelectSlider")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.LevelSelectSlider not found");
            return AccessTools.Method(type, "OnScrollbarChanged", new[] { typeof(float) })
                ?? throw new MissingMethodException(type.FullName, "OnScrollbarChanged");
        }

        private static bool Prefix(object __instance, float __0)
        {
            if (!LevelSliderIcebreakerLoadPatch.EnsureIcebreakerContext(__instance))
                return true;

            try
            {
                var normalized = Mathf.Clamp01(__0);
                var level = Mathf.Clamp(Mathf.RoundToInt(normalized * 15f), 0, 15);
                var selected = LevelSliderIcebreakerLoadPatch.GetSelectedLevel(__instance);

                if (selected == int.MinValue || selected != level)
                    LevelSliderIcebreakerLoadPatch.InvokeSelection(__instance, level);

                // Keep the handle exactly on the discrete floor after rounding.
                LevelSliderIcebreakerLoadPatch.SyncIcebreakerScrollbar(__instance, level);

                if (level != _lastLoggedLevel)
                {
                    _lastLoggedLevel = level;
                    Plugin.Debug($"Icebreaker slider drag selected level {level} (value={normalized:F3}).");
                }
                return false;
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker slider drag patch failed open: " + e.Message);
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class IcebreakerSliderButtonPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.LevelSelectSlider")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.LevelSelectSlider not found");
            return AccessTools.Method(type, "ChangeLevelBy", new[] { typeof(int) })
                ?? throw new MissingMethodException(type.FullName, "ChangeLevelBy");
        }

        private static bool Prefix(object __instance, int __0)
        {
            if (!LevelSliderIcebreakerLoadPatch.EnsureIcebreakerContext(__instance))
                return true;

            try
            {
                var selected = LevelSliderIcebreakerLoadPatch.GetSelectedLevel(__instance);
                if (selected == int.MinValue)
                    return true;

                var next = Mathf.Clamp(selected + __0, 0, 15);
                if (next != selected)
                    LevelSliderIcebreakerLoadPatch.InvokeSelection(__instance, next);

                LevelSliderIcebreakerLoadPatch.SyncIcebreakerScrollbar(__instance, next);
                return false;
            }
            catch (Exception e)
            {
                Plugin.Debug("Icebreaker +/- level patch failed open: " + e.Message);
                return true;
            }
        }
    }
}