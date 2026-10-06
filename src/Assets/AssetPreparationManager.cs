using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace DynamicMapsExtended
{
    // First-run asset preparation is intentionally implemented inside this DLL.
    // No PowerShell, curl, cmd, helper EXE, or child process is launched.
    internal static class AssetPreparationManager
    {
        private const string ManifestRelative = "Assets/DMEXT-ASSET-MANIFEST-v2.0.0.json";
        private const string StructureRelative = "Assets/DMEXT-UPSTREAM-STRUCTURE-v2.0.0.json";

        private static readonly object StatusLock = new object();
        private static LockedAssetInstaller _installer;
        private static CancellationTokenSource _cancel;
        private static Task _task;
        private static LockedAssetInstaller.ProgressSnapshot _snapshot = new LockedAssetInstaller.ProgressSnapshot();
        private static bool _initialized;
        private static bool _ready;
        private static int _terminalRefreshHandled;
        private static readonly HashSet<string> ManagedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int _pausedForRaid;
        private static float _nextRaidCheckAt;
        private static float _hideOverlayAt;
        private static Texture2D _overlayTexture;
        private static Texture2D _barTexture;
        private static Texture2D _barBackTexture;

        private static string ManifestPath => Path.Combine(Plugin.ExtensionRoot, ManifestRelative.Replace('/', Path.DirectorySeparatorChar));
        private static string StructurePath => Path.Combine(Plugin.ExtensionRoot, StructureRelative.Replace('/', Path.DirectorySeparatorChar));
        private static string StateRoot => PersistentAssetCache.StateRoot;
        private static string LogPath => Path.Combine(StateRoot, "asset-preparation.log");

        internal static bool Ready => _ready;
        internal static bool Running => _task != null && !_task.IsCompleted;
        internal static bool Failed
        {
            get { lock (StatusLock) return _snapshot.Finished && !_snapshot.AllReady; }
        }

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                CleanupLegacyBootstrap();
                if (!File.Exists(ManifestPath))
                {
                    SetTerminalFailure("Asset manifest is missing: " + ManifestPath);
                    return;
                }
                if (!File.Exists(StructurePath))
                {
                    SetTerminalFailure("Upstream structure lock is missing: " + StructurePath);
                    return;
                }

                Directory.CreateDirectory(StateRoot);
                var manifestHash = Sha256Files(ManifestPath, StructurePath);
                _cancel = new CancellationTokenSource();
                _installer = new LockedAssetInstaller(
                    Plugin.ExtensionRoot,
                    ManifestPath,
                    manifestHash,
                    _cancel.Token,
                    OnProgress,
                    () => Volatile.Read(ref _pausedForRaid) != 0);

                // One-time upgrade from older test builds: adopt verified NVTILES2 packs/state from
                // either the temporary sibling-cache layout or older direct Maps/ packs into
                // DynamicMaps-Extended/AssetCache before checking readiness. This migration is
                // local-only and never uses the network.
                _installer.MigrateLegacyLocalCache();

                ManagedOutputPaths.Clear();
                foreach (var entry in _installer.Manifest.Assets)
                {
                    if (entry?.Preview != null && !string.IsNullOrWhiteSpace(entry.Preview.OutputPath))
                        ManagedOutputPaths.Add(ToAbsolute(entry.Preview.OutputPath));
                    if (entry?.Warm != null && !string.IsNullOrWhiteSpace(entry.Warm.OutputPath))
                        ManagedOutputPaths.Add(ToAbsolute(entry.Warm.OutputPath));
                    if (entry?.Pack != null && !string.IsNullOrWhiteSpace(entry.Pack.OutputPath))
                        ManagedOutputPaths.Add(ToAbsolute(entry.Pack.OutputPath));
                }

                if (_installer.GlobalReadyMarkerMatches())
                {
                    _ready = true;
                    lock (StatusLock)
                    {
                        _snapshot = new LockedAssetInstaller.ProgressSnapshot
                        {
                            State = "ready", Stage = "Ready", Detail = "Verified local map cache is ready.", Percent = 100,
                            Completed = _installer.Manifest.Assets.Count, Total = _installer.Manifest.Assets.Count, Finished = true, AllReady = true
                        };
                    }
                    Interlocked.Exchange(ref _terminalRefreshHandled, 1);
                    Plugin.Log?.LogInfo("DynamicMaps Extended raster assets are already prepared. No download needed.");
                    return;
                }

                lock (StatusLock)
                {
                    _snapshot = new LockedAssetInstaller.ProgressSnapshot
                    {
                        State = "starting", Stage = "Checking local map cache", Detail = "Looking for previously prepared assets...",
                        Percent = 0, Completed = 0, Total = _installer.Manifest.Assets.Count
                    };
                }

                _task = Task.Run(() => _installer.Run(), _cancel.Token);
                Plugin.Log?.LogInfo("DynamicMaps Extended built-in first-run asset preparation started. No PowerShell/curl/external process is used.");
            }
            catch (Exception e)
            {
                SetTerminalFailure("Could not initialize the built-in asset installer: " + e.Message);
            }
        }

        internal static void Update()
        {
            if (!_initialized) return;

            if (Time.unscaledTime >= _nextRaidCheckAt)
            {
                _nextRaidCheckAt = Time.unscaledTime + 0.5f;
                Interlocked.Exchange(ref _pausedForRaid, string.IsNullOrEmpty(DynamicMapsBridge.GetCurrentLocationOrNull()) ? 0 : 1);
            }

            LockedAssetInstaller.ProgressSnapshot snap;
            lock (StatusLock) snap = _snapshot;

            // Handle a completed preparation run exactly once on the Unity/main thread.
            // Older test builds notified after every layer and then notified again when the
            // terminal snapshot was observed, which repeatedly reset raster/high-res caches.
            if (snap.Finished && Interlocked.CompareExchange(ref _terminalRefreshHandled, 1, 0) == 0)
            {
                try { MapStyleManager.OnAssetsPrepared(); }
                catch (Exception e) { Plugin.Debug("Asset availability refresh failed: " + e.Message); }

                if (snap.AllReady)
                {
                    _ready = true;
                    _hideOverlayAt = Time.unscaledTime + 3f;
                    Plugin.Log?.LogInfo("DynamicMaps Extended one-time map asset preparation completed successfully.");
                }
                else
                {
                    _hideOverlayAt = Time.unscaledTime + 15f;
                    Plugin.Log?.LogWarning("DynamicMaps Extended map asset preparation is incomplete. Completed maps were kept; missing maps use fallback artwork. Transient failures retry next launch; official map-structure changes require a DynamicMaps Extended update. See " + LogPath);
                }
            }
        }

        internal static void OnGUI()
        {
            if (!_initialized) return;
            LockedAssetInstaller.ProgressSnapshot snap;
            lock (StatusLock) snap = _snapshot;

            if (Volatile.Read(ref _pausedForRaid) != 0) return;
            var visible = Running || !snap.Finished || (_hideOverlayAt > 0f && Time.unscaledTime < _hideOverlayAt);
            if (!visible) return;

            EnsureTextures();
            var width = Mathf.Min(760f, Screen.width - 40f);
            var height = snap.Finished && !snap.AllReady ? 270f : 205f;
            var x = (Screen.width - width) * 0.5f;
            var y = Mathf.Max(30f, Screen.height * 0.12f);
            var box = new Rect(x, y, width, height);
            GUI.DrawTexture(box, _overlayTexture);

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            var bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            var smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUI.Label(new Rect(x + 18f, y + 12f, width - 36f, 30f), "DynamicMaps Extended", titleStyle);

            GUI.Label(new Rect(x + 24f, y + 48f, width - 48f, 28f), string.IsNullOrWhiteSpace(snap.Stage) ? "Preparing map assets..." : snap.Stage, bodyStyle);
            var bar = new Rect(x + 48f, y + 82f, width - 96f, 22f);
            GUI.DrawTexture(bar, _barBackTexture);
            var clamped = Mathf.Clamp01(snap.Percent / 100f);
            if (clamped > 0f) GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * clamped, bar.height), _barTexture);
            GUI.Label(bar, snap.Percent.ToString(CultureInfo.InvariantCulture) + "%", smallStyle);

            var current = string.IsNullOrWhiteSpace(snap.Asset) ? snap.Detail : snap.Asset + (string.IsNullOrWhiteSpace(snap.Detail) ? string.Empty : "\n" + snap.Detail);
            GUI.Label(new Rect(x + 24f, y + 112f, width - 48f, snap.Finished && !snap.AllReady ? 62f : 54f), current, smallStyle);

            var footer = snap.Finished
                ? (snap.AllReady ? "Ready. Future launches use the local verified cache." : "Completed maps were kept. Missing maps use fallback artwork.\nTransient failures retry next launch; official map-structure changes require a DynamicMaps Extended update.\nDetailed log: " + LogPath)
                : "Maps: " + snap.Completed.ToString(CultureInfo.InvariantCulture) + "/" + snap.Total.ToString(CultureInfo.InvariantCulture) +
                  (snap.Failed > 0 ? "  |  unavailable: " + snap.Failed.ToString(CultureInfo.InvariantCulture) : string.Empty) +
                  "\nOne-time preparation. Closing SPT is safe; partial downloads resume next launch.";
            GUI.Label(new Rect(x + 24f, y + 178f, width - 48f, snap.Finished && !snap.AllReady ? 82f : 30f), footer, smallStyle);
        }

        internal static void Shutdown()
        {
            try { _cancel?.Cancel(); } catch { }
            try { if (_task != null && !_task.IsCompleted) _task.Wait(750); } catch { }
            _task = null;
            _installer = null;
            _cancel?.Dispose();
            _cancel = null;

            if (_overlayTexture != null) UnityEngine.Object.Destroy(_overlayTexture);
            if (_barTexture != null) UnityEngine.Object.Destroy(_barTexture);
            if (_barBackTexture != null) UnityEngine.Object.Destroy(_barBackTexture);
            _overlayTexture = null; _barTexture = null; _barBackTexture = null;
            ManagedOutputPaths.Clear();
            Interlocked.Exchange(ref _terminalRefreshHandled, 0);
        }

        internal static bool KnownSatelliteChoice(MapStyleKey key)
        {
            switch (key)
            {
                case MapStyleKey.GroundZero:
                case MapStyleKey.Customs:
                case MapStyleKey.Factory:
                case MapStyleKey.Woods:
                case MapStyleKey.Interchange:
                case MapStyleKey.InterchangeManimal:
                case MapStyleKey.Labs:
                case MapStyleKey.LabsManimal:
                case MapStyleKey.Icebreaker:
                case MapStyleKey.Reserve:
                case MapStyleKey.Shoreline:
                case MapStyleKey.Labyrinth:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsRuntimeManagedOutput(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath)) return false;
            try { return ManagedOutputPaths.Contains(Path.GetFullPath(absolutePath)); }
            catch { return false; }
        }

        private static string ToAbsolute(string relativePath)
            => Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        internal static string Describe()
        {
            lock (StatusLock)
                return $"ready={_ready}, running={Running}, state={_snapshot.State}, stage={_snapshot.Stage}, percent={_snapshot.Percent}, asset={_snapshot.Asset}, completed={_snapshot.Completed}/{_snapshot.Total}, failed={_snapshot.Failed}, assetCache={PersistentAssetCache.Root}";
        }

        private static void OnProgress(LockedAssetInstaller.ProgressSnapshot snapshot)
        {
            lock (StatusLock) _snapshot = snapshot;
        }

        private static void CleanupLegacyBootstrap()
        {
            try
            {
                var legacy = Path.Combine(Plugin.ExtensionRoot, "AssetBootstrap");
                if (Directory.Exists(legacy))
                {
                    Directory.Delete(legacy, true);
                    Plugin.Log?.LogInfo("DynamicMaps Extended removed the obsolete external AssetBootstrap folder from an older test build.");
                }
            }
            catch (Exception e)
            {
                Plugin.Debug("Legacy AssetBootstrap cleanup skipped: " + e.Message);
            }
        }

        private static void SetTerminalFailure(string message)
        {
            lock (StatusLock)
            {
                _snapshot = new LockedAssetInstaller.ProgressSnapshot
                {
                    State = "failed", Stage = "Asset preparation failed", Detail = message ?? "Unknown error.",
                    Percent = 0, Completed = 0, Total = 0, Failed = 1, Finished = true, AllReady = false
                };
            }
            try
            {
                Directory.CreateDirectory(StateRoot);
                File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] FATAL: " + (message ?? "Unknown error.") + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            Plugin.Log?.LogError("DynamicMaps Extended asset preparation: " + message);
        }

        private static string Sha256Files(params string[] paths)
        {
            using var sha = SHA256.Create();
            using var stream = new MemoryStream();
            foreach (var path in paths)
            {
                var name = Encoding.UTF8.GetBytes(Path.GetFileName(path));
                stream.Write(name, 0, name.Length);
                stream.WriteByte(0);
                var bytes = File.ReadAllBytes(path);
                stream.Write(bytes, 0, bytes.Length);
                stream.WriteByte(0);
            }
            stream.Position = 0;
            var hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static void EnsureTextures()
        {
            if (_overlayTexture == null)
            {
                _overlayTexture = new Texture2D(1, 1); _overlayTexture.SetPixel(0, 0, new Color(0.04f, 0.04f, 0.04f, 0.94f)); _overlayTexture.Apply();
            }
            if (_barBackTexture == null)
            {
                _barBackTexture = new Texture2D(1, 1); _barBackTexture.SetPixel(0, 0, new Color(0.16f, 0.16f, 0.16f, 1f)); _barBackTexture.Apply();
            }
            if (_barTexture == null)
            {
                _barTexture = new Texture2D(1, 1); _barTexture.SetPixel(0, 0, new Color(0.55f, 0.75f, 0.30f, 1f)); _barTexture.Apply();
            }
        }
    }
}