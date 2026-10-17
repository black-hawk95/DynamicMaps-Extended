using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DynamicMapsExtended
{
    // SVGImage normalizes vertices by sprite.bounds (drawn geometry), although the
    // calibrated ImageBounds describe sprite.rect (the complete SVG viewBox).
    // Restore the viewBox mapping after SVGImage builds its mesh. This preserves
    // each deck's empty margins without touching world coordinates or map bounds.
    internal sealed class IcebreakerSvgViewport : BaseMeshEffect
    {
        private PropertyInfo _spriteProperty;
        private PropertyInfo _statusProperty;
        internal object MapLayer;
        internal string SourcePath;

        internal static float RestoreAxis(float rendered, float offset, float geometrySize, float canvasSize)
            => (rendered + offset) * (geometrySize / canvasSize) - offset;

        public override void ModifyMesh(VertexHelper vertices)
        {
            if (!IsActive() || graphic == null || vertices.currentVertCount == 0)
                return;

            _spriteProperty ??= AccessTools.Property(graphic.GetType(), "sprite");
            if (!(_spriteProperty?.GetValue(graphic) is Sprite sprite))
                return;

            // Inspect the sprite actually displayed, including during asynchronous style changes.
            // Raster previews and tile children must never receive SVG viewBox compensation.
            if (sprite.name == null || sprite.name.IndexOf("Icebreaker-Deck_", StringComparison.OrdinalIgnoreCase) < 0)
                return;
            if (MapLayer != null)
            {
                _statusProperty ??= AccessTools.Property(MapLayer.GetType(), "Status");
                if (IcebreakerArtwork.IsDuplicateUnderlay(SourcePath, _statusProperty?.GetValue(MapLayer)?.ToString()))
                {
                    vertices.Clear();
                    return;
                }
            }

            var rect = sprite.rect;
            var ppu = sprite.pixelsPerUnit;
            if (rect.width <= 0 || rect.height <= 0 || ppu <= 0)
                return;

            var canvasSize = rect.size / ppu;
            var geometrySize = sprite.bounds.size;
            if (geometrySize.x <= 0 || geometrySize.y <= 0)
                return;

            var drawing = graphic.GetPixelAdjustedRect().size;
            var offset = Vector2.Scale(graphic.rectTransform.pivot - sprite.pivot / rect.size, drawing);
            var vertex = new UIVertex();
            for (var i = 0; i < vertices.currentVertCount; i++)
            {
                vertices.PopulateUIVertex(ref vertex, i);
                var position = vertex.position;
                position.x = RestoreAxis(position.x, offset.x, geometrySize.x, canvasSize.x);
                position.y = RestoreAxis(position.y, offset.y, geometrySize.y, canvasSize.y);
                vertex.position = position;
                vertices.SetUIVertex(vertex, i);
            }
        }
    }

    [HarmonyPatch]
    internal static class IcebreakerSvgViewportPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.Method(AccessTools.TypeByName("DynamicMaps.UI.Components.MapLayer"), "Create")
               ?? throw new MissingMethodException("DynamicMaps MapLayer.Create");

        private static void Postfix(object __2, object __result)
        {
            if (!(__result is Component layer) || __2 == null)
                return;
            var path = AccessTools.Property(__2.GetType(), "ImagePath")?.GetValue(__2) as string;
            var normalized = path?.Replace('\\', '/');
            if (normalized == null ||
                normalized.IndexOf("/Icebreaker/Layers/", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            var effect = layer.GetComponent<IcebreakerSvgViewport>() ??
                layer.gameObject.AddComponent<IcebreakerSvgViewport>();
            effect.MapLayer = __result;
            effect.SourcePath = path;
        }
    }
}
