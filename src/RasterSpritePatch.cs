using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DynamicMapsExtended
{
    [HarmonyPatch]
    internal static class RasterSpritePatch
    {
        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);
        private static PropertyInfo _imagePathProperty;
        private static Sprite _placeholder;
        private static long _estimatedGpuBytes;

        [ThreadStatic] private static bool _inDynamicMapsPrecache;
        [ThreadStatic] private static bool _inMapLayerCreate;

        internal static bool InDynamicMapsPrecache
        {
            get => _inDynamicMapsPrecache;
            set => _inDynamicMapsPrecache = value;
        }

        internal static bool InMapLayerCreate
        {
            get => _inMapLayerCreate;
            set => _inMapLayerCreate = value;
        }

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.Utils.SvgUtils")
                ?? throw new MissingMemberException("DynamicMaps.Utils.SvgUtils not found");
            return AccessTools.Method(type, "GetOrLoadCachedSprite")
                ?? throw new MissingMethodException(type.FullName, "GetOrLoadCachedSprite");
        }

        private static bool Prefix(object __0, ref Sprite __result)
        {
            if (__0 == null) return true;

            _imagePathProperty ??= AccessTools.Property(__0.GetType(), "ImagePath");
            var imagePath = _imagePathProperty?.GetValue(__0) as string;
            if (string.IsNullOrEmpty(imagePath))
                return true;

            var style = MapStyleManager.Resolve(imagePath);
            var resolvedPath = style.ResolvedPath;

            if (!IsRasterPath(resolvedPath))
                return true;

            if (!string.Equals(imagePath, resolvedPath, StringComparison.OrdinalIgnoreCase))
                Plugin.Debug($"Style asset selected: {style}");

            if (InDynamicMapsPrecache)
            {
                Plugin.Debug($"Skipped raster startup precache: {resolvedPath}");
                __result = null;
                return false;
            }

            if (Cache.TryGetValue(resolvedPath, out var cached) && cached != null)
            {
                __result = cached;
                return false;
            }

            if (InMapLayerCreate)
            {
                __result = GetPlaceholder();
                Plugin.Debug($"Deferred raster decode during MapLayer.Create: {resolvedPath}");
                return false;
            }

            __result = GetOrLoadRaster(resolvedPath, "direct DynamicMaps request");
            return false;
        }

        internal static Sprite GetOrLoadRaster(string imagePath, string reason)
        {
            if (string.IsNullOrEmpty(imagePath) || !IsRasterPath(imagePath))
                return null;

            if (Cache.TryGetValue(imagePath, out var cached) && cached != null)
            {
                Plugin.Debug($"Raster cache hit: {imagePath}, reason={reason}");
                return cached;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                string absolute;
                if (Path.IsPathRooted(imagePath))
                {
                    absolute = imagePath;
                }
                else
                {
                    absolute = Path.Combine(DynamicMapsBridge.PluginPath, imagePath.Replace('/', Path.DirectorySeparatorChar));
                }

                if (!File.Exists(absolute))
                {
                    Plugin.Log?.LogError($"Raster map layer missing: {absolute}");
                    return GetPlaceholder();
                }

                var bytes = File.ReadAllBytes(absolute);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "DMExt:" + Path.GetFileName(imagePath),
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 0
                };

                if (!ImageConversion.LoadImage(texture, bytes, true))
                {
                    UnityEngine.Object.Destroy(texture);
                    Plugin.Log?.LogError($"Unity failed to decode raster map layer: {absolute}");
                    return GetPlaceholder();
                }

                var sprite = Sprite.Create(
                    texture,
                    new Rect(0, 0, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f),
                    100f,
                    0,
                    SpriteMeshType.FullRect);
                sprite.name = texture.name;

                Cache[imagePath] = sprite;
                _estimatedGpuBytes += (long)texture.width * texture.height * 4L;

                sw.Stop();
                Plugin.Debug($"Raster loaded: {imagePath}, reason={reason}, file={bytes.Length / 1024.0:F1} KiB, decoded={texture.width}x{texture.height}, estimatedRGBA={texture.width * (long)texture.height * 4L / (1024.0 * 1024.0):F1} MiB, time={sw.ElapsedMilliseconds} ms, readable={texture.isReadable}");
                return sprite;
            }
            catch (Exception e)
            {
                sw.Stop();
                Plugin.Log?.LogError($"Raster layer load failed for {imagePath}: {e}");
                return GetPlaceholder();
            }
        }

        internal static void PreloadPaths(IEnumerable<string> imagePaths, string reason)
        {
            if (imagePaths == null) return;

            var loaded = 0;
            foreach (var imagePath in imagePaths)
            {
                if (string.IsNullOrEmpty(imagePath))
                    continue;

                var style = MapStyleManager.Resolve(imagePath);
                var resolvedPath = style.ResolvedPath;
                if (!IsRasterPath(resolvedPath))
                    continue;

                var before = Cache.ContainsKey(resolvedPath);
                var sprite = GetOrLoadRaster(resolvedPath, reason);
                if (!before && sprite != null && !ReferenceEquals(sprite, GetPlaceholder()))
                    loaded++;
            }

            Plugin.Debug($"Active-map raster preload finished: loaded={loaded}, reason={reason}, {DescribeCache()}");
        }

        internal static bool EnsureLayerSprite(object mapLayer, string reason)
        {
            if (mapLayer == null) return false;

            try
            {
                var layerType = mapLayer.GetType();
                var defField = AccessTools.Field(layerType, "_def");
                var def = defField?.GetValue(mapLayer);
                if (def == null) return false;

                var imagePath = AccessTools.Property(def.GetType(), "ImagePath")?.GetValue(def) as string;
                if (string.IsNullOrEmpty(imagePath))
                    return false;

                var style = MapStyleManager.Resolve(imagePath);
                var resolvedPath = style.ResolvedPath;
                if (!IsRasterPath(resolvedPath))
                    return false;

                var sprite = GetOrLoadRaster(resolvedPath, reason);
                if (sprite == null) return false;

                var image = AccessTools.Property(layerType, "Image")?.GetValue(mapLayer);
                if (image == null) return false;

                var spriteProperty = AccessTools.Property(image.GetType(), "sprite");
                if (spriteProperty == null) return false;

                var current = spriteProperty.GetValue(image) as Sprite;
                if (!ReferenceEquals(current, sprite))
                    spriteProperty.SetValue(image, sprite);

                return !ReferenceEquals(sprite, GetPlaceholder());
            }
            catch (Exception e)
            {
                Plugin.Debug("EnsureLayerSprite failed: " + e);
                return false;
            }
        }

        internal static bool RefreshLayerSprite(object mapLayer, string reason)
        {
            if (mapLayer == null) return false;

            try
            {
                var layerType = mapLayer.GetType();
                var defField = AccessTools.Field(layerType, "_def");
                var def = defField?.GetValue(mapLayer);
                if (def == null) return false;

                var imagePath = AccessTools.Property(def.GetType(), "ImagePath")?.GetValue(def) as string;
                if (string.IsNullOrEmpty(imagePath))
                    return false;

                var style = MapStyleManager.Resolve(imagePath);
                Sprite sprite = null;

                if (IsRasterPath(style.ResolvedPath))
                    sprite = GetOrLoadRaster(style.ResolvedPath, reason);
                else
                    sprite = GetOrLoadNativeSprite(def, reason);

                if (sprite == null)
                    return false;

                var image = AccessTools.Property(layerType, "Image")?.GetValue(mapLayer);
                if (image == null) return false;

                var spriteProperty = AccessTools.Property(image.GetType(), "sprite");
                if (spriteProperty == null) return false;

                var current = spriteProperty.GetValue(image) as Sprite;
                if (!ReferenceEquals(current, sprite))
                {
                    spriteProperty.SetValue(image, sprite);
                    Plugin.Debug($"Layer artwork refreshed: reason={reason}, {style}");
                }

                return true;
            }
            catch (Exception e)
            {
                Plugin.Debug("RefreshLayerSprite failed: " + e);
                return false;
            }
        }

        private static Sprite GetOrLoadNativeSprite(object mapLayerDef, string reason)
        {
            if (mapLayerDef == null) return null;

            var svgUtilsType = AccessTools.TypeByName("DynamicMaps.Utils.SvgUtils");
            var method = svgUtilsType == null ? null : AccessTools.Method(svgUtilsType, "GetOrLoadCachedSprite");
            if (method == null)
            {
                Plugin.Debug("DynamicMaps.Utils.SvgUtils.GetOrLoadCachedSprite not found for native style refresh.");
                return null;
            }

            try
            {
                var sprite = method.Invoke(null, new[] { mapLayerDef }) as Sprite;
                if (sprite != null)
                    Plugin.Debug($"Native artwork loaded via DynamicMaps: reason={reason}");
                return sprite;
            }
            catch (Exception e)
            {
                Plugin.Debug("GetOrLoadNativeSprite failed: " + e);
                return null;
            }
        }

        internal static string DescribeSprite(Sprite sprite)
        {
            if (sprite == null) return "<null>";

            try
            {
                var tex = sprite.texture;
                if (tex == null)
                    return $"{sprite.name ?? "<unnamed>"} sprite={sprite.rect.width:F0}x{sprite.rect.height:F0}, texture=<null>";

                var mib = tex.width * (long)tex.height * 4L / (1024.0 * 1024.0);
                return $"{sprite.name ?? "<unnamed>"} sprite={sprite.rect.width:F0}x{sprite.rect.height:F0}, texture={tex.width}x{tex.height}, approxRGBA={mib:F1} MiB";
            }
            catch (Exception e)
            {
                return $"<describe failed: {e.Message}>";
            }
        }

        internal static string DescribeLayerSprite(object mapLayer)
        {
            if (mapLayer == null) return "<null layer>";

            try
            {
                var image = AccessTools.Property(mapLayer.GetType(), "Image")?.GetValue(mapLayer);
                if (image == null) return "<no Image>";

                var sprite = AccessTools.Property(image.GetType(), "sprite")?.GetValue(image) as Sprite;
                return DescribeSprite(sprite);
            }
            catch (Exception e)
            {
                return $"<layer sprite read failed: {e.Message}>";
            }
        }

        internal static Sprite GetLayerSprite(object mapLayer)
        {
            if (mapLayer == null) return null;

            try
            {
                var image = AccessTools.Property(mapLayer.GetType(), "Image")?.GetValue(mapLayer);
                return image == null ? null : AccessTools.Property(image.GetType(), "sprite")?.GetValue(image) as Sprite;
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsRasterPath(string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return false;
            var ext = Path.GetExtension(imagePath);
            return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ReleasePaths(IEnumerable<string> imagePaths, string reason)
        {
            if (imagePaths == null) return;

            var released = 0;
            foreach (var imagePath in imagePaths)
            {
                if (string.IsNullOrEmpty(imagePath) || !Cache.TryGetValue(imagePath, out var sprite))
                    continue;

                Cache.Remove(imagePath);
                if (sprite == null) continue;

                var texture = sprite.texture;
                if (texture != null)
                    _estimatedGpuBytes = Math.Max(0, _estimatedGpuBytes - (long)texture.width * texture.height * 4L);

                UnityEngine.Object.Destroy(sprite);
                if (texture != null)
                    UnityEngine.Object.Destroy(texture);
                released++;
            }

            if (released > 0)
                Plugin.Debug($"Released {released} raster layer(s), reason={reason}. {DescribeCache()}");
        }

        internal static void ReleaseAll(string reason)
        {
            if (Cache.Count > 0)
            {
                var paths = new List<string>(Cache.Keys);
                ReleasePaths(paths, reason);
            }
        }

        internal static string DescribeCache()
            => $"entries={Cache.Count}, estimatedRGBA={_estimatedGpuBytes / (1024.0 * 1024.0):F1} MiB";

        private static Sprite GetPlaceholder()
        {
            if (_placeholder != null) return _placeholder;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "DMExt:deferred-raster-placeholder",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point
            };
            texture.SetPixels32(new[]
            {
                new Color32(0,0,0,0), new Color32(0,0,0,0),
                new Color32(0,0,0,0), new Color32(0,0,0,0)
            });
            texture.Apply(false, true);

            _placeholder = Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            _placeholder.name = texture.name;
            return _placeholder;
        }
    }
}
