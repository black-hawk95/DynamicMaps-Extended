using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicMapsExtended
{
    // On-demand raster loader. The currently selected floor always wins; lower/underneath
    // layers are deliberately background priority so opening the map never starts a burst.
    //
    // An optional sibling *.warm.png whole-floor atlas is supported. The normal small
    // preview is still shown first for instant responsiveness, then ONLY the selected/foreground
    // floor is upgraded asynchronously to the warm atlas. The high-resolution tile system waits
    // for this uniform whole-floor upgrade before showing max-resolution detail, avoiding the
    // distracting sharp rectangle over a blurry map.
    internal static class RasterWarmupManager
    {
        private const int WarmPreviewLongEdge = 2500;
        private const int WarmUpgradeQuietFrames = 2;
        private const float WarmUpgradeHealthyFrameSeconds = 0.045f;
        private const float WarmUpgradeMaximumWaitSeconds = 1.5f;

        private sealed class WorkItem
        {
            internal readonly string Path;
            internal readonly string Reason;
            internal readonly int Generation;
            internal readonly bool Background;

            internal WorkItem(string path, string reason, int generation, bool background)
            {
                Path = path;
                Reason = reason;
                Generation = generation;
                Background = background;
            }
        }

        private static readonly Queue<WorkItem> ForegroundQueue = new Queue<WorkItem>();
        private static readonly Queue<WorkItem> BackgroundQueue = new Queue<WorkItem>();
        private static readonly HashSet<string> Queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> WarmUnavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static Coroutine _worker;
        private static int _generation;
        private static string _selectionSignature;

        internal static void Shutdown()
        {
            _selectionSignature = null;
            ResetQueue();
        }

        internal static void OnRaidEnd()
        {
            _selectionSignature = null;
            ResetQueue();
            Plugin.Debug("Raster queue cleared at raid end.");
        }

        internal static void BeginMapSelection(IEnumerable<string> rasterPaths)
        {
            var normalized = new List<string>();
            if (rasterPaths != null)
            {
                foreach (var path in rasterPaths)
                {
                    var absolute = RasterSpritePatch.ResolveAbsolutePath(path);
                    if (!string.IsNullOrEmpty(absolute))
                        normalized.Add(Path.GetFullPath(absolute));
                }
            }

            normalized.Sort(StringComparer.OrdinalIgnoreCase);
            var signature = string.Join("|", normalized);
            if (string.Equals(signature, _selectionSignature, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Debug("Raster queue map selection unchanged; preserving in-flight/cached work.");
                return;
            }

            ResetQueue();
            _selectionSignature = signature;
        }

        internal static void OnStyleChanged(string reason)
        {
            _selectionSignature = null;
            ResetQueue();
            Plugin.Debug($"Raster queue reset for {reason}; selected floor will be loaded first.");
        }

        internal static void QueuePath(string imagePath, string reason, bool background = false)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !RasterSpritePatch.IsExtensionOwnedRaster(imagePath))
                return;

            if (RasterSpritePatch.IsCached(imagePath) || !Queued.Add(imagePath))
                return;

            var item = new WorkItem(imagePath, reason, _generation, background);
            if (background)
                BackgroundQueue.Enqueue(item);
            else
                ForegroundQueue.Enqueue(item);

            EnsureWorker();
        }

        internal static bool IsBusy => _worker != null || ForegroundQueue.Count > 0 || BackgroundQueue.Count > 0;

        internal static string DescribeQueue()
            => $"foreground={ForegroundQueue.Count}, background={BackgroundQueue.Count}, tracked={Queued.Count}, worker={(_worker != null ? "running" : "idle")}";

        // Used by TilePackManager to decide whether max-resolution detail should wait for a uniform
        // whole-floor atlas. This is deliberately just a local sibling-file check: no manifest parse,
        // no network access, and no work is started here.
        internal static bool HasWarmAtlas(string imagePath)
            => TryResolveWarmAtlasPath(imagePath, out _);

        internal static bool IsWarmSprite(Sprite sprite)
            => sprite != null && sprite.texture != null &&
               Math.Max(sprite.texture.width, sprite.texture.height) >= WarmPreviewLongEdge;

        internal static bool ShouldDeferTilesForWarmAtlas(string imagePath, Sprite currentSprite)
        {
            if (IsWarmSprite(currentSprite) || string.IsNullOrWhiteSpace(imagePath) || WarmUnavailable.Contains(imagePath))
                return false;
            return HasWarmAtlas(imagePath);
        }

        internal static bool TryResolveWarmAtlasPath(string imagePath, out string warmPath)
        {
            warmPath = null;
            if (string.IsNullOrWhiteSpace(imagePath))
                return false;

            try
            {
                var absolute = RasterSpritePatch.ResolveAbsolutePath(imagePath);
                if (string.IsNullOrEmpty(absolute))
                    return false;

                var directory = Path.GetDirectoryName(absolute);
                var stem = Path.GetFileNameWithoutExtension(absolute);
                if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(stem))
                    return false;

                var candidate = Path.Combine(directory, stem + ".warm.png");
                if (!File.Exists(candidate))
                    return false;

                warmPath = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ResetQueue()
        {
            _generation++;
            ForegroundQueue.Clear();
            BackgroundQueue.Clear();
            Queued.Clear();
            WarmUnavailable.Clear();

            if (_worker != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_worker);

            _worker = null;
        }

        private static void EnsureWorker()
        {
            if (_worker == null && Plugin.Instance != null)
                _worker = Plugin.Instance.StartCoroutine(ProcessQueue());
        }

        private static WorkItem DequeueNext()
        {
            if (ForegroundQueue.Count > 0)
                return ForegroundQueue.Dequeue();
            if (BackgroundQueue.Count > 0)
                return BackgroundQueue.Dequeue();
            return null;
        }

        private static IEnumerator ProcessQueue()
        {
            while (ForegroundQueue.Count > 0 || BackgroundQueue.Count > 0)
            {
                var item = DequeueNext();
                if (item == null)
                    break;

                Queued.Remove(item.Path);

                if (item.Generation != _generation || RasterSpritePatch.IsCached(item.Path))
                    continue;

                // Background/underneath floors are intentionally delayed so opening the map and
                // moving the cursor/UI get clean frames before any non-essential texture upload.
                if (item.Background)
                {
                    yield return null;
                    yield return null;

                    // A foreground request may have arrived while we waited. Put this background
                    // item back and service the visible floor first.
                    if (ForegroundQueue.Count > 0)
                    {
                        if (Queued.Add(item.Path))
                            BackgroundQueue.Enqueue(item);
                        continue;
                    }
                }

                var absolute = RasterSpritePatch.ResolveAbsolutePath(item.Path);
                if (string.IsNullOrEmpty(absolute) || !File.Exists(absolute))
                {
                    Plugin.Log?.LogError($"Raster map layer missing: {absolute ?? item.Path}");
                    continue;
                }

                var sw = Stopwatch.StartNew();
                Texture2D texture = null;

                using (var request = UnityWebRequestTexture.GetTexture(new Uri(absolute).AbsoluteUri, true))
                {
                    yield return request.SendWebRequest();

                    if (item.Generation != _generation)
                    {
                        sw.Stop();
                        continue;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        sw.Stop();
                        Plugin.Log?.LogError($"Async raster load failed for {absolute}: {request.error}");
                        continue;
                    }

                    texture = DownloadHandlerTexture.GetContent(request);
                }

                if (texture == null)
                {
                    sw.Stop();
                    Plugin.Log?.LogError($"Async raster load returned no texture: {absolute}");
                    continue;
                }

                if (item.Generation != _generation)
                {
                    UnityEngine.Object.Destroy(texture);
                    sw.Stop();
                    continue;
                }

                texture.name = "DMExt:" + Path.GetFileName(item.Path);
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                texture.anisoLevel = 0;

                var fileBytes = 0L;
                try { fileBytes = new FileInfo(absolute).Length; } catch { }

                var sprite = RasterSpritePatch.StoreAsyncTexture(item.Path, texture, fileBytes, sw.ElapsedMilliseconds, item.Reason);
                sw.Stop();

                if (sprite != null)
                    StyleRefreshManager.ApplyReadyRaster(item.Path);

                // Never finish two normal preview uploads in the same frame.
                yield return null;

                // The selected floor gets a second-stage whole-floor quality upgrade when a bundled
                // warm atlas exists. Underneath/background floors intentionally stay on their small
                // previews so multi-floor maps such as Icebreaker do not consume hundreds of MiB.
                if (!item.Background && item.Generation == _generation &&
                    TryResolveWarmAtlasPath(item.Path, out var warmPath))
                {
                    RasterSpritePatch.TryGetCached(item.Path, out var current);
                    if (!IsWarmSprite(current))
                        yield return UpgradeSelectedFloorToWarmAtlas(item, warmPath);
                }
            }

            _worker = null;
        }

        private static IEnumerator UpgradeSelectedFloorToWarmAtlas(WorkItem item, string warmPath)
        {
            if (item == null || string.IsNullOrEmpty(warmPath) || !File.Exists(warmPath))
                yield break;

            // Give the just-applied low-resolution preview a couple of clean frames first. Then wait
            // briefly for a healthy frame before starting the async 3K decode. If the game stays slow,
            // skip the optional upgrade rather than turning quality into a hitch.
            for (var i = 0; i < WarmUpgradeQuietFrames; i++)
            {
                if (item.Generation != _generation)
                    yield break;
                yield return null;
            }

            var waitStarted = Time.realtimeSinceStartup;
            while (item.Generation == _generation &&
                   Time.realtimeSinceStartup - waitStarted < WarmUpgradeMaximumWaitSeconds)
            {
                var dt = Time.unscaledDeltaTime;
                if (dt > 0f && dt <= WarmUpgradeHealthyFrameSeconds)
                    break;
                yield return null;
            }

            if (item.Generation != _generation)
                yield break;

            // If the game never produced a healthy frame, keep the normal preview. Max-resolution
            // tiles remain available at deep zoom, and we avoid forcing a large texture upload.
            if (Time.unscaledDeltaTime <= 0f || Time.unscaledDeltaTime > WarmUpgradeHealthyFrameSeconds)
            {
                WarmUnavailable.Add(item.Path);
                Plugin.Debug($"Whole-floor warm atlas deferred/skipped on busy frames: {Path.GetFileName(warmPath)}");
                yield break;
            }

            var sw = Stopwatch.StartNew();
            Texture2D warmTexture = null;

            using (var request = UnityWebRequestTexture.GetTexture(new Uri(warmPath).AbsoluteUri, true))
            {
                yield return request.SendWebRequest();

                if (item.Generation != _generation)
                {
                    sw.Stop();
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    sw.Stop();
                    WarmUnavailable.Add(item.Path);
                    Plugin.Log?.LogWarning($"Whole-floor warm atlas load failed for {warmPath}: {request.error}");
                    yield break;
                }

                warmTexture = DownloadHandlerTexture.GetContent(request);
            }

            if (warmTexture == null)
            {
                sw.Stop();
                WarmUnavailable.Add(item.Path);
                Plugin.Log?.LogWarning($"Whole-floor warm atlas decode returned no texture: {warmPath}");
                yield break;
            }

            if (item.Generation != _generation)
            {
                UnityEngine.Object.Destroy(warmTexture);
                sw.Stop();
                yield break;
            }

            warmTexture.name = "DMExtWarm:" + Path.GetFileName(warmPath);
            warmTexture.wrapMode = TextureWrapMode.Clamp;
            warmTexture.filterMode = FilterMode.Bilinear;
            warmTexture.anisoLevel = 0;

            long warmBytes = 0;
            try { warmBytes = new FileInfo(warmPath).Length; } catch { }

            var upgraded = RasterSpritePatch.StoreOrUpgradeAsyncTexture(
                item.Path,
                warmTexture,
                warmBytes,
                sw.ElapsedMilliseconds,
                $"visible-floor whole-map warm atlas ({item.Reason})");
            sw.Stop();

            if (upgraded != null)
            {
                WarmUnavailable.Remove(item.Path);
                StyleRefreshManager.ApplyReadyRaster(item.Path);
                Plugin.Debug($"Whole-floor warm atlas ready: {Path.GetFileName(item.Path)} -> {RasterSpritePatch.DescribeSprite(upgraded)}, elapsed={sw.ElapsedMilliseconds} ms.");
            }

            // Keep the warm image visible for at least one frame before max-resolution tile logic
            // can decide whether the user has zoomed deeply enough to need further refinement.
            yield return null;
        }
    }
}
