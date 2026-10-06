using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace DynamicMapsExtended
{
    // Medium-resolution whole-floor cache prepared specifically for the first in-raid map/minimap open.
    //
    // The source package contains one ~3K warm atlas for the normal/default floor of each raster map.
    // Only the atlas matching the current raid + selected artwork is decoded. It is stored under the
    // normal preview cache key, so DynamicMaps automatically receives the clearer sprite without a new
    // rendering path. Max-resolution .tiles still refine deep zoom on top of it.
    internal static class RaidWarmCacheManager
    {
        private const float InitialRaidQuietSeconds = 2.0f;
        private const float StableFrameLimitSeconds = 0.040f;
        private const int StableFramesRequired = 12;
        private const float MaximumStableWaitSeconds = 12.0f;
        private const int CenterTilesToPrewarm = 16;
        private const int AlreadyWarmLongEdge = 2800;

        private static Coroutine _worker;
        private static int _generation;
        private static string _activePreview;
        private static string _status = "idle";

        internal static void BeginRaidWarmup(string reason)
        {
            if (Plugin.Instance == null)
                return;

            CancelWorker();
            var generation = ++_generation;
            _status = "waiting for stable raid frames";
            _worker = Plugin.Instance.StartCoroutine(WarmCurrentRaid(generation, reason));
            Plugin.Debug($"Raid warm cache scheduled: reason={reason}.");
        }

        internal static void OnStyleChanged()
        {
            if (string.IsNullOrEmpty(DynamicMapsBridge.GetCurrentLocationOrNull()))
                return;

            BeginRaidWarmup("artwork style changed in raid");
        }

        internal static void OnRaidEnd()
        {
            _generation++;
            CancelWorker();
            _activePreview = null;
            _status = "idle";
        }

        internal static void Shutdown() => OnRaidEnd();

        internal static string Describe()
            => $"status={_status}, activePreview={(_activePreview == null ? "<none>" : Path.GetFileName(_activePreview))}";

        private static void CancelWorker()
        {
            if (_worker != null && Plugin.Instance != null)
                Plugin.Instance.StopCoroutine(_worker);
            _worker = null;
        }

        private static IEnumerator WarmCurrentRaid(int generation, string reason)
        {
            var started = Time.realtimeSinceStartup;
            while (generation == _generation && Time.realtimeSinceStartup - started < InitialRaidQuietSeconds)
                yield return null;

            if (generation != _generation)
                yield break;

            // Do not compete with spawn/preview work. Require a short run of healthy frames before
            // starting a 3K image decode, but do not wait forever on a permanently low-FPS raid.
            var stableFrames = 0;
            var stableWaitStarted = Time.realtimeSinceStartup;
            while (generation == _generation && stableFrames < StableFramesRequired &&
                   Time.realtimeSinceStartup - stableWaitStarted < MaximumStableWaitSeconds)
            {
                var dt = Time.unscaledDeltaTime;
                if (!RasterWarmupManager.IsBusy && dt > 0f && dt <= StableFrameLimitSeconds)
                    stableFrames++;
                else
                    stableFrames = 0;
                yield return null;
            }

            if (generation != _generation)
                yield break;

            if (stableFrames < StableFramesRequired)
            {
                _status = "skipped because raid stayed busy";
                _worker = null;
                Plugin.Debug("Raid warm cache skipped: the raid never produced enough healthy frames; avoiding a background texture upload.");
                yield break;
            }

            if (!TryResolveWarmTarget(out var previewPath, out var warmPath, out var label))
            {
                _status = "not applicable for current artwork";
                _worker = null;
                Plugin.Debug("Raid warm cache: current raid/artwork has no raster warm atlas; native/vector artwork stays authoritative.");
                yield break;
            }

            _activePreview = previewPath;

            if (RasterSpritePatch.TryGetCached(previewPath, out var existing) && existing != null && existing.texture != null &&
                Math.Max(existing.texture.width, existing.texture.height) >= AlreadyWarmLongEdge)
            {
                _status = "warm atlas already cached";
                TilePackManager.PrewarmCenterTiles(previewPath, CenterTilesToPrewarm);
                _worker = null;
                Plugin.Debug($"Raid warm cache already ready: {label}, {RasterSpritePatch.DescribeSprite(existing)}");
                yield break;
            }

            if (string.IsNullOrEmpty(warmPath) || !File.Exists(warmPath))
            {
                _status = "warm atlas missing";
                _worker = null;
                Plugin.Log?.LogWarning($"Raid warm atlas is missing for {label}: {warmPath}");
                yield break;
            }

            // If the player opened the map during our quiet window, let that foreground preview
            // request finish first. The warm atlas will then upgrade it instead of competing with it.
            var foregroundWaitStarted = Time.realtimeSinceStartup;
            while (generation == _generation && RasterWarmupManager.IsBusy &&
                   Time.realtimeSinceStartup - foregroundWaitStarted < 3.0f)
                yield return null;

            if (generation != _generation)
                yield break;

            _status = "loading whole-floor warm atlas";
            var sw = Stopwatch.StartNew();
            Texture2D texture = null;

            using (var request = UnityWebRequestTexture.GetTexture(new Uri(warmPath).AbsoluteUri, true))
            {
                yield return request.SendWebRequest();

                if (generation != _generation)
                {
                    sw.Stop();
                    yield break;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    sw.Stop();
                    _status = "warm atlas load failed";
                    _worker = null;
                    Plugin.Log?.LogWarning($"Raid warm atlas load failed for {warmPath}: {request.error}");
                    yield break;
                }

                texture = DownloadHandlerTexture.GetContent(request);
            }

            if (texture == null)
            {
                sw.Stop();
                _status = "warm atlas decode returned no texture";
                _worker = null;
                Plugin.Log?.LogWarning($"Raid warm atlas decode returned no texture: {warmPath}");
                yield break;
            }

            if (generation != _generation)
            {
                UnityEngine.Object.Destroy(texture);
                sw.Stop();
                yield break;
            }

            texture.name = "DMExtWarm:" + Path.GetFileName(warmPath);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            texture.anisoLevel = 0;

            long fileBytes = 0;
            try { fileBytes = new FileInfo(warmPath).Length; } catch { }

            var sprite = RasterSpritePatch.StoreOrUpgradeAsyncTexture(
                previewPath,
                texture,
                fileBytes,
                sw.ElapsedMilliseconds,
                $"raid-start warm atlas ({label})");
            sw.Stop();

            if (sprite != null)
            {
                StyleRefreshManager.ApplyReadyRaster(previewPath);
                _status = "whole-floor warm atlas cached";
                Plugin.Debug($"Raid warm cache ready: {label}, {RasterSpritePatch.DescribeSprite(sprite)}, elapsed={sw.ElapsedMilliseconds} ms.");

                // After the complete floor is already clear, quietly cache a small centre group from
                // the real max-resolution pack. This makes the first deep zoom pop in immediately.
                TilePackManager.PrewarmCenterTiles(previewPath, CenterTilesToPrewarm);
            }
            else
            {
                _status = "warm atlas cache store failed";
            }

            _worker = null;
        }

        private static bool TryResolveWarmTarget(out string previewPath, out string warmPath, out string label)
        {
            previewPath = null;
            warmPath = null;
            label = null;

            var rawLocation = DynamicMapsBridge.GetCurrentLocationOrNull();
            if (string.IsNullOrWhiteSpace(rawLocation))
                return false;

            var location = rawLocation.Trim().ToLowerInvariant();
            string sourcePath;

            if (location == "sandbox" || location == "sandbox_high" || location == "groundzero" || location == "ground_zero")
            {
                label = "Ground Zero";
                sourcePath = "Maps/GroundZero_TarkovDev/Layers/GroundZero-Ground_Level.svg";
            }
            else if (location == "bigmap" || location == "customs")
            {
                label = "Customs";
                sourcePath = "Maps/Customs_TarkovDev/Layers/Customs-Ground_Level.svg";
            }
            else if (FactoryClassicBridge.IsFactory(location))
            {
                if (FactoryClassicBridge.IsClassicChosen || FactoryClassicBridge.ClassicSceneLoaded)
                    return false;

                label = "Factory";
                sourcePath = "Maps/Factory_TarkovDev/Layers/Factory-Ground_Floor.svg";
            }
            else if (location == "woods")
            {
                label = "Woods";
                sourcePath = "Maps/Woods_TarkovData/Layers/Woods-Ground_Level.svg";
            }
            else if (location == "interchange")
            {
                var mv = MapVariantsBridge.Resolve("Interchange");
                var manimal = mv.ApiAvailable && mv.Managed && mv.Decided && mv.IsBackport &&
                              PluginDetection.Loaded(Plugin.ManimalInterchangeGuid) &&
                              ExtensionAssetAvailability.ManimalInterchangeReady;

                label = manimal ? "Interchange (Manimal)" : "Interchange";
                sourcePath = manimal
                    ? ExtensionPath("Maps/Interchange_Backport/Abstract/Interchange-Ground.svg")
                    : "Maps/Interchange_TarkovDev/Layers/Interchange-Ground_Level.svg";
            }
            else if (location == "laboratory" || location == "lab")
            {
                label = "The Lab";
                sourcePath = "Maps/Labs_TarkovDev/Layers/Labs_First_Level.svg";
            }
            else if (location == "icebreaker")
            {
                label = "Icebreaker";
                sourcePath = ExtensionPath("Maps/Icebreaker/Layers/06-Infirmary.png");
            }
            else if (location == "rezervbase" || location == "reserve")
            {
                if (!MapStyleManager.WantsSatelliteMapDef(MapStyleKey.Reserve))
                    return false;

                label = "Reserve";
                sourcePath = ExtensionPath("Maps/Reserve_Satellite/Layers/Reserve-Ground.png");
            }
            else if (location == "shoreline")
            {
                label = "Shoreline";
                sourcePath = "Maps/Shoreline_TarkovData/Layers/Shoreline-Ground_Level.svg";
            }
            else if (location == "labyrinth")
            {
                label = "Labyrinth";
                sourcePath = "Maps/Labyrinth/Layers/Labyrinth.svg";
            }
            else
            {
                return false;
            }

            var resolution = MapStyleManager.Resolve(sourcePath);
            if (!RasterSpritePatch.IsExtensionOwnedRaster(resolution.ResolvedPath))
                return false;

            var absolutePreview = RasterSpritePatch.ResolveAbsolutePath(resolution.ResolvedPath);
            if (string.IsNullOrEmpty(absolutePreview))
                return false;

            var candidateWarm = Path.ChangeExtension(absolutePreview, ".warm.png");
            if (!File.Exists(candidateWarm))
                return false;

            previewPath = resolution.ResolvedPath;
            warmPath = candidateWarm;
            return true;
        }

        private static string ExtensionPath(string relative)
            => Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    [HarmonyPatch]
    internal static class RaidWarmGameStartedPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("EFT.GameWorld")
                ?? throw new MissingMemberException("EFT.GameWorld not found");
            return AccessTools.Method(type, "OnGameStarted")
                ?? throw new MissingMethodException(type.FullName, "OnGameStarted");
        }

        private static void Postfix()
        {
            RaidWarmCacheManager.BeginRaidWarmup("GameWorld.OnGameStarted");
        }
    }
}