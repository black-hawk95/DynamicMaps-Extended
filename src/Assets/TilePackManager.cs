using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicMapsExtended
{
    // Optional high-resolution refinement for bundled Satellite artwork.
    //
    // Each normal .png remains a small preview that can be decoded asynchronously with
    // UnityWebRequestTexture. If a sibling .tiles file exists, this manager streams only the
    // source tile blocks that are actually visible while the player is zoomed in. This keeps the map
    // sharp without ever allocating one enormous 8K/10K Texture2D.
    //
    // Pack formats (little-endian):
    // NVTILES1: 8-byte magic + width/height/tileEdge/count + row-major PNG index.
    // NVTILES2: 8-byte magic + width/height/tileEdge/columns/rows/originX/originY/count
    //            + row-major PNG index. NVTILES2 may contain missing (length=0) records and
    //            preserves original Tarkov.dev source PNG bytes while allowing exact cropped
    //            map bounds that begin partway through the first 256px source tile.
    internal static class TilePackManager
    {
        private const string MagicV1 = "NVTILES1";
        private const string MagicV2 = "NVTILES2";
        private const int HeaderBytesV1 = 24;
        private const int HeaderBytesV2 = 40;
        // Cache survives minimap close/reopen for the current map/style. It is bounded by both
        // entry count and estimated texture memory so repeated opens are instant without runaway VRAM.
        private const int MaxCachedTiles = 160;
        private const long MaxCacheBytes = 64L * 1024L * 1024L;
        // Raid-start prewarm is intentionally much smaller than the full tile cache. The 3K warm
        // atlas already makes the complete floor clear; these centre tiles only make the first
        // deep zoom instant without turning raid start into a texture-loading burst.
        private const long RaidPrewarmMaxBytes = 16L * 1024L * 1024L;
        private const int CachedViewsPerFrame = 4;
        private const int VisibilityScanIntervalFrames = 3;
        // Decode a small pixel-budgeted group concurrently rather than one 256px tile at a time.
        // Four 256px PNGs equal only 1 MiB of decoded RGBA, far less than a normal preview texture,
        // but make refinement roughly four times faster. Larger 512/1024px pack tiles automatically
        // fall back to fewer concurrent decodes so Manimal Abstract cannot create a texture-upload burst.
        private const int MaxConcurrentTileLoads = 4;
        private const int ConcurrentDecodePixelBudget = 4 * 256 * 256;
        // Reveal a coherent centre block rather than individual squares. For normal 256px packs the
        // first 16 tiles form roughly a 4x4 (1024px) detail block; larger tiles use smaller batches.
        private const int InitialRevealTileLimit256 = 16;
        private const int ProgressiveRevealBatchSize256 = 8;
        private const int ProgressiveRevealMaxWaitFrames = 4;
        // Never decode on the map-open frame itself. Start on the following frame only if the
        // frame-time guard says the game is healthy; this gives the fastest safe first refinement.
        private const int InitialDecodeQuietFrames = 1;
        // If the game is already having a slow frame, defer starting another tile decode. Stable
        // 30 FPS (~33 ms) is still allowed; severely overloaded frames are left completely alone.
        private const float SlowFrameDeferSeconds = 0.045f;
        private const float PrefetchMarginPixels = 160f;
        // A zoomed-out 4K/fullscreen map can technically see thousands of source tiles. Never
        // refine all of them at once: keep a centre-first working set so the async detail system
        // stays bounded even if the entire map is visible.
        private const int MaxDesiredTiles = 160;
        // Start centre-first refinement before softness becomes obvious. Work is still capped at
        // 96 desired tiles, bounded by a tiny concurrent pixel budget, and deferred on slow frames.
        private const float HighResThreshold = 0.75f;
        // A 3K warm atlas is the normal-zoom baseline, not a reason to postpone
        // max-resolution refinement until extreme zoom. Start refinement once the map is roughly
        // 72% of the warm texture's native display size; on a 1440p/1600p screen this means the
        // first meaningful zoom step, while the warm atlas still hides any loading latency.
        private const float WarmPreviewHighResThreshold = 0.72f;
        private const int WarmPreviewLongEdge = 2500;
        // Reveal one coherent centre block over a warm atlas. Requiring the entire visible set
        // (often capped at 160 tiles) prevented Woods and other large maps from ever switching to
        // max resolution at practical zoom levels.
        private const int WarmInitialRevealTileLimit256 = 36;
        private const int WarmInitialRevealTileLimit512 = 9;

        private sealed class TileRecord
        {
            internal long Offset;
            internal int Length;
        }

        private sealed class PackInfo
        {
            internal string Path;
            internal int Width;
            internal int Height;
            internal int TileSize;
            internal int Columns;
            internal int Rows;
            internal int OriginX;
            internal int OriginY;
            internal TileRecord[] Tiles;
        }

        private sealed class LayerState
        {
            internal readonly WeakReference Layer;
            internal string PreviewPath;
            internal PackInfo Pack;
            internal GameObject Root;
            internal readonly Dictionary<int, GameObject> Views = new Dictionary<int, GameObject>();
            internal readonly List<int> Desired = new List<int>();
            internal readonly List<int> RevealRequired = new List<int>();
            internal readonly List<int> PendingReveal = new List<int>();
            internal readonly List<int> ScratchRemove = new List<int>();
            internal readonly Vector3[] WorldCorners = new Vector3[4];
            internal int LastScanFrame = -1;
            internal int PendingRevealSinceFrame = -1;
            internal bool InitialRevealComplete;
            internal bool HighResActivationLogged;
            internal int EarliestDecodeFrame;

            internal LayerState(object layer, string previewPath, PackInfo pack)
            {
                Layer = new WeakReference(layer);
                PreviewPath = previewPath;
                Pack = pack;
                EarliestDecodeFrame = Time.frameCount + InitialDecodeQuietFrames;
            }
        }

        private sealed class CachedTile
        {
            internal Texture2D Texture;
            internal long Stamp;
            internal long EstimatedBytes;
        }

        private static readonly List<LayerState> Layers = new List<LayerState>();
        private static readonly Dictionary<string, PackInfo> Packs = new Dictionary<string, PackInfo>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CachedTile> TileCache = new Dictionary<string, CachedTile>(StringComparer.OrdinalIgnoreCase);

        private static Coroutine _worker;
        private static Coroutine _prewarmWorker;
        private static long _stamp;
        private static long _cacheBytes;
        private static int _generation;
        private static string _selectionSignature;
        private static int _activeTileLoads;
        private static int _activeDecodePixels;
        private static readonly HashSet<string> InFlightTiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _nextDecodeStartFrame;
        private static Type _rawImageType;
        private static PropertyInfo _rawTextureProperty;
        private static PropertyInfo _rawColorProperty;
        private static PropertyInfo _rawRaycastProperty;
        private static PropertyInfo _rawUvRectProperty;
        private static PropertyInfo _layerImageProperty;
        private static PropertyInfo _graphicColorProperty;

        internal static void Initialize()
        {
            _rawImageType = AccessTools.TypeByName("UnityEngine.UI.RawImage");
            if (_rawImageType == null)
            {
                Plugin.Debug("High-resolution tile refinement disabled: UnityEngine.UI.RawImage was not found.");
                return;
            }

            _rawTextureProperty = AccessTools.Property(_rawImageType, "texture");
            _rawColorProperty = AccessTools.Property(_rawImageType, "color");
            _rawRaycastProperty = AccessTools.Property(_rawImageType, "raycastTarget");
            _rawUvRectProperty = AccessTools.Property(_rawImageType, "uvRect");

            // Best-effort cleanup of stale temporary tile PNGs from a previous crash/session.
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DynamicMapsExtended", "TileDecode");
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch
            {
                // Never fail plugin startup because a temp file is locked.
            }
        }

        internal static void Shutdown() => Reset("plugin shutdown");

        // DynamicMaps can call LoadMap again when the minimap is simply reopened. Do not throw
        // away already-decoded tiles in that case. Keep only tile packs that belong to the map
        // being selected; a true map/style change still releases irrelevant textures.
        internal static void OnMapSelection(IEnumerable<string> rasterPaths)
        {
            var keepPacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (rasterPaths != null)
            {
                foreach (var imagePath in rasterPaths)
                {
                    var absolute = RasterSpritePatch.ResolveAbsolutePath(imagePath);
                    if (string.IsNullOrEmpty(absolute))
                        continue;

                    var packPath = PersistentAssetCache.ResolvePackForPreview(absolute);
                    if (File.Exists(packPath))
                        keepPacks.Add(packPath);
                }
            }

            // DynamicMaps may invoke LoadMap again merely because the minimap was closed/reopened.
            // Treat an identical pack set as the same selection: preserve decoded textures AND let
            // an in-flight asynchronous decode finish into the cache instead of cancelling/restarting it.
            var signature = string.Join("|", keepPacks.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            var sameSelection = string.Equals(signature, _selectionSignature, StringComparison.OrdinalIgnoreCase);

            if (!sameSelection)
                _generation++; // true map/style selection change invalidates the old async request

            if (_worker != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_worker);
            _worker = null;

            foreach (var state in Layers.ToList())
            {
                ClearViews(state);
                if (state.Root != null)
                    UnityEngine.Object.Destroy(state.Root);
            }
            Layers.Clear();

            if (!sameSelection)
            {
                foreach (var key in TileCache.Keys.ToList())
                {
                    var separator = key.LastIndexOf('|');
                    var packPath = separator > 0 ? key.Substring(0, separator) : string.Empty;
                    if (keepPacks.Contains(packPath))
                        continue;

                    RemoveCachedTile(key);
                }

                foreach (var packPath in Packs.Keys.ToList())
                {
                    if (!keepPacks.Contains(packPath))
                        Packs.Remove(packPath);
                }

                _selectionSignature = signature;
            }

            Plugin.Debug($"High-res tile map selection: same={sameSelection}, kept {TileCache.Count} cached tiles / {_cacheBytes / (1024f * 1024f):0.0} MiB.");
        }

        internal static void OnRaidEnd() => Reset("raid end");

        internal static void OnStyleChanged() => Reset("artwork changed");

        internal static void PrewarmCenterTiles(string previewPath, int requestedTiles = 16)
        {
            if (Plugin.Instance == null || string.IsNullOrWhiteSpace(previewPath) || requestedTiles <= 0)
                return;

            try
            {
                var absolutePreview = RasterSpritePatch.ResolveAbsolutePath(previewPath);
                if (string.IsNullOrEmpty(absolutePreview))
                    return;

                var packPath = PersistentAssetCache.ResolvePackForPreview(absolutePreview);
                if (!File.Exists(packPath))
                    return;

                var pack = GetOrOpenPack(packPath);
                if (pack == null)
                    return;

                if (_prewarmWorker != null)
                    Plugin.Instance.StopCoroutine(_prewarmWorker);

                var tileBytes = Math.Max(1L, (long)pack.TileSize * pack.TileSize * 4L);
                var memoryCappedTiles = Math.Max(1, (int)(RaidPrewarmMaxBytes / tileBytes));
                var targetCount = Math.Min(requestedTiles, memoryCappedTiles);
                var generation = _generation;
                _prewarmWorker = Plugin.Instance.StartCoroutine(PrewarmCenterWorker(pack, previewPath, targetCount, generation));
                Plugin.Debug($"High-res raid prewarm queued: {Path.GetFileName(pack.Path)}, targetTiles={targetCount}, tileSize={pack.TileSize}.");
            }
            catch (Exception e)
            {
                Plugin.Debug("High-res raid prewarm skipped: " + e.Message);
            }
        }

        internal static string Describe()
            => $"layers={Layers.Count}, packs={Packs.Count}, cachedTiles={TileCache.Count}/{MaxCachedTiles}, cacheMiB={_cacheBytes / (1024f * 1024f):0.0}/{MaxCacheBytes / (1024f * 1024f):0}, asyncTiles={_activeTileLoads}/{MaxConcurrentTileLoads}, decodePixels={_activeDecodePixels}/{ConcurrentDecodePixelBudget}, nextDecodeFrame={_nextDecodeStartFrame}, worker={(_worker != null ? "running" : "idle")}, raidPrewarm={(_prewarmWorker != null ? "running" : "idle")}";

        internal static bool HasHighResolutionPack(string previewPath)
        {
            var absolutePreview = RasterSpritePatch.ResolveAbsolutePath(previewPath);
            if (string.IsNullOrEmpty(absolutePreview))
                return false;

            var packPath = PersistentAssetCache.ResolvePackForPreview(absolutePreview);
            return File.Exists(packPath) && new FileInfo(packPath).Length > HeaderBytesV1;
        }

        internal static void ReconcileLayer(object mapLayer)
        {
            if (mapLayer == null)
                return;

            try
            {
                // Only the currently selected floor gets high-resolution tile refinement.
                // Underneath/hidden floors keep their inexpensive preview/native artwork.
                if (!StyleRefreshManager.IsSelectedFloor(mapLayer))
                {
                    RemoveLayer(mapLayer, destroyCachedTiles: false);
                    return;
                }

                var sourcePath = RasterSpritePatch.GetLayerImagePath(mapLayer);
                if (string.IsNullOrEmpty(sourcePath))
                {
                    RemoveLayer(mapLayer, destroyCachedTiles: false);
                    return;
                }

                var resolution = MapStyleManager.Resolve(sourcePath);
                var previewPath = resolution.ResolvedPath;
                if (!RasterSpritePatch.IsExtensionOwnedRaster(previewPath))
                {
                    RemoveLayer(mapLayer, destroyCachedTiles: false);
                    return;
                }

                var absolutePreview = RasterSpritePatch.ResolveAbsolutePath(previewPath);
                if (string.IsNullOrEmpty(absolutePreview))
                    return;

                var packPath = PersistentAssetCache.ResolvePackForPreview(absolutePreview);
                if (!File.Exists(packPath))
                {
                    RemoveLayer(mapLayer, destroyCachedTiles: false);
                    return;
                }

                var pack = GetOrOpenPack(packPath);
                if (pack == null)
                    return;

                var existing = FindState(mapLayer);
                if (existing != null)
                {
                    if (string.Equals(existing.PreviewPath, previewPath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Pack.Path, pack.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        SyncLayerTint(existing);
                        EnsureWorker();
                        return;
                    }

                    RemoveState(existing, destroyCachedTiles: false);
                }

                var state = new LayerState(mapLayer, previewPath, pack);
                Layers.Add(state);
                EnsureRoot(state);
                SyncLayerTint(state);
                EnsureWorker();

                Plugin.Debug($"High-res tile pack armed: {Path.GetFileName(pack.Path)} {pack.Width}x{pack.Height}, {pack.Columns}x{pack.Rows} tiles.");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"Could not arm high-resolution map tiles: {e.Message}");
            }
        }

        internal static void SyncLayerTint(object mapLayer)
        {
            var state = FindState(mapLayer);
            if (state != null)
                SyncLayerTint(state);
        }

        private static void SyncLayerTint(LayerState state)
        {
            if (state == null || state.Layer?.Target == null)
                return;

            var color = GetLayerColor(state.Layer.Target);
            if (!color.HasValue)
                return;

            foreach (var view in state.Views.Values)
            {
                if (view == null) continue;
                var raw = view.GetComponent(_rawImageType);
                if (raw != null)
                    _rawColorProperty?.SetValue(raw, color.Value);
            }
        }

        private static Color? GetLayerColor(object mapLayer)
        {
            try
            {
                _layerImageProperty ??= AccessTools.Property(mapLayer.GetType(), "Image");
                var image = _layerImageProperty?.GetValue(mapLayer);
                if (image == null) return null;

                _graphicColorProperty ??= AccessTools.Property(image.GetType(), "color");
                var value = _graphicColorProperty?.GetValue(image);
                return value is Color color ? color : (Color?)null;
            }
            catch
            {
                return null;
            }
        }

        private static void EnsureRoot(LayerState state)
        {
            if (state.Root != null || !(state.Layer.Target is Component component))
                return;

            var go = new GameObject("DMExt High-Resolution Tiles", typeof(RectTransform));
            go.layer = component.gameObject.layer;
            go.transform.SetParent(component.transform, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;

            // Keep the preview visible until the complete initially-visible high-resolution
            // group is ready. This prevents the square-by-square sharpening effect.
            go.SetActive(false);
            state.Root = go;
        }

        private static PackInfo GetOrOpenPack(string path)
        {
            path = Path.GetFullPath(path);
            if (Packs.TryGetValue(path, out var cached))
                return cached;

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < HeaderBytesV1)
                    throw new InvalidDataException("tile pack is too small");

                var magicBytes = new byte[8];
                ReadExactly(stream, magicBytes, 0, magicBytes.Length);
                var magic = System.Text.Encoding.ASCII.GetString(magicBytes);

                int headerBytes;
                int width;
                int height;
                int tileSize;
                int columns;
                int rows;
                int originX;
                int originY;
                int count;
                var allowMissing = false;

                if (string.Equals(magic, MagicV1, StringComparison.Ordinal))
                {
                    headerBytes = HeaderBytesV1;
                    var rest = new byte[HeaderBytesV1 - 8];
                    ReadExactly(stream, rest, 0, rest.Length);
                    width = checked((int)BitConverter.ToUInt32(rest, 0));
                    height = checked((int)BitConverter.ToUInt32(rest, 4));
                    tileSize = checked((int)BitConverter.ToUInt32(rest, 8));
                    count = checked((int)BitConverter.ToUInt32(rest, 12));
                    columns = (width + tileSize - 1) / tileSize;
                    rows = (height + tileSize - 1) / tileSize;
                    originX = 0;
                    originY = 0;
                }
                else if (string.Equals(magic, MagicV2, StringComparison.Ordinal))
                {
                    headerBytes = HeaderBytesV2;
                    if (stream.Length < HeaderBytesV2)
                        throw new InvalidDataException("NVTILES2 pack is too small");

                    var rest = new byte[HeaderBytesV2 - 8];
                    ReadExactly(stream, rest, 0, rest.Length);
                    width = checked((int)BitConverter.ToUInt32(rest, 0));
                    height = checked((int)BitConverter.ToUInt32(rest, 4));
                    tileSize = checked((int)BitConverter.ToUInt32(rest, 8));
                    columns = checked((int)BitConverter.ToUInt32(rest, 12));
                    rows = checked((int)BitConverter.ToUInt32(rest, 16));
                    originX = BitConverter.ToInt32(rest, 20);
                    originY = BitConverter.ToInt32(rest, 24);
                    count = checked((int)BitConverter.ToUInt32(rest, 28));
                    allowMissing = true;
                }
                else
                {
                    throw new InvalidDataException($"unexpected tile-pack magic '{magic}'");
                }

                if (width <= 0 || height <= 0 || width > 65536 || height > 65536)
                    throw new InvalidDataException($"invalid tile-pack dimensions {width}x{height}");
                if (tileSize < 128 || tileSize > 1024)
                    throw new InvalidDataException($"invalid tile size {tileSize}");
                if (columns <= 0 || rows <= 0 || count != columns * rows || count > 16384)
                    throw new InvalidDataException($"invalid tile grid {columns}x{rows} / count {count}");

                var indexBytesLength = checked(count * 8);
                if ((long)headerBytes + indexBytesLength > stream.Length)
                    throw new InvalidDataException("tile index extends past end of pack");

                var indexBytes = new byte[indexBytesLength];
                ReadExactly(stream, indexBytes, 0, indexBytes.Length);
                var records = new TileRecord[count];
                var payloadStart = (ulong)(headerBytes + indexBytesLength);

                for (var i = 0; i < count; i++)
                {
                    var offset = BitConverter.ToUInt32(indexBytes, i * 8);
                    var length = BitConverter.ToUInt32(indexBytes, i * 8 + 4);

                    if (allowMissing && length == 0)
                    {
                        records[i] = new TileRecord { Offset = 0, Length = 0 };
                        continue;
                    }

                    if (length == 0 || length > 4 * 1024 * 1024 || (ulong)offset < payloadStart || (ulong)offset + (ulong)length > (ulong)stream.Length)
                        throw new InvalidDataException($"invalid tile record {i}");

                    records[i] = new TileRecord { Offset = offset, Length = checked((int)length) };
                }

                var pack = new PackInfo
                {
                    Path = path,
                    Width = width,
                    Height = height,
                    TileSize = tileSize,
                    Columns = columns,
                    Rows = rows,
                    OriginX = originX,
                    OriginY = originY,
                    Tiles = records
                };

                Packs[path] = pack;
                return pack;
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning($"High-resolution tile pack is invalid ({path}): {e.Message}");
                Packs[path] = null;
                return null;
            }
        }

        private static IEnumerator PrewarmCenterWorker(PackInfo pack, string previewPath, int targetCount, int generation)
        {
            var dummy = new LayerState(new object(), previewPath, pack)
            {
                EarliestDecodeFrame = Time.frameCount + InitialDecodeQuietFrames
            };
            var candidates = BuildCenterTileCandidates(pack, targetCount);
            var prewarmStarted = Time.realtimeSinceStartup;

            while (generation == _generation && candidates.Count > 0 && Time.realtimeSinceStartup - prewarmStarted < 8.0f)
            {
                var remaining = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var index = candidates[i];
                    if (HasCachedTile(pack.Path, index))
                        continue;

                    remaining++;
                    if (!IsTileInFlight(pack.Path, index) && CanStartAnotherDecode(dummy))
                        TryStartTileDecode(dummy, index, generation);
                }

                if (remaining == 0)
                    break;

                yield return null;
            }

            if (generation == _generation)
            {
                var ready = 0;
                for (var i = 0; i < candidates.Count; i++)
                    if (HasCachedTile(pack.Path, candidates[i])) ready++;
                Plugin.Debug($"High-res raid prewarm complete: {Path.GetFileName(pack.Path)}, cached={ready}/{candidates.Count} centre tiles.");
            }

            _prewarmWorker = null;
        }

        private static List<int> BuildCenterTileCandidates(PackInfo pack, int maxCount)
        {
            var result = new List<int>(Math.Max(0, maxCount));
            if (pack == null || maxCount <= 0)
                return result;

            var centerPixelX = pack.Width * 0.5f;
            var centerPixelY = pack.Height * 0.5f;
            var centerCol = Mathf.Clamp(Mathf.FloorToInt((centerPixelX - pack.OriginX) / pack.TileSize), 0, pack.Columns - 1);
            var centerRow = Mathf.Clamp(Mathf.FloorToInt((centerPixelY - pack.OriginY) / pack.TileSize), 0, pack.Rows - 1);
            var maxRadius = Math.Max(pack.Columns, pack.Rows);

            for (var radius = 0; radius <= maxRadius && result.Count < maxCount; radius++)
            {
                var colMin = Math.Max(0, centerCol - radius);
                var colMax = Math.Min(pack.Columns - 1, centerCol + radius);
                var rowMin = Math.Max(0, centerRow - radius);
                var rowMax = Math.Min(pack.Rows - 1, centerRow + radius);

                for (var col = colMin; col <= colMax && result.Count < maxCount; col++)
                {
                    AddPrewarmCandidate(pack, col, rowMin, result, maxCount);
                    if (rowMax != rowMin)
                        AddPrewarmCandidate(pack, col, rowMax, result, maxCount);
                }

                for (var row = rowMin + 1; row < rowMax && result.Count < maxCount; row++)
                {
                    AddPrewarmCandidate(pack, colMin, row, result, maxCount);
                    if (colMax != colMin)
                        AddPrewarmCandidate(pack, colMax, row, result, maxCount);
                }
            }

            return result;
        }

        private static void AddPrewarmCandidate(PackInfo pack, int col, int row, List<int> result, int maxCount)
        {
            if (result.Count >= maxCount || col < 0 || col >= pack.Columns || row < 0 || row >= pack.Rows)
                return;

            var index = row * pack.Columns + col;
            if (pack.Tiles[index].Length > 0 && !result.Contains(index))
                result.Add(index);
        }

        private static void EnsureWorker()
        {
            if (_worker == null && Plugin.Instance != null && Layers.Count > 0)
                _worker = Plugin.Instance.StartCoroutine(Worker());
        }

        private static bool CanStartAnotherDecode(LayerState state)
        {
            if (state == null || state.Pack == null || Time.frameCount < state.EarliestDecodeFrame || Time.frameCount < _nextDecodeStartFrame)
                return false;

            // The preview already gives the user a complete map. High-resolution refinement is
            // optional visual work, so never begin another decode while the game is visibly behind.
            var dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt >= SlowFrameDeferSeconds)
                return false;

            if (_activeTileLoads >= MaxConcurrentTileLoads)
                return false;

            var tilePixels = state.Pack.TileSize * state.Pack.TileSize;

            // Always permit one request when idle, even for a 1024px macro tile. Once something is
            // active, enforce the small global pixel budget. This gives normal 256px packs up to four
            // parallel async decodes while large tiles remain strictly one-at-a-time.
            if (_activeTileLoads == 0)
                return true;

            return _activeDecodePixels + tilePixels <= ConcurrentDecodePixelBudget;
        }

        private static bool IsTileInFlight(string packPath, int index)
            => InFlightTiles.Contains(CacheKey(packPath, index));

        private static bool TryStartTileDecode(LayerState state, int index, int generation)
        {
            if (state?.Pack == null || index < 0 || index >= state.Pack.Tiles.Length ||
                Plugin.Instance == null || !CanStartAnotherDecode(state) ||
                HasCachedTile(state.Pack.Path, index) || IsTileInFlight(state.Pack.Path, index))
                return false;

            var key = CacheKey(state.Pack.Path, index);
            var decodePixels = state.Pack.TileSize * state.Pack.TileSize;
            InFlightTiles.Add(key);
            _activeTileLoads++;
            _activeDecodePixels += decodePixels;

            try
            {
                Plugin.Instance.StartCoroutine(LoadTileAsync(state, index, generation, key, decodePixels));
                return true;
            }
            catch
            {
                InFlightTiles.Remove(key);
                _activeTileLoads = Math.Max(0, _activeTileLoads - 1);
                _activeDecodePixels = Math.Max(0, _activeDecodePixels - decodePixels);
                throw;
            }
        }

        private static IEnumerator Worker()
        {
            while (Layers.Count > 0)
            {
                PruneDeadLayers();
                if (Layers.Count == 0)
                    break;

                var cachedViewsCreated = 0;
                var generation = _generation;

                for (var i = 0; i < Layers.Count; i++)
                {
                    var state = Layers[i];
                    if (state.Layer.Target == null)
                        continue;

                    var shouldRescan = Time.frameCount - state.LastScanFrame >= VisibilityScanIntervalFrames;
                    if (shouldRescan)
                    {
                        state.LastScanFrame = Time.frameCount;

                        if (!StyleRefreshManager.IsSelectedFloor(state.Layer.Target) || !NeedsHighResolution(state))
                        {
                            ClearViews(state);
                            continue;
                        }

                        FillVisibleTileIndices(state, PrefetchMarginPixels, state.Desired);
                        RemoveUndesiredViews(state, state.Desired);

                        if (!state.HighResActivationLogged && state.Desired.Count > 0)
                        {
                            state.HighResActivationLogged = true;
                            var preview = RasterSpritePatch.GetLayerSprite(state.Layer.Target);
                            var previewSize = preview?.texture != null
                                ? $"{preview.texture.width}x{preview.texture.height}"
                                : "<none>";
                            Plugin.Debug($"High-res refinement active: {Path.GetFileName(state.Pack.Path)}, preview={previewSize}, desired={state.Desired.Count}.");
                        }

                        if (!state.InitialRevealComplete)
                            RefreshInitialRevealTargets(state);
                    }

                    if (state.Desired.Count == 0)
                        continue;

                    // Reopening the same minimap/map uses already-decoded GPU textures. Recreate a
                    // handful of lightweight RawImage views per frame; four is still tiny compared
                    // with the normal map UI while making a cached reopen effectively immediate.
                    while (cachedViewsCreated < CachedViewsPerFrame)
                    {
                        var nearestCached = -1;

                        // Desired is centre-first, so no per-frame screen-distance sort is needed.
                        for (var desiredIndex = 0; desiredIndex < state.Desired.Count; desiredIndex++)
                        {
                            var index = state.Desired[desiredIndex];
                            if (state.Views.ContainsKey(index) || !HasCachedTile(state.Pack.Path, index))
                                continue;

                            nearestCached = index;
                            break;
                        }

                        if (nearestCached < 0 || !TryCreateCachedTileView(state, nearestCached))
                            break;

                        cachedViewsCreated++;
                    }

                    UpdateRevealState(state);

                    // Start a tiny pixel-budgeted group of async decodes. For normal 256px source
                    // tiles this means up to four concurrent requests (1 MiB decoded RGBA total),
                    // which is substantially smaller than one normal preview texture. Large 512/
                    // 1024px tiles automatically reduce to one request, preserving the no-stutter rule.
                    while (CanStartAnotherDecode(state))
                    {
                        var candidate = -1;
                        foreach (var index in state.Desired)
                        {
                            if (state.Views.ContainsKey(index) || HasCachedTile(state.Pack.Path, index) ||
                                IsTileInFlight(state.Pack.Path, index))
                                continue;

                            candidate = index;
                            break;
                        }

                        if (candidate < 0 || !TryStartTileDecode(state, candidate, generation))
                            break;
                    }
                }

                TrimTileCache();
                yield return null;
            }

            _worker = null;
        }

        private static bool HasCachedTile(string packPath, int index)
        {
            var key = CacheKey(packPath, index);
            return TileCache.TryGetValue(key, out var cached) && cached.Texture != null;
        }

        private static int InitialRevealLimit(LayerState state)
        {
            if (state?.Pack == null) return 1;

            // The older warm-atlas path waited for the *entire* visible working set
            // before revealing anything. Large maps normally hit the 160-tile Desired cap, so this
            // condition never completed at useful zoom levels. Instead reveal a centre-first block
            // as one coherent batch, then let normal progressive batches expand it while panning/zooming.
            var preview = state.Layer.Target == null ? null : RasterSpritePatch.GetLayerSprite(state.Layer.Target);
            if (RasterWarmupManager.IsWarmSprite(preview))
            {
                if (state.Pack.TileSize <= 256)
                    return Math.Min(WarmInitialRevealTileLimit256, Math.Max(1, state.Desired.Count));
                if (state.Pack.TileSize <= 512)
                    return Math.Min(WarmInitialRevealTileLimit512, Math.Max(1, state.Desired.Count));
                return 1;
            }

            var pack = state.Pack;
            if (pack.TileSize <= 256) return InitialRevealTileLimit256;
            if (pack.TileSize <= 512) return 4;
            return 1;
        }

        private static int ProgressiveRevealBatchSize(PackInfo pack)
        {
            if (pack == null) return 1;
            if (pack.TileSize <= 256) return ProgressiveRevealBatchSize256;
            if (pack.TileSize <= 512) return 2;
            return 1;
        }

        private static void RefreshInitialRevealTargets(LayerState state)
        {
            if (state == null || state.InitialRevealComplete)
                return;

            // Drop targets that moved outside the current visible/prefetch region.
            for (var i = state.RevealRequired.Count - 1; i >= 0; i--)
            {
                if (!state.Desired.Contains(state.RevealRequired[i]))
                    state.RevealRequired.RemoveAt(i);
            }

            var revealLimit = InitialRevealLimit(state);
            if (revealLimit <= 0)
            {
                // No initial reveal requested for this pack/state. Keep the preview visible.
                state.RevealRequired.Clear();
                return;
            }

            while (state.RevealRequired.Count > revealLimit)
                state.RevealRequired.RemoveAt(state.RevealRequired.Count - 1);

            // Desired is centre-first. Build one coherent initial block; warm previews use a
            // larger block than the small fallback preview so the refinement is clearly visible.
            for (var i = 0; i < state.Desired.Count && state.RevealRequired.Count < revealLimit; i++)
            {
                var index = state.Desired[i];
                if (!state.RevealRequired.Contains(index))
                    state.RevealRequired.Add(index);
            }
        }

        private static void UpdateRevealState(LayerState state)
        {
            if (state == null || state.Root == null)
                return;

            if (!state.InitialRevealComplete)
            {
                if (state.RevealRequired.Count == 0)
                    return;

                foreach (var index in state.RevealRequired)
                {
                    if (!state.Views.ContainsKey(index))
                        return;
                }

                state.InitialRevealComplete = true;
                state.RevealRequired.Clear();
                state.Root.SetActive(true);
                RevealPendingViews(state);
                Plugin.Debug($"High-res initial tile group revealed: {Path.GetFileName(state.Pack.Path)}, views={state.Views.Count}, desired={state.Desired.Count}.");
                return;
            }

            if (state.PendingReveal.Count == 0)
                return;

            var waitedLongEnough = state.PendingRevealSinceFrame >= 0 &&
                                   Time.frameCount - state.PendingRevealSinceFrame >= ProgressiveRevealMaxWaitFrames;
            if (state.PendingReveal.Count >= ProgressiveRevealBatchSize(state.Pack) || waitedLongEnough)
                RevealPendingViews(state);
        }

        private static void RevealPendingViews(LayerState state)
        {
            if (state == null)
                return;

            for (var i = 0; i < state.PendingReveal.Count; i++)
            {
                var index = state.PendingReveal[i];
                if (state.Views.TryGetValue(index, out var go) && go != null)
                    go.SetActive(true);
            }

            state.PendingReveal.Clear();
            state.PendingRevealSinceFrame = -1;
        }

        // Extracting bytes and writing the temporary local PNG happens on a worker thread.
        // UnityWebRequestTexture then performs the actual PNG decode asynchronously from file://.
        // There is intentionally no synchronous ImageConversion.LoadImage path here.
        private static IEnumerator LoadTileAsync(
            LayerState state,
            int index,
            int generation,
            string flightKey,
            int decodePixels)
        {
            var pack = state?.Pack;
            string tempPath = null;
            UnityWebRequest request = null;

            try
            {
                if (pack == null || index < 0 || index >= pack.Tiles.Length)
                    yield break;

                Task<string> extractTask = null;
                try
                {
                    extractTask = Task.Run(() => ExtractTileToTempFile(pack, index, generation));
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"Could not queue asynchronous map-tile extraction: {e.Message}");
                }

                if (extractTask == null)
                    yield break;

                while (!extractTask.IsCompleted)
                    yield return null;

                if (extractTask.IsFaulted || extractTask.IsCanceled)
                {
                    var message = extractTask.Exception?.GetBaseException().Message ?? "cancelled";
                    Plugin.Log?.LogWarning($"Could not extract high-resolution map tile {Path.GetFileName(pack.Path)}#{index}: {message}");
                    yield break;
                }

                tempPath = extractTask.Result;
                if (generation != _generation || string.IsNullOrEmpty(tempPath) || !File.Exists(tempPath))
                    yield break;

                try
                {
                    request = UnityWebRequestTexture.GetTexture(new Uri(tempPath).AbsoluteUri, true);
                }
                catch (Exception e)
                {
                    Plugin.Log?.LogWarning($"Could not create asynchronous local tile request: {e.Message}");
                }

                if (request == null)
                    yield break;

                yield return request.SendWebRequest();

                Texture2D texture = null;
                if (generation == _generation && request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        texture = DownloadHandlerTexture.GetContent(request);
                    }
                    catch (Exception e)                    {
                        Plugin.Log?.LogWarning($"Could not obtain asynchronously decoded map tile: {e.Message}");
                    }
                }
                else if (generation == _generation && request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log?.LogWarning($"Async local map-tile decode failed for {Path.GetFileName(pack.Path)}#{index}: {request.error}");
                }

                if (texture != null)
                {
                    if (generation != _generation)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                    else
                    {
                        texture.name = $"DMExtTile:{Path.GetFileName(pack.Path)}:{index}";
                        texture.wrapMode = TextureWrapMode.Clamp;
                        texture.filterMode = FilterMode.Bilinear;
                        texture.anisoLevel = 0;

                        // Cache on the decode-completion frame. RawImage creation happens later in
                        // the worker, so decode completion and UI allocation remain separated.
                        StoreCachedTile(pack.Path, index, texture);
                    }
                }
            }
            finally
            {
                request?.Dispose();
                DeleteTempFile(tempPath);
                if (!string.IsNullOrEmpty(flightKey))
                    InFlightTiles.Remove(flightKey);
                _activeTileLoads = Math.Max(0, _activeTileLoads - 1);
                _activeDecodePixels = Math.Max(0, _activeDecodePixels - decodePixels);
                _nextDecodeStartFrame = Time.frameCount + 1;
            }
        }

        private static string ExtractTileToTempFile(PackInfo pack, int index, int generation)
        {
            var record = pack.Tiles[index];
            if (record == null || record.Length <= 0)
                return null;

            var bytes = new byte[record.Length];
            using (var stream = new FileStream(pack.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                stream.Seek(record.Offset, SeekOrigin.Begin);
                ReadExactly(stream, bytes, 0, bytes.Length);
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "DynamicMapsExtended", "TileDecode");
            Directory.CreateDirectory(tempDir);

            var fileName = $"{Path.GetFileNameWithoutExtension(pack.Path)}-{index}-{generation}-{Guid.NewGuid():N}.png";
            var tempPath = Path.Combine(tempDir, fileName);
            File.WriteAllBytes(tempPath, bytes);
            return tempPath;
        }

        private static void DeleteTempFile(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Temp cleanup is best effort; stale files are removed on the next plugin start.
            }
        }

        private static void StoreCachedTile(string packPath, int index, Texture2D texture)
        {
            if (texture == null)
                return;

            var key = CacheKey(packPath, index);
            if (TileCache.TryGetValue(key, out var existing) && existing.Texture != null)
            {
                existing.Stamp = ++_stamp;
                UnityEngine.Object.Destroy(texture);
                return;
            }

            var estimatedBytes = Math.Max(1L, (long)texture.width * texture.height * 4L);
            TileCache[key] = new CachedTile
            {
                Texture = texture,
                Stamp = ++_stamp,
                EstimatedBytes = estimatedBytes
            };
            _cacheBytes += estimatedBytes;
            TrimTileCache();
        }

        private static void RemoveCachedTile(string key)
        {
            if (string.IsNullOrEmpty(key) || !TileCache.TryGetValue(key, out var entry))
                return;

            TileCache.Remove(key);
            _cacheBytes = Math.Max(0, _cacheBytes - Math.Max(0, entry.EstimatedBytes));
            if (entry.Texture != null)
                UnityEngine.Object.Destroy(entry.Texture);
        }

        private static bool NeedsHighResolution(LayerState state)
        {
            if (!(state.Layer.Target is Component component))
                return false;

            var rect = component.transform as RectTransform;
            if (rect == null)
                return false;

            rect.GetWorldCorners(state.WorldCorners);
            var camera = GetCanvasCamera(rect);
            var left = RectTransformUtility.WorldToScreenPoint(camera, state.WorldCorners[0]);
            var right = RectTransformUtility.WorldToScreenPoint(camera, state.WorldCorners[2]);
            var displayedWidth = Mathf.Abs(right.x - left.x);
            var displayedHeight = Mathf.Abs(right.y - left.y);

            var preview = RasterSpritePatch.GetLayerSprite(state.Layer.Target);
            if (preview == null || preview.texture == null ||
                (preview.name ?? string.Empty).StartsWith("DMExt:deferred-raster-placeholder", StringComparison.OrdinalIgnoreCase))
                return false;

            var previewWidth = preview.texture.width;
            var previewHeight = preview.texture.height;

            var longEdge = Math.Max(previewWidth, previewHeight);
            // If a whole-floor warm atlas exists but the small preview is still on screen, do not
            // reveal a sharp tile island while that atlas is loading. The selected floor is upgraded
            // asynchronously by RasterWarmupManager; until then a uniform preview looks much better.
            // If the optional warm load was skipped/failed on busy frames, this returns false and
            // max-resolution tiles become the fallback again.
            if (longEdge < WarmPreviewLongEdge &&
                RasterWarmupManager.ShouldDeferTilesForWarmAtlas(state.PreviewPath, preview))
                return false;

            // A ~3K warm atlas is the uniform baseline. Start max-resolution refinement on the
            // first meaningful zoom rather than waiting for extreme zoom; the reveal path below
            // switches a coherent centre block, not individual checkerboard tiles.
            var threshold = longEdge >= WarmPreviewLongEdge
                ? WarmPreviewHighResThreshold
                : HighResThreshold;

            return displayedWidth > previewWidth * threshold || displayedHeight > previewHeight * threshold;
        }

        private static void FillVisibleTileIndices(LayerState state, float marginPixels, List<int> result)
        {
            result.Clear();
            if (!(state.Layer.Target is Component component))
                return;

            var rect = component.transform as RectTransform;
            if (rect == null || rect.rect.width == 0f || rect.rect.height == 0f)
                return;

            var camera = GetCanvasCamera(rect);

            // Convert only the four viewport corners into local coordinates. Reuse the destination
            // list so the visibility scan does not allocate on every third frame.
            var localMinX = float.PositiveInfinity;
            var localMaxX = float.NegativeInfinity;
            var localMinY = float.PositiveInfinity;
            var localMaxY = float.NegativeInfinity;

            AccumulateLocalPoint(rect, camera, new Vector2(-marginPixels, -marginPixels), ref localMinX, ref localMaxX, ref localMinY, ref localMaxY);
            AccumulateLocalPoint(rect, camera, new Vector2(Screen.width + marginPixels, -marginPixels), ref localMinX, ref localMaxX, ref localMinY, ref localMaxY);
            AccumulateLocalPoint(rect, camera, new Vector2(-marginPixels, Screen.height + marginPixels), ref localMinX, ref localMaxX, ref localMinY, ref localMaxY);
            AccumulateLocalPoint(rect, camera, new Vector2(Screen.width + marginPixels, Screen.height + marginPixels), ref localMinX, ref localMaxX, ref localMinY, ref localMaxY);

            if (float.IsInfinity(localMinX) || float.IsInfinity(localMaxX) ||
                float.IsInfinity(localMinY) || float.IsInfinity(localMaxY))
                return;

            var localRect = rect.rect;
            var pixelX0 = (localMinX - localRect.xMin) / localRect.width * state.Pack.Width;
            var pixelX1 = (localMaxX - localRect.xMin) / localRect.width * state.Pack.Width;
            var pixelY0 = (localRect.yMax - localMaxY) / localRect.height * state.Pack.Height;
            var pixelY1 = (localRect.yMax - localMinY) / localRect.height * state.Pack.Height;

            var visibleX0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pixelX0, pixelX1)), 0, state.Pack.Width);
            var visibleX1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pixelX0, pixelX1)), 0, state.Pack.Width);
            var visibleY0 = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(pixelY0, pixelY1)), 0, state.Pack.Height);
            var visibleY1 = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(pixelY0, pixelY1)), 0, state.Pack.Height);

            if (visibleX1 <= visibleX0 || visibleY1 <= visibleY0)
                return;

            var colMin = Mathf.Clamp(Mathf.FloorToInt((visibleX0 - state.Pack.OriginX) / (float)state.Pack.TileSize), 0, state.Pack.Columns - 1);
            var colMax = Mathf.Clamp(Mathf.FloorToInt(((visibleX1 - 1) - state.Pack.OriginX) / (float)state.Pack.TileSize), 0, state.Pack.Columns - 1);
            var rowMin = Mathf.Clamp(Mathf.FloorToInt((visibleY0 - state.Pack.OriginY) / (float)state.Pack.TileSize), 0, state.Pack.Rows - 1);
            var rowMax = Mathf.Clamp(Mathf.FloorToInt(((visibleY1 - 1) - state.Pack.OriginY) / (float)state.Pack.TileSize), 0, state.Pack.Rows - 1);

            // Emit centre-first in square rings. The worker can now take the first eligible item
            // instead of calculating screen distance for every visible tile every frame.
            var centerCol = (colMin + colMax) / 2;
            var centerRow = (rowMin + rowMax) / 2;
            var maxRadius = Math.Max(
                Math.Max(centerCol - colMin, colMax - centerCol),
                Math.Max(centerRow - rowMin, rowMax - centerRow));

            for (var radius = 0; radius <= maxRadius; radius++)
            {
                if (result.Count >= MaxDesiredTiles)
                    return;

                var ringColMin = Math.Max(colMin, centerCol - radius);
                var ringColMax = Math.Min(colMax, centerCol + radius);
                var ringRowMin = Math.Max(rowMin, centerRow - radius);
                var ringRowMax = Math.Min(rowMax, centerRow + radius);

                for (var col = ringColMin; col <= ringColMax; col++)
                {
                    AddVisibleTile(state, col, ringRowMin, result);
                    if (ringRowMax != ringRowMin)
                        AddVisibleTile(state, col, ringRowMax, result);
                }

                for (var row = ringRowMin + 1; row < ringRowMax; row++)
                {
                    AddVisibleTile(state, ringColMin, row, result);
                    if (ringColMax != ringColMin)
                        AddVisibleTile(state, ringColMax, row, result);
                }
            }
        }

        private static void AddVisibleTile(LayerState state, int col, int row, List<int> result)
        {
            if (result.Count >= MaxDesiredTiles ||
                col < 0 || col >= state.Pack.Columns || row < 0 || row >= state.Pack.Rows)
                return;

            var index = row * state.Pack.Columns + col;
            if (state.Pack.Tiles[index].Length > 0)
                result.Add(index);
        }

        private static void AccumulateLocalPoint(
            RectTransform rect,
            Camera camera,
            Vector2 screenPoint,
            ref float localMinX,
            ref float localMaxX,
            ref float localMinY,
            ref float localMaxY)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPoint, camera, out var local))
                return;

            localMinX = Mathf.Min(localMinX, local.x);
            localMaxX = Mathf.Max(localMaxX, local.x);
            localMinY = Mathf.Min(localMinY, local.y);
            localMaxY = Mathf.Max(localMaxY, local.y);
        }

        private static bool TryGetTilePixelRect(PackInfo pack, int col, int row, out int x0, out int y0, out int x1, out int y1)
        {
            var rawX0 = pack.OriginX + col * pack.TileSize;
            var rawY0 = pack.OriginY + row * pack.TileSize;
            var rawX1 = rawX0 + pack.TileSize;
            var rawY1 = rawY0 + pack.TileSize;

            x0 = Math.Max(0, rawX0);
            y0 = Math.Max(0, rawY0);
            x1 = Math.Min(pack.Width, rawX1);
            y1 = Math.Min(pack.Height, rawY1);
            return x1 > x0 && y1 > y0;
        }

        private static float TileDistanceToScreenCenter(LayerState state, int index)
        {
            if (!(state.Layer.Target is Component component))
                return float.MaxValue;

            var rect = component.transform as RectTransform;
            if (rect == null)
                return float.MaxValue;

            var col = index % state.Pack.Columns;
            var row = index / state.Pack.Columns;
            if (!TryGetTilePixelRect(state.Pack, col, row, out var pixelX0, out var pixelY0, out var pixelX1, out var pixelY1))
                return float.MaxValue;

            // Only the tile centre is needed for queue priority. Avoid allocating four screen
            // points + LINQ min/max calculations for every candidate tile.
            var localRect = rect.rect;
            var pixelCenterX = (pixelX0 + pixelX1) * 0.5f;
            var pixelCenterY = (pixelY0 + pixelY1) * 0.5f;
            var localX = localRect.xMin + localRect.width * pixelCenterX / state.Pack.Width;
            var localY = localRect.yMax - localRect.height * pixelCenterY / state.Pack.Height;
            var screen = RectTransformUtility.WorldToScreenPoint(
                GetCanvasCamera(rect),
                rect.TransformPoint(new Vector3(localX, localY, 0f)));

            var dx = screen.x - Screen.width * 0.5f;
            var dy = screen.y - Screen.height * 0.5f;
            return dx * dx + dy * dy;
        }

        private static Camera GetCanvasCamera(RectTransform rect)
        {
            var canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return null;
            return canvas.worldCamera;
        }

        private static bool TryCreateCachedTileView(LayerState state, int index)
        {
            if (state.Root == null)
                EnsureRoot(state);
            if (state.Root == null || index < 0 || index >= state.Pack.Tiles.Length)
                return false;
            if (state.Pack.Tiles[index].Length <= 0 || state.Views.ContainsKey(index))
                return false;

            var col = index % state.Pack.Columns;
            var row = index / state.Pack.Columns;
            if (!TryGetTilePixelRect(state.Pack, col, row, out var pixelX0, out var pixelY0, out var pixelX1, out var pixelY1))
                return false;

            var key = CacheKey(state.Pack.Path, index);
            if (!TileCache.TryGetValue(key, out var cached) || cached.Texture == null)
                return false;

            cached.Stamp = ++_stamp;

            var go = new GameObject($"DMExt Tile {col},{row}", typeof(RectTransform), typeof(CanvasRenderer));
            go.layer = state.Root.layer;
            go.transform.SetParent(state.Root.transform, false);

            var tileRect = (RectTransform)go.transform;
            tileRect.anchorMin = new Vector2(
                (float)pixelX0 / state.Pack.Width,
                1f - (float)pixelY1 / state.Pack.Height);
            tileRect.anchorMax = new Vector2(
                (float)pixelX1 / state.Pack.Width,
                1f - (float)pixelY0 / state.Pack.Height);
            tileRect.offsetMin = Vector2.zero;
            tileRect.offsetMax = Vector2.zero;
            tileRect.localScale = Vector3.one;
            tileRect.localRotation = Quaternion.identity;

            var raw = go.AddComponent(_rawImageType);
            _rawTextureProperty?.SetValue(raw, cached.Texture);
            _rawRaycastProperty?.SetValue(raw, false);

            // NVTILES2 can begin/end in the middle of a source tile. Keep the original PNG
            // untouched and crop only its RawImage UVs at runtime so quality is lossless.
            var rawX0 = state.Pack.OriginX + col * state.Pack.TileSize;
            var rawY0 = state.Pack.OriginY + row * state.Pack.TileSize;
            var uMin = (float)(pixelX0 - rawX0) / state.Pack.TileSize;
            var uWidth = (float)(pixelX1 - pixelX0) / state.Pack.TileSize;
            var bottomCrop = rawY0 + state.Pack.TileSize - pixelY1;
            var vMin = (float)bottomCrop / state.Pack.TileSize;
            var vHeight = (float)(pixelY1 - pixelY0) / state.Pack.TileSize;
            _rawUvRectProperty?.SetValue(raw, new Rect(uMin, vMin, uWidth, vHeight));

            var tint = GetLayerColor(state.Layer.Target);
            if (tint.HasValue)
                _rawColorProperty?.SetValue(raw, tint.Value);

            go.SetActive(false);
            state.Views[index] = go;
            state.PendingReveal.Add(index);
            if (state.PendingRevealSinceFrame < 0)
                state.PendingRevealSinceFrame = Time.frameCount;
            return true;
        }

        private static void RemoveUndesiredViews(LayerState state, List<int> desired)
        {
            if (state.Views.Count == 0)
                return;

            state.ScratchRemove.Clear();
            foreach (var index in state.Views.Keys)
            {
                if (!desired.Contains(index))
                    state.ScratchRemove.Add(index);
            }

            for (var i = 0; i < state.ScratchRemove.Count; i++)
            {
                var index = state.ScratchRemove[i];
                var go = state.Views[index];
                state.Views.Remove(index);
                state.PendingReveal.Remove(index);
                state.RevealRequired.Remove(index);
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
            state.ScratchRemove.Clear();
        }

        private static void ClearViews(LayerState state)
        {
            foreach (var go in state.Views.Values)
            {
                if (go != null)
                    UnityEngine.Object.Destroy(go);
            }
            state.Views.Clear();
            state.Desired.Clear();
            state.RevealRequired.Clear();
            state.PendingReveal.Clear();
            state.PendingRevealSinceFrame = -1;
            state.InitialRevealComplete = false;
            if (state.Root != null)
                state.Root.SetActive(false);
        }

        private static void TrimTileCache()
        {
            if (TileCache.Count <= MaxCachedTiles && _cacheBytes <= MaxCacheBytes)
                return;

            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in Layers)
            {
                foreach (var index in state.Views.Keys)
                    active.Add(CacheKey(state.Pack.Path, index));
            }

            foreach (var pair in TileCache
                         .Where(p => !active.Contains(p.Key))
                         .OrderBy(p => p.Value.Stamp)
                         .ToList())
            {
                if (TileCache.Count <= MaxCachedTiles && _cacheBytes <= MaxCacheBytes)
                    break;

                RemoveCachedTile(pair.Key);
            }
        }

        private static void RemoveLayer(object mapLayer, bool destroyCachedTiles)
        {
            var state = FindState(mapLayer);
            if (state != null)
                RemoveState(state, destroyCachedTiles);
        }

        private static LayerState FindState(object mapLayer)
        {
            foreach (var state in Layers)
            {
                if (ReferenceEquals(state.Layer.Target, mapLayer))
                    return state;
            }
            return null;
        }

        private static void RemoveState(LayerState state, bool destroyCachedTiles)
        {
            if (state == null)
                return;

            ClearViews(state);
            if (state.Root != null)
                UnityEngine.Object.Destroy(state.Root);

            Layers.Remove(state);

            if (destroyCachedTiles && state.Pack != null)
                ReleasePackTiles(state.Pack.Path);
        }

        private static void PruneDeadLayers()
        {
            for (var i = Layers.Count - 1; i >= 0; i--)
            {
                var state = Layers[i];
                if (state.Layer.IsAlive && state.Layer.Target != null)
                    continue;

                ClearViews(state);
                if (state.Root != null)
                    UnityEngine.Object.Destroy(state.Root);
                Layers.RemoveAt(i);
            }
        }

        private static void ReleasePackTiles(string packPath)
        {
            var prefix = packPath + "|";
            foreach (var key in TileCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                RemoveCachedTile(key);
        }

        private static void Reset(string reason)
        {
            _generation++;
            _nextDecodeStartFrame = Time.frameCount + InitialDecodeQuietFrames;

            if (_worker != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_worker);
            _worker = null;

            if (_prewarmWorker != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_prewarmWorker);
            _prewarmWorker = null;

            foreach (var state in Layers.ToList())
            {
                ClearViews(state);
                if (state.Root != null)
                    UnityEngine.Object.Destroy(state.Root);
            }
            Layers.Clear();

            foreach (var key in TileCache.Keys.ToList())
                RemoveCachedTile(key);

            TileCache.Clear();
            _cacheBytes = 0;
            Packs.Clear();
            _selectionSignature = null;

            // Do not synchronously abort an in-flight local decode. Its generation is now stale,
            // so it will discard its texture and clean up the temporary PNG when it completes.
            Plugin.Debug($"High-res tile cache cleared: {reason}.");
        }

        private static string CacheKey(string packPath, int index) => packPath + "|" + index;

        private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0)
                    throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }
    }
}