using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMapsExtended
{
    // Sprite changes alone do not re-register clipping or invalidate cached stencil state.
    // Commit once in LateUpdate, after F12's geometry/viewport updates have finished.
    // The component belongs to the view so a request survives hiding the map until it reopens.
    internal sealed class ArtworkDisplayRefresh : MonoBehaviour
    {
        private static Type _viewType;
        private Component _view;
        private bool _pending;
        private PropertyInfo _selectedLevel;
        private PropertyInfo _layerContainer;
        private MethodInfo _selectTopLevel;

        internal static void Request(object mapLayer)
        {
            if (!(mapLayer is Component layer) || layer == null) return;
            _viewType ??= AccessTools.TypeByName("DynamicMaps.UI.Components.MapView");
            if (_viewType == null) return;

            // Walk explicitly: inactive ancestors must retain a pending request too.
            for (var parent = layer.transform; parent != null; parent = parent.parent)
            {
                var view = parent.GetComponent(_viewType);
                if (view == null) continue;
                var refresh = view.GetComponent<ArtworkDisplayRefresh>() ??
                    view.gameObject.AddComponent<ArtworkDisplayRefresh>();
                refresh._view = view;
                refresh._pending = true;
                return;
            }
        }

        private void LateUpdate()
        {
            if (!_pending || _view == null || !_view.gameObject.activeInHierarchy) return;
            // Multiple floor/image completions in one frame share one refresh. Clear before
            // invoking native callbacks so any genuinely new request survives to the next frame.
            _pending = false;
            try
            {
                var type = _view.GetType();
                _selectedLevel ??= AccessTools.Property(type, "SelectedLevel");
                _layerContainer ??= AccessTools.Property(type, "MapLayerContainer");
                _selectTopLevel ??= AccessTools.Method(type, "SelectTopLevel", new[] { typeof(int) });
                if (_selectedLevel?.GetValue(_view) is int level)
                    _selectTopLevel?.Invoke(_view, new object[] { level });

                // Keep native mask rules; never force visibility by disabling masks or culling.
                if (!(_layerContainer?.GetValue(_view) is GameObject container)) return;
                var refreshed = 0;
                foreach (var graphic in container.GetComponentsInChildren<MaskableGraphic>(true))
                {
                    if (!graphic.isActiveAndEnabled) continue;
                    graphic.RecalculateClipping();
                    graphic.RecalculateMasking();
                    graphic.SetAllDirty();
                    refreshed++;
                }
                Plugin.Debug($"Artwork display refreshed: floor={_selectedLevel?.GetValue(_view)}, graphics={refreshed}; native floor visibility and masks reapplied.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("Artwork display refresh failed: " + e.Message);
            }
        }
    }
}
