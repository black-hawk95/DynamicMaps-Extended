using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DynamicMapsExtended
{
    internal sealed class LockedAssetInstaller
    {
        internal sealed class ProgressSnapshot
        {
            internal string State = "idle";
            internal string Stage = string.Empty;
            internal string Asset = string.Empty;
            internal string Detail = string.Empty;
            internal int Percent;
            internal int Completed;
            internal int Total;
            internal int Failed;
            internal bool Finished;
            internal bool AllReady;
        }

        private sealed class AssetDownloadException : Exception
        {
            internal readonly bool NetworkWide;
            internal AssetDownloadException(string message, bool networkWide, Exception inner = null) : base(message, inner) { NetworkWide = networkWide; }
        }

        // Binary and decoded-pixel fingerprints are retained for diagnostics/provenance.
        // Runtime acceptance is based on the official map STRUCTURE (tile path, transform,
        // bounds, rotation, zoom metadata, layer path) plus the exact expected tile geometry.
        // Tarkov.dev currently mutates raster artwork in place, so an old pixel hash cannot be
        // used as a permanent release lock without redistributing the old artwork.
        private sealed class SourceFingerprintResult
        {
            internal string BinaryLock;
            internal string VisualLock;
            internal int Count;
            internal long TotalBytes;
            internal readonly Dictionary<string, string> VisualKeyByBinaryHash = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private readonly struct PayloadReference
        {
            internal readonly uint Offset;
            internal readonly uint Length;
            internal PayloadReference(uint offset, uint length) { Offset = offset; Length = length; }
        }

        private readonly string _pluginRoot;
        private readonly string _manifestPath;
        private readonly string _cacheRoot;
        private readonly string _stateRoot;
        private readonly string _stagingRoot;
        private readonly string _logPath;
        private readonly string _globalReadyPath;
        private readonly string _manifestHash;
        private readonly RuntimeAssetManifest _manifest;
        private readonly UpstreamStructureLock _structureLock;
        private readonly string _structurePath;
        private readonly Dictionary<string, string> _structureProblems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _structureChecked;
        private readonly CancellationToken _token;
        private readonly Action<ProgressSnapshot> _progress;
        private readonly Func<bool> _pauseRequested;
        private readonly object _logLock = new object();
        private int _currentIndex;
        private int _failedAssets;

        internal LockedAssetInstaller(
            string pluginRoot,
            string manifestPath,
            string manifestHash,
            CancellationToken token,
            Action<ProgressSnapshot> progress,
            Func<bool> pauseRequested)
        {
            _pluginRoot = pluginRoot;
            _manifestPath = manifestPath;
            _manifestHash = manifestHash;
            _token = token;
            _progress = progress;
            _pauseRequested = pauseRequested;
            _cacheRoot = PersistentAssetCache.Root;
            _stateRoot = PersistentAssetCache.StateRoot;
            _stagingRoot = PersistentAssetCache.StagingRoot;
            _logPath = Path.Combine(_stateRoot, "asset-preparation.log");
            _manifest = JsonConvert.DeserializeObject<RuntimeAssetManifest>(File.ReadAllText(manifestPath));
            if (_manifest == null || !string.Equals(_manifest.Format, "DynamicMapsExtended.RuntimeAssetManifest.v1", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported runtime asset manifest.");
            if (_manifest.Assets == null || _manifest.Assets.Count != 42)
                throw new InvalidDataException("Runtime asset manifest must contain 42 raster layers.");

            _structurePath = Path.Combine(Path.GetDirectoryName(manifestPath) ?? pluginRoot, "DMEXT-UPSTREAM-STRUCTURE-v2.0.0.json");
            if (!File.Exists(_structurePath)) throw new InvalidDataException("Upstream structure lock is missing: " + _structurePath);
            _structureLock = JsonConvert.DeserializeObject<UpstreamStructureLock>(File.ReadAllText(_structurePath));
            if (_structureLock == null || !string.Equals(_structureLock.Format, "DynamicMapsExtended.UpstreamStructureLock.v1", StringComparison.Ordinal) || _structureLock.Maps == null || _structureLock.Maps.Count == 0)
                throw new InvalidDataException("Unsupported upstream structure lock.");

            _globalReadyPath = Path.Combine(_stateRoot, (_manifest.AssetSet ?? "assets") + ".ready");
        }

        internal RuntimeAssetManifest Manifest => _manifest;
        internal string LogPath => _logPath;
        internal string GlobalReadyPath => _globalReadyPath;

        internal void MigrateLegacyLocalCache()
        {
            PersistentAssetCache.EnsureDirectories();

            var migratedPacks = 0;
            var migratedMarkers = 0;
            var migratedStaging = 0;

            // v1.5 persistent-cache test builds stored the large cache in a sibling folder:
            // BepInEx/plugins/DynamicMaps-Extended-AssetCache. Move that cache back under the
            // single mod folder without touching the network. This specifically protects users
            // who already paid the one-time download cost while testing the sibling-cache build.
            var siblingRoot = PersistentAssetCache.LegacySiblingRoot;
            var siblingPacksRoot = Path.Combine(siblingRoot, "Packs");
            var siblingStateRoot = Path.Combine(siblingRoot, "State");
            var siblingStagingRoot = Path.Combine(siblingRoot, "Staging");

            foreach (var entry in _manifest.Assets)
            {
                if (entry?.Pack == null || string.IsNullOrWhiteSpace(entry.Pack.OutputPath)) continue;

                var cachedPack = PackPath(entry);
                var siblingPack = Path.GetFullPath(Path.Combine(siblingPacksRoot,
                    entry.Pack.OutputPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

                try
                {
                    if (!ValidatePackStructure(entry, cachedPack) && ValidatePackStructure(entry, siblingPack))
                    {
                        if (File.Exists(cachedPack)) TryDelete(cachedPack);
                        Directory.CreateDirectory(Path.GetDirectoryName(cachedPack));
                        MoveOrCopyFile(siblingPack, cachedPack);
                        if (!ValidatePackStructure(entry, cachedPack))
                            throw new InvalidDataException("One-folder cache validation failed after sibling migration: " + entry.Pack.OutputPath);
                        migratedPacks++;
                    }

                    if (File.Exists(siblingPack) && ValidatePackStructure(entry, cachedPack))
                        TryDelete(siblingPack);
                }
                catch (Exception ex)
                {
                    Log("WARN could not migrate sibling NVTILES2 pack for " + entry.Id + ": " + ex.Message);
                }

                try
                {
                    var oldReady = Path.Combine(siblingStateRoot, SafeId(entry.Id) + ".ready");
                    var newReady = AssetReadyPath(entry);
                    if (!File.Exists(newReady) && File.Exists(oldReady))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(newReady));
                        MoveOrCopyFile(oldReady, newReady);
                        if (File.Exists(newReady)) TryDelete(oldReady);
                        migratedMarkers++;
                    }

                    var oldMismatch = Path.Combine(siblingStateRoot, SafeId(entry.Id) + ".source-mismatch");
                    var newMismatch = MismatchMarkerPath(entry);
                    if (!File.Exists(newMismatch) && File.Exists(oldMismatch))
                    {
                        MoveOrCopyFile(oldMismatch, newMismatch);
                        if (File.Exists(newMismatch)) TryDelete(oldMismatch);
                    }
                }
                catch (Exception ex)
                {
                    Log("WARN could not migrate sibling state marker for " + entry.Id + ": " + ex.Message);
                }

                // Pre-persistent test builds stored the expensive .tiles pack directly beside
                // the generated preview under Maps/. Adopt those too if encountered.
                try
                {
                    var oldDirectPack = Absolute(entry.Pack.OutputPath);
                    if (!ValidatePackStructure(entry, cachedPack) && ValidatePackStructure(entry, oldDirectPack))
                    {
                        if (File.Exists(cachedPack)) TryDelete(cachedPack);
                        Directory.CreateDirectory(Path.GetDirectoryName(cachedPack));
                        MoveOrCopyFile(oldDirectPack, cachedPack);
                        if (!ValidatePackStructure(entry, cachedPack))
                            throw new InvalidDataException("One-folder cache validation failed after direct-pack migration: " + entry.Pack.OutputPath);
                        migratedPacks++;
                    }

                    if (File.Exists(oldDirectPack) && ValidatePackStructure(entry, cachedPack))
                        TryDelete(oldDirectPack);
                }
                catch (Exception ex)
                {
                    Log("WARN could not migrate old direct NVTILES2 pack for " + entry.Id + ": " + ex.Message);
                }
            }

            try
            {
                var oldGlobal = Path.Combine(siblingStateRoot, (_manifest.AssetSet ?? "assets") + ".ready");
                if (!File.Exists(_globalReadyPath) && File.Exists(oldGlobal))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_globalReadyPath));
                    MoveOrCopyFile(oldGlobal, _globalReadyPath);
                    if (File.Exists(_globalReadyPath)) TryDelete(oldGlobal);
                    migratedMarkers++;
                }
            }
            catch (Exception ex)
            {
                Log("WARN could not migrate sibling global ready marker: " + ex.Message);
            }

            // Preserve interrupted partial downloads from the sibling-cache test build.
            try
            {
                if (Directory.Exists(siblingStagingRoot))
                {
                    foreach (var dir in Directory.GetDirectories(siblingStagingRoot))
                    {
                        var destination = Path.Combine(_stagingRoot, Path.GetFileName(dir));
                        if (Directory.Exists(destination)) continue;
                        try
                        {
                            Directory.Move(dir, destination);
                        }
                        catch
                        {
                            CopyDirectory(dir, destination);
                            Directory.Delete(dir, true);
                        }
                        migratedStaging++;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN could not migrate sibling resumable staging cache: " + ex.Message);
            }

            // Preserve the previous sibling-cache diagnostic log without overwriting the new
            // one-folder log created during this migration.
            try
            {
                var oldLog = Path.Combine(siblingStateRoot, "asset-preparation.log");
                if (File.Exists(oldLog))
                {
                    var archivedLog = Path.Combine(_stateRoot, "asset-preparation.previous-sibling.log");
                    if (File.Exists(archivedLog)) TryDelete(archivedLog);
                    MoveOrCopyFile(oldLog, archivedLog);
                    if (File.Exists(archivedLog)) TryDelete(oldLog);
                }
            }
            catch (Exception ex)
            {
                Log("WARN could not archive sibling asset-preparation log: " + ex.Message);
            }

            // Remove empty leftovers from the obsolete second folder. Never recursively delete
            // non-empty content we did not successfully migrate.
            TryDeleteEmptyDirectoryTree(siblingRoot);

            if (migratedPacks > 0 || migratedMarkers > 0 || migratedStaging > 0)
                Log("One-folder cache migration complete: packs=" + migratedPacks.ToString(CultureInfo.InvariantCulture) +
                    ", markers=" + migratedMarkers.ToString(CultureInfo.InvariantCulture) +
                    ", staging=" + migratedStaging.ToString(CultureInfo.InvariantCulture) + ". No network download was used for migration.");
        }

        internal bool GlobalReadyMarkerMatches()
        {
            try
            {
                if (!File.Exists(_globalReadyPath)) return false;
                if (!string.Equals(File.ReadAllText(_globalReadyPath).Trim(), _manifestHash, StringComparison.OrdinalIgnoreCase)) return false;
                foreach (var entry in _manifest.Assets)
                {
                    if (!QuickOutputCheck(entry) || !AssetReadyMarkerMatches(entry)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        internal void Run()
        {
            Directory.CreateDirectory(_stateRoot);
            Directory.CreateDirectory(_stagingRoot);
            Log("Built-in C# asset installer started. No PowerShell/curl/external process is used.");
            Log("Manifest: " + _manifestPath);
            Log("Structure lock: " + _structurePath);
            Log("Asset set: " + _manifest.AssetSet + ", layers=" + _manifest.Assets.Count.ToString(CultureInfo.InvariantCulture));

            try
            {
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

                var total = _manifest.Assets.Count;
                var readyCount = 0;
                for (_currentIndex = 0; _currentIndex < total; _currentIndex++)
                {
                    _token.ThrowIfCancellationRequested();
                    WaitIfPaused();
                    var entry = _manifest.Assets[_currentIndex];
                    Report("Checking local cache", entry.Id, PhasePercent(0.0), readyCount, total, "Checking verified local files...");

                    try
                    {
                        if (IsPrepared(entry))
                        {
                            readyCount++;
                            Report("Already ready", entry.Id, PhasePercent(1.0), readyCount, total, "Verified local asset - no download needed.");
                            continue;
                        }

                        EnsureUpstreamStructureVerified();
                        if (_structureProblems.TryGetValue(entry.Map ?? string.Empty, out var structuralProblem))
                            throw new AssetDownloadException("Official map structure no longer matches the v2.0.0 calibration lock: " + structuralProblem + " This layer was not downloaded.", false);

                        PrepareAsset(entry, readyCount, total);
                        readyCount++;
                        WriteAssetReadyMarker(entry);
                        TryDelete(MismatchMarkerPath(entry));
                        CleanupStaging(entry);
                        Report("Map asset ready", entry.Id, PhasePercent(1.0), readyCount, total, "Verified structure and installed locally.");
                        Log("PASS " + entry.Id + " - official source structure and NVTILES2 pack verified.");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (AssetDownloadException ex)
                    {
                        _failedAssets++;
                        Log("FAIL " + entry.Id + " - " + ex.Message);
                        Report(ex.NetworkWide ? "Connection problem" : "Map asset unavailable", entry.Id, PhasePercent(1.0), readyCount, total, ex.Message);
                        if (ex.NetworkWide)
                        {
                            Log("Stopping this launch after a connection-wide failure. Completed assets remain installed; partial current-layer downloads remain resumable.");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _failedAssets++;
                        Log("FAIL " + entry.Id + " - " + ex);
                        Report("Map asset failed", entry.Id, PhasePercent(1.0), readyCount, total, Short(ex.Message));
                    }
                }

                var allReady = _manifest.Assets.All(IsPrepared);
                if (allReady)
                {
                    AtomicWriteText(_globalReadyPath, _manifestHash + Environment.NewLine);
                    ReportFinished(true, "Ready", "All map assets are prepared. Future launches require no download.");
                    Log("ALL READY - global ready marker written.");
                }
                else
                {
                    try { if (File.Exists(_globalReadyPath)) File.Delete(_globalReadyPath); } catch { }
                    ReportFinished(false, "Preparation incomplete", _failedAssets > 0
                        ? _failedAssets.ToString(CultureInfo.InvariantCulture) + " asset(s) could not be prepared. Completed maps were kept; missing maps use fallback artwork. Network failures retry next launch; official map-structure changes require a DynamicMaps Extended update."
                        : "Preparation stopped before every map was ready. Completed maps were kept and the next launch resumes the remaining work.");
                }
            }
            catch (OperationCanceledException)
            {
                Log("Asset installer cancelled during client shutdown. Partial current-layer downloads were kept for resume.");
            }
            catch (Exception ex)
            {
                Log("FATAL installer error: " + ex);
                ReportFinished(false, "Asset preparation failed", Short(ex.Message));
            }
        }

        private void EnsureUpstreamStructureVerified()
        {
            if (_structureChecked) return;
            _structureChecked = true;
            Report("Verifying official map structure", string.Empty, PhasePercent(0.0), 0, _manifest.Assets.Count,
                "Checking Tarkov.dev map paths, transform, bounds, rotation and zoom metadata...");

            string json = null;
            Exception metadataError = null;
            for (var attempt = 1; attempt <= 3 && json == null; attempt++)
            {
                _token.ThrowIfCancellationRequested();
                WaitIfPaused();
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(_structureLock.CurrentMetadataUrl);
                    request.Method = "GET";
                    request.UserAgent = "DynamicMaps-Extended/2.0.0";
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 60000;
                    request.KeepAlive = true;
                    using var response = (HttpWebResponse)request.GetResponse();
                    if (response.StatusCode != HttpStatusCode.OK) throw new WebException("HTTP " + (int)response.StatusCode);
                    using var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8, true);
                    json = reader.ReadToEnd();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    metadataError = ex;
                    if (attempt < 3) Thread.Sleep(500 * attempt * attempt);
                }
            }
            if (json == null)
            {
                _structureChecked = false; // allow a later launch to retry
                throw new AssetDownloadException("Could not verify official Tarkov.dev map metadata after 3 attempts: " + Short(metadataError?.Message), true, metadataError);
            }

            JArray current;
            try { current = JArray.Parse(json); }
            catch (Exception ex)
            {
                _structureChecked = false;
                throw new AssetDownloadException("Official Tarkov.dev map metadata could not be parsed: " + Short(ex.Message), true, ex);
            }

            foreach (var expected in _structureLock.Maps)
            {
                var mapNode = current.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["normalizedName"], expected.NormalizedName, StringComparison.Ordinal));
                if (mapNode == null)
                {
                    _structureProblems[expected.NormalizedName] = "map entry is missing";
                    continue;
                }
                var interactive = (mapNode["maps"] as JArray)?.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["projection"], "interactive", StringComparison.Ordinal));
                if (interactive == null)
                {
                    _structureProblems[expected.NormalizedName] = "interactive map entry is missing";
                    continue;
                }

                var problems = new List<string>();
                CompareString(problems, "tilePath", expected.TilePath, (string)interactive["tilePath"]);
                CompareInt(problems, "tileSize", expected.TileSize, (int?)interactive["tileSize"] ?? 256);
                CompareInt(problems, "minZoom", expected.MinZoom, (int?)interactive["minZoom"] ?? -1);
                CompareInt(problems, "maxZoom", expected.MaxZoom, (int?)interactive["maxZoom"] ?? -1);
                CompareNumberList(problems, "transform", expected.Transform, interactive["transform"] as JArray);
                CompareBounds(problems, "bounds", expected.Bounds, interactive["bounds"] as JArray);
                CompareDouble(problems, "coordinateRotation", expected.CoordinateRotation, (double?)interactive["coordinateRotation"] ?? 0.0);

                var layers = interactive["layers"] as JArray;
                var expectedLayers = expected.Layers ?? new List<UpstreamStructureLayer>();
                foreach (var expectedLayer in expectedLayers)
                {
                    var layer = layers?.OfType<JObject>().FirstOrDefault(x => string.Equals((string)x["name"], expectedLayer.Name, StringComparison.Ordinal));
                    if (layer == null) problems.Add("layer missing: " + expectedLayer.Name);
                    else CompareString(problems, "layer tilePath " + expectedLayer.Name, expectedLayer.TilePath, (string)layer["tilePath"]);
                }

                // Treat additions/removals of raster-backed floors as a structural update too.
                // SVG-only overlay layers do not participate in this runtime raster installer.
                var currentRasterLayers = (layers?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                    .Where(x => !string.IsNullOrWhiteSpace((string)x["tilePath"]))
                    .Select(x => new { Name = (string)x["name"] ?? string.Empty, TilePath = (string)x["tilePath"] ?? string.Empty })
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.TilePath, StringComparer.Ordinal)
                    .ToList();
                var lockedRasterLayers = expectedLayers
                    .Select(x => new { Name = x.Name ?? string.Empty, TilePath = x.TilePath ?? string.Empty })
                    .OrderBy(x => x.Name, StringComparer.Ordinal)
                    .ThenBy(x => x.TilePath, StringComparer.Ordinal)
                    .ToList();
                if (currentRasterLayers.Count != lockedRasterLayers.Count ||
                    currentRasterLayers.Where((x, i) => x.Name != lockedRasterLayers[i].Name || x.TilePath != lockedRasterLayers[i].TilePath).Any())
                    problems.Add("raster layer set changed");

                if (problems.Count > 0) _structureProblems[expected.NormalizedName] = string.Join("; ", problems.Distinct(StringComparer.Ordinal));
            }

            var missingLocks = _manifest.Assets.Select(a => a.Map).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(m => !_structureLock.Maps.Any(x => string.Equals(x.NormalizedName, m, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var map in missingLocks) _structureProblems[map] = "no v2.0.0 structural lock exists for this map";

            if (_structureProblems.Count == 0)
            {
                Log("UPSTREAM STRUCTURE VERIFIED: current Tarkov.dev metadata matches pinned commit " + _structureLock.PinnedMetadataCommit + " for all " + _structureLock.Maps.Count.ToString(CultureInfo.InvariantCulture) + " raster maps.");
            }
            else
            {
                foreach (var problem in _structureProblems) Log("STRUCTURE MISMATCH " + problem.Key + " - " + problem.Value);
            }
        }

        private static void CompareString(List<string> problems, string field, string expected, string actual)
        {
            if (!string.Equals(expected ?? string.Empty, actual ?? string.Empty, StringComparison.Ordinal)) problems.Add(field + " changed");
        }

        private static void CompareInt(List<string> problems, string field, int expected, int actual)
        {
            if (expected != actual) problems.Add(field + " changed (" + expected.ToString(CultureInfo.InvariantCulture) + " -> " + actual.ToString(CultureInfo.InvariantCulture) + ")");
        }

        private static void CompareDouble(List<string> problems, string field, double expected, double actual)
        {
            if (Math.Abs(expected - actual) > 0.000001) problems.Add(field + " changed");
        }

        private static void CompareNumberList(List<string> problems, string field, List<double> expected, JArray actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
            {
                problems.Add(field + " changed");
                return;
            }
            for (var i = 0; i < expected.Count; i++)
                if (Math.Abs(expected[i] - (double)actual[i]) > 0.000001) { problems.Add(field + " changed"); return; }
        }

        private static void CompareBounds(List<string> problems, string field, List<List<double>> expected, JArray actual)
        {
            if (expected == null || actual == null || expected.Count != actual.Count)
            {
                problems.Add(field + " changed");
                return;
            }
            for (var i = 0; i < expected.Count; i++)
            {
                var row = actual[i] as JArray;
                if (row == null || expected[i] == null || row.Count != expected[i].Count) { problems.Add(field + " changed"); return; }
                for (var j = 0; j < expected[i].Count; j++)
                    if (Math.Abs(expected[i][j] - (double)row[j]) > 0.000001) { problems.Add(field + " changed"); return; }
            }
        }

        private void PrepareAsset(RuntimeAssetEntry entry, int completed, int total)
        {
            ValidateEntry(entry);
            var staging = StagingPath(entry);
            Directory.CreateDirectory(staging);

            var coords = ExpandCoordinates(entry);
            var missing = coords.Where(c => !IsValidPng(TilePath(staging, entry.Zoom, c.X, c.Y))).ToList();
            if (missing.Count > 0)
            {
                Log(entry.Id + ": downloading " + missing.Count.ToString(CultureInfo.InvariantCulture) + "/" + coords.Count.ToString(CultureInfo.InvariantCulture) + " exact source tiles.");
                DownloadMissing(entry, staging, missing, coords.Count, completed, total);
            }
            else
            {
                Report("Verifying downloaded source", entry.Id, PhasePercent(0.74), completed, total, "All exact source tiles are already cached.");
            }

            _token.ThrowIfCancellationRequested();
            Report("Verifying downloaded source", entry.Id, PhasePercent(0.77), completed, total, "Validating exact PNG tiles and recording local artwork revision...");
            var fingerprints = CanonicalFingerprints(staging);
            if (fingerprints.Count != entry.ValidTileCount)
            {
                CleanupStaging(entry);
                throw new AssetDownloadException("Downloaded tile count does not match the v2.0.0 structural layer definition; it was rejected.", false);
            }

            var visualExact = !string.IsNullOrWhiteSpace(entry.SourceVisualFingerprintSha256) &&
                              string.Equals(fingerprints.VisualLock, entry.SourceVisualFingerprintSha256, StringComparison.OrdinalIgnoreCase);
            var binaryExact = fingerprints.TotalBytes == entry.SourcePngBytes &&
                              string.Equals(fingerprints.BinaryLock, entry.SourceFingerprintSha256, StringComparison.OrdinalIgnoreCase);
            if (visualExact && binaryExact)
            {
                Log(entry.Id + ": historical binary + decoded-pixel fingerprints MATCH.");
            }
            else if (visualExact)
            {
                Log(entry.Id + ": decoded pixels match the 2026-10-04 capture; PNG transport bytes were losslessly re-encoded upstream.");
            }
            else
            {
                Log(entry.Id + ": artwork revision detected. Official Tarkov.dev map structure is unchanged and verified, so the current raster revision is accepted for this local cache.");
                Log(entry.Id + ": historicalVisual=" + (entry.SourceVisualFingerprintSha256 ?? string.Empty) +
                    ", currentVisual=" + fingerprints.VisualLock +
                    ", historicalBinary=" + (entry.SourceFingerprintSha256 ?? string.Empty) +
                    ", currentBinary=" + fingerprints.BinaryLock +
                    ", historicalBytes=" + entry.SourcePngBytes.ToString(CultureInfo.InvariantCulture) +
                    ", currentBytes=" + fingerprints.TotalBytes.ToString(CultureInfo.InvariantCulture) + ".");
            }

            _token.ThrowIfCancellationRequested();
            Report("Building local high-resolution pack", entry.Id, PhasePercent(0.82), completed, total, "Creating compact NVTILES2 cache...");
            BuildPack(entry, staging, fingerprints.VisualKeyByBinaryHash);

            _token.ThrowIfCancellationRequested();
            Report("Building local preview", entry.Id, PhasePercent(0.88), completed, total, entry.Warm != null ? "Creating preview and warm atlas..." : "Creating preview...");
            BuildImages(entry, staging, completed, total);

            if (!ValidatePreparedStrict(entry)) throw new InvalidDataException("Generated asset validation failed after local build.");
        }

        private void DownloadMissing(RuntimeAssetEntry entry, string staging, List<RuntimeTileCoord> missing, int totalTiles, int completedAssets, int totalAssets)
        {
            var queue = new ConcurrentQueue<RuntimeTileCoord>(missing);
            var errors = new ConcurrentQueue<Exception>();
            var finished = totalTiles - missing.Count;
            const int workers = 8;
            var tasks = new List<Task>();
            for (var i = 0; i < workers; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    while (!_token.IsCancellationRequested && errors.IsEmpty && queue.TryDequeue(out var coord))
                    {
                        try
                        {
                            DownloadTile(entry, staging, coord);
                            var now = Interlocked.Increment(ref finished);
                            if ((now & 31) == 0 || now == totalTiles)
                            {
                                var phase = 0.72 * now / Math.Max(1.0, totalTiles);
                                Report("Downloading map assets", entry.Id, PhasePercent(phase), completedAssets, totalAssets,
                                    "Files: " + now.ToString(CultureInfo.InvariantCulture) + "/" + totalTiles.ToString(CultureInfo.InvariantCulture));
                            }
                        }
                        catch (Exception ex) { errors.Enqueue(ex); }
                    }
                }, _token));
            }

            try { Task.WaitAll(tasks.ToArray()); }
            catch (AggregateException ae)
            {
                var real = ae.Flatten().InnerExceptions.FirstOrDefault(e => !(e is OperationCanceledException));
                if (real != null) throw real;
                _token.ThrowIfCancellationRequested();
            }
            _token.ThrowIfCancellationRequested();
            if (errors.TryDequeue(out var error)) throw error;

            var remaining = ExpandCoordinates(entry).Count(c => !IsValidPng(TilePath(staging, entry.Zoom, c.X, c.Y)));
            if (remaining != 0) throw new AssetDownloadException(remaining.ToString(CultureInfo.InvariantCulture) + " exact source tiles are still missing after download.", false);
        }

        private void DownloadTile(RuntimeAssetEntry entry, string staging, RuntimeTileCoord coord)
        {
            var finalPath = TilePath(staging, entry.Zoom, coord.X, coord.Y);
            if (IsValidPng(finalPath)) return;
            var url = entry.TilePathTemplate
                .Replace("{z}", entry.Zoom.ToString(CultureInfo.InvariantCulture))
                .Replace("{x}", coord.X.ToString(CultureInfo.InvariantCulture))
                .Replace("{y}", coord.Y.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            var part = finalPath + ".part";
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                _token.ThrowIfCancellationRequested();
                WaitIfPaused();
                try
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.UserAgent = "DynamicMaps-Extended/2.0.0";
                    request.Timeout = 30000;
                    request.ReadWriteTimeout = 120000;
                    request.KeepAlive = true;
                    using var response = (HttpWebResponse)request.GetResponse();
                    if (response.StatusCode != HttpStatusCode.OK) throw new WebException("HTTP " + (int)response.StatusCode);
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                    if (!IsValidPng(part)) throw new InvalidDataException("Downloaded file is not a valid PNG.");
                    if (File.Exists(finalPath)) File.Delete(finalPath);
                    File.Move(part, finalPath);
                    return;
                }
                catch (WebException ex)
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    var status = (ex.Response as HttpWebResponse)?.StatusCode;
                    var permanent = status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone;
                    if (permanent) throw new AssetDownloadException("Expected v2.0.0 tile is no longer available upstream (HTTP " + (int)status.Value + ").", false, ex);
                    if (attempt >= 4)
                    {
                        var networkWide = !status.HasValue || (int)status.Value == 429 || (int)status.Value >= 500;
                        throw new AssetDownloadException("Could not download map data after 4 attempts" + (status.HasValue ? " (HTTP " + (int)status.Value + ")" : "") + ".", networkWide, ex);
                    }
                    Thread.Sleep(500 * attempt * attempt);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    try { if (File.Exists(part)) File.Delete(part); } catch { }
                    if (attempt >= 4) throw new AssetDownloadException("Downloaded tile could not be validated after 4 attempts: " + Short(ex.Message), false, ex);
                    Thread.Sleep(500 * attempt * attempt);
                }
            }
        }

        private void BuildPack(RuntimeAssetEntry entry, string staging, Dictionary<string, string> visualKeyByBinaryHash)
        {
            var p = entry.Pack;
            var destination = PackPath(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var part = destination + ".part";
            var payload = destination + ".payload.part";
            TryDelete(part); TryDelete(payload);

            var x0 = p.Grid[0]; var x1 = p.Grid[1]; var y0 = p.Grid[2]; var y1 = p.Grid[3];
            var count = checked(p.Columns * p.Rows);
            if (count != p.Records || p.Columns != x1 - x0 + 1 || p.Rows != y1 - y0 + 1) throw new InvalidDataException("Pack grid metadata is inconsistent.");
            var offsets = new uint[count];
            var lengths = new uint[count];

            // Deduplicate by decoded pixels, not PNG transport bytes. If the CDN losslessly
            // re-encodes identical tiles with different compression/chunks, the local pack still
            // stays compact and every record points at pixel-identical data.
            var seen = new Dictionary<string, PayloadReference>(StringComparer.Ordinal);
            var present = 0;
            var headerAndIndex = 40L + count * 8L;

            using (var payloadStream = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var index = 0;
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++, index++)
                    {
                        _token.ThrowIfCancellationRequested();
                        WaitIfPaused();
                        var tile = TilePath(staging, entry.Zoom, x, y);
                        if (!File.Exists(tile)) continue;
                        var bytes = File.ReadAllBytes(tile);
                        var binaryHash = Sha256Bytes(bytes);
                        if (!visualKeyByBinaryHash.TryGetValue(binaryHash, out var visualKey))
                        {
                            // Defensive fallback. Verification normally populated this dictionary
                            // for every staged tile before pack construction starts.
                            var image = PngCodec.Decode(bytes);
                            visualKey = image.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                        image.Height.ToString(CultureInfo.InvariantCulture) + ":" +
                                        Hex(Sha256Raw(image.Rgba));
                            visualKeyByBinaryHash[binaryHash] = visualKey;
                        }

                        present++;
                        if (p.DeduplicatePayloads && seen.TryGetValue(visualKey, out var old))
                        {
                            offsets[index] = old.Offset;
                            lengths[index] = old.Length;
                        }
                        else
                        {
                            var absolute = headerAndIndex + payloadStream.Position;
                            if (absolute > uint.MaxValue) throw new InvalidDataException("NVTILES2 pack exceeds 4 GiB.");
                            var length = checked((uint)bytes.Length);
                            offsets[index] = (uint)absolute;
                            lengths[index] = length;
                            payloadStream.Write(bytes, 0, bytes.Length);
                            if (p.DeduplicatePayloads) seen[visualKey] = new PayloadReference(offsets[index], length);
                        }
                    }
                }
            }

            if (present != p.PresentRecords) throw new InvalidDataException("Pack source record count mismatch: " + present + "/" + p.PresentRecords);

            using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("NVTILES2"));
                writer.Write((uint)p.FullWidth); writer.Write((uint)p.FullHeight); writer.Write((uint)p.TileSize);
                writer.Write((uint)p.Columns); writer.Write((uint)p.Rows); writer.Write(p.Origin[0]); writer.Write(p.Origin[1]); writer.Write((uint)count);
                for (var i = 0; i < count; i++) { writer.Write(offsets[i]); writer.Write(lengths[i]); }
                writer.Flush();
                using var input = File.OpenRead(payload);
                input.CopyTo(output);
                output.Flush();
            }
            TryDelete(payload);

            if (!ValidatePackStructure(entry, part))
            {
                TryDelete(part);
                throw new InvalidDataException("Locally generated NVTILES2 pack failed structural validation.");
            }

            var actualBytes = new FileInfo(part).Length;
            var actualHash = Sha256File(part);
            if (actualBytes == p.ExpectedBytes && string.Equals(actualHash, p.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Log(entry.Id + ": generated pack is byte-for-byte identical to the original tested pack.");
            }
            else
            {
                Log(entry.Id + ": generated pack bytes differ from the historical tested pack; NVTILES2 geometry/index structure and the current accepted source raster were verified. " +
                    "historical=" + p.ExpectedBytes.ToString(CultureInfo.InvariantCulture) + "/" + p.ExpectedSha256 +
                    ", current=" + actualBytes.ToString(CultureInfo.InvariantCulture) + "/" + actualHash +
                    ", visualPayloads=" + seen.Count.ToString(CultureInfo.InvariantCulture) + ".");
            }
            AtomicMove(part, destination);
        }

        private void BuildImages(RuntimeAssetEntry entry, string staging, int completedAssets, int totalAssets)
        {
            var preview = new Canvas(entry.Preview.Width, entry.Preview.Height, entry.Pack.FullWidth, entry.Pack.FullHeight);
            Canvas warm = entry.Warm != null ? new Canvas(entry.Warm.Width, entry.Warm.Height, entry.Pack.FullWidth, entry.Pack.FullHeight) : null;
            var p = entry.Pack;
            var coords = ExpandCoordinates(entry);
            Dictionary<string, PngCodec.Image> decodeCache = p.UniquePayloads <= 256
                ? new Dictionary<string, PngCodec.Image>(StringComparer.Ordinal)
                : null;
            var done = 0;
            foreach (var coord in coords)
            {
                _token.ThrowIfCancellationRequested();
                WaitIfPaused();
                var tilePath = TilePath(staging, entry.Zoom, coord.X, coord.Y);
                var bytes = File.ReadAllBytes(tilePath);
                PngCodec.Image tile;
                if (decodeCache != null)
                {
                    var key = Sha256Bytes(bytes);
                    if (!decodeCache.TryGetValue(key, out tile))
                    {
                        tile = PngCodec.Decode(bytes);
                        decodeCache[key] = tile;
                    }
                }
                else
                {
                    tile = PngCodec.Decode(bytes);
                }
                var col = coord.X - p.Grid[0]; var row = coord.Y - p.Grid[2];
                var rawX = p.Origin[0] + col * p.TileSize;
                var rawY = p.Origin[1] + row * p.TileSize;
                preview.Draw(tile, rawX, rawY);
                warm?.Draw(tile, rawX, rawY);
                done++;
                if ((done & 127) == 0)
                {
                    var fraction = done / Math.Max(1.0, coords.Count);
                    Report("Building local preview", entry.Id, PhasePercent(0.88 + 0.105 * fraction), completedAssets, totalAssets,
                        "Rendering: " + done.ToString(CultureInfo.InvariantCulture) + "/" + coords.Count.ToString(CultureInfo.InvariantCulture));
                }
            }

            WaitIfPaused();
            WriteCanvas(entry.Preview, preview);
            if (entry.Warm != null)
            {
                WaitIfPaused();
                WriteCanvas(entry.Warm, warm);
            }
        }

        private void BuildImagesFromPack(RuntimeAssetEntry entry)
        {
            var p = entry.Pack;
            var packPath = PackPath(entry);
            if (!ValidatePackStructure(entry, packPath))
                throw new InvalidDataException("Existing NVTILES2 pack failed structural validation: " + p.OutputPath);

            var preview = new Canvas(entry.Preview.Width, entry.Preview.Height, p.FullWidth, p.FullHeight);
            Canvas warm = entry.Warm != null ? new Canvas(entry.Warm.Width, entry.Warm.Height, p.FullWidth, p.FullHeight) : null;
            var count = checked(p.Columns * p.Rows);
            var offsets = new uint[count];
            var lengths = new uint[count];

            using (var stream = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.ASCII, true))
            {
                var magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (!string.Equals(magic, "NVTILES2", StringComparison.Ordinal))
                    throw new InvalidDataException("Existing tile pack is not NVTILES2: " + p.OutputPath);

                var width = checked((int)reader.ReadUInt32());
                var height = checked((int)reader.ReadUInt32());
                var tileSize = checked((int)reader.ReadUInt32());
                var columns = checked((int)reader.ReadUInt32());
                var rows = checked((int)reader.ReadUInt32());
                var originX = reader.ReadInt32();
                var originY = reader.ReadInt32();
                var records = checked((int)reader.ReadUInt32());
                if (width != p.FullWidth || height != p.FullHeight || tileSize != p.TileSize ||
                    columns != p.Columns || rows != p.Rows || originX != p.Origin[0] || originY != p.Origin[1] || records != p.Records)
                    throw new InvalidDataException("Existing NVTILES2 geometry does not match the manifest: " + p.OutputPath);

                for (var i = 0; i < count; i++)
                {
                    offsets[i] = reader.ReadUInt32();
                    lengths[i] = reader.ReadUInt32();
                }

                // Small highly-deduplicated layers benefit from a tiny decoded-image cache. Large
                // satellite maps are deliberately decoded one tile at a time to keep memory bounded.
                Dictionary<ulong, PngCodec.Image> decodeCache = p.UniquePayloads > 0 && p.UniquePayloads <= 256
                    ? new Dictionary<ulong, PngCodec.Image>()
                    : null;

                for (var row = 0; row < p.Rows; row++)
                {
                    for (var col = 0; col < p.Columns; col++)
                    {
                        _token.ThrowIfCancellationRequested();
                        WaitIfPaused();
                        var index = row * p.Columns + col;
                        var length = lengths[index];
                        if (length == 0) continue;

                        var cacheKey = ((ulong)offsets[index] << 32) | length;
                        PngCodec.Image tile = null;
                        if (decodeCache != null) decodeCache.TryGetValue(cacheKey, out tile);
                        if (tile == null)
                        {
                            var bytes = new byte[checked((int)length)];
                            stream.Seek(offsets[index], SeekOrigin.Begin);
                            ReadFully(stream, bytes, 0, bytes.Length);
                            tile = PngCodec.Decode(bytes);
                            if (decodeCache != null) decodeCache[cacheKey] = tile;
                        }

                        var rawX = p.Origin[0] + col * p.TileSize;
                        var rawY = p.Origin[1] + row * p.TileSize;
                        preview.Draw(tile, rawX, rawY);
                        warm?.Draw(tile, rawX, rawY);
                    }
                }
            }

            WriteCanvas(entry.Preview, preview);
            if (entry.Warm != null) WriteCanvas(entry.Warm, warm);
        }

        private static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new EndOfStreamException();
                offset += read;
                count -= read;
            }
        }

        private void WriteCanvas(RuntimeImageSpec spec, Canvas canvas)
        {
            var destination = Absolute(spec.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var part = destination + ".part";
            TryDelete(part);
            PngCodec.EncodeRgba(part, canvas.Width, canvas.Height, canvas.Pixels);
            if (!PngCodec.TryReadDimensions(part, out var w, out var h) || w != spec.Width || h != spec.Height)
            {
                TryDelete(part);
                throw new InvalidDataException("Generated preview PNG failed validation: " + spec.OutputPath);
            }
            AtomicMove(part, destination);
        }

        private sealed class Canvas
        {
            internal readonly int Width;
            internal readonly int Height;
            internal readonly byte[] Pixels;
            private readonly int _fullWidth;
            private readonly int _fullHeight;
            internal Canvas(int width, int height, int fullWidth, int fullHeight)
            {
                Width = width; Height = height; _fullWidth = fullWidth; _fullHeight = fullHeight;
                Pixels = new byte[checked(width * height * 4)];
            }

            internal void Draw(PngCodec.Image tile, int rawX, int rawY)
            {
                var px0 = Math.Max(0, rawX); var py0 = Math.Max(0, rawY);
                var px1 = Math.Min(_fullWidth, rawX + tile.Width); var py1 = Math.Min(_fullHeight, rawY + tile.Height);
                if (px1 <= px0 || py1 <= py0) return;
                var dx0 = Math.Max(0, (int)Math.Floor(px0 * Width / (double)_fullWidth));
                var dy0 = Math.Max(0, (int)Math.Floor(py0 * Height / (double)_fullHeight));
                var dx1 = Math.Min(Width, (int)Math.Ceiling(px1 * Width / (double)_fullWidth));
                var dy1 = Math.Min(Height, (int)Math.Ceiling(py1 * Height / (double)_fullHeight));

                for (var dy = dy0; dy < dy1; dy++)
                {
                    // IMPORTANT: the preview/warm atlas is downsampled tile-by-tile. A destination
                    // pixel whose centre falls just outside this tile can still belong to this
                    // tile's projected destination rectangle because of floor/ceil rounding. The
                    // old code skipped that pixel; on some scaling ratios neither neighbour wrote
                    // it, leaving a transparent row/column that appeared in-game as a black grid.
                    // Clamp the sample to the source edge instead. Adjacent tiles overlap by at most
                    // one destination pixel and the later tile replaces that edge sample, so every
                    // destination pixel is covered with no synthetic seams.
                    var sy = ((dy + 0.5) * _fullHeight / Height) - rawY - 0.5;
                    sy = Math.Max(0.0, Math.Min(tile.Height - 1.0, sy));
                    var y0 = (int)Math.Floor(sy); var y1 = Math.Min(tile.Height - 1, y0 + 1); var fy = sy - y0;
                    for (var dx = dx0; dx < dx1; dx++)
                    {                        var sx = ((dx + 0.5) * _fullWidth / Width) - rawX - 0.5;
                        sx = Math.Max(0.0, Math.Min(tile.Width - 1.0, sx));
                        var x0 = (int)Math.Floor(sx); var x1 = Math.Min(tile.Width - 1, x0 + 1); var fx = sx - x0;
                        var q00 = (y0 * tile.Width + x0) * 4;
                        var q10 = (y0 * tile.Width + x1) * 4;
                        var q01 = (y1 * tile.Width + x0) * 4;
                        var q11 = (y1 * tile.Width + x1) * 4;
                        var dest = (dy * Width + dx) * 4;
                        for (var c = 0; c < 4; c++)
                        {
                            var top = tile.Rgba[q00 + c] + (tile.Rgba[q10 + c] - tile.Rgba[q00 + c]) * fx;
                            var bottom = tile.Rgba[q01 + c] + (tile.Rgba[q11 + c] - tile.Rgba[q01 + c]) * fx;
                            Pixels[dest + c] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(top + (bottom - top) * fy)));
                        }
                    }
                }
            }
        }

        private bool IsPrepared(RuntimeAssetEntry entry)
        {
            var marker = AssetReadyPath(entry);
            string markerValue = null;
            try
            {
                if (File.Exists(marker))
                    markerValue = File.ReadLines(marker).FirstOrDefault()?.Trim();
            }
            catch { }

            // Fast path: current renderer signature and all expected local outputs are valid.
            if (string.Equals(markerValue, AssetSignature(entry), StringComparison.OrdinalIgnoreCase) && QuickOutputCheck(entry))
                return true;

            // Stable offline migration path. The NVTILES2 pack is the expensive downloaded asset;
            // previews/warm atlases are disposable derived cache. If an earlier DMExt version left
            // a structurally valid pack in the exact expected location, reuse it regardless of the
            // old ready-marker/render-cache version and rebuild only the small derived images.
            // BuildImagesFromPack decodes every present PNG payload, so corrupt packs fail here and
            // fall through to a network repair instead of being silently accepted.
            var packPath = PackPath(entry);
            if (ValidatePackStructure(entry, packPath))
            {
                try
                {
                    Report("Reusing local map pack", entry.Id, PhasePercent(0.90), _currentIndex, _manifest.Assets.Count,
                        "Rebuilding preview/warm atlas locally from the existing verified-layout NVTILES2 pack. No download required...");
                    BuildImagesFromPack(entry);
                    if (!ValidatePreparedStrict(entry))
                        throw new InvalidDataException("Local pack migration produced invalid derived images.");

                    WriteAssetReadyMarker(entry);
                    TryDelete(MismatchMarkerPath(entry));
                    Log(entry.Id + ": reused existing structurally valid NVTILES2 pack and rebuilt derived images locally; no network download needed.");
                    return true;
                }
                catch (Exception ex)
                {
                    Log("WARN local NVTILES2 reuse failed for " + entry.Id + ": " + ex.Message + " Falling back to source download/repair.");
                }
            }

            return false;
        }

        private bool AssetReadyMarkerMatches(RuntimeAssetEntry entry)
        {
            try
            {
                var marker = AssetReadyPath(entry);
                if (!File.Exists(marker)) return false;
                var first = File.ReadLines(marker).FirstOrDefault()?.Trim();
                return string.Equals(first, AssetSignature(entry), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private bool ValidatePreparedStrict(RuntimeAssetEntry entry)
        {
            var pack = PackPath(entry);
            return ValidatePackStructure(entry, pack) &&
                   PngMatches(entry.Preview) && (entry.Warm == null || PngMatches(entry.Warm));
        }

        private bool QuickOutputCheck(RuntimeAssetEntry entry)
        {
            try
            {
                var pack = PackPath(entry);
                return ValidatePackStructure(entry, pack) &&
                       PngMatches(entry.Preview) && (entry.Warm == null || PngMatches(entry.Warm));
            }
            catch { return false; }
        }

        private bool ValidatePackStructure(RuntimeAssetEntry entry, string path)
        {
            try
            {
                if (entry?.Pack == null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                var p = entry.Pack;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 40) return false;
                using var reader = new BinaryReader(stream, Encoding.ASCII, true);
                var magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (!string.Equals(magic, "NVTILES2", StringComparison.Ordinal)) return false;
                var width = checked((int)reader.ReadUInt32());
                var height = checked((int)reader.ReadUInt32());
                var tileSize = checked((int)reader.ReadUInt32());
                var columns = checked((int)reader.ReadUInt32());
                var rows = checked((int)reader.ReadUInt32());
                var originX = reader.ReadInt32();
                var originY = reader.ReadInt32();
                var count = checked((int)reader.ReadUInt32());
                if (width != p.FullWidth || height != p.FullHeight || tileSize != p.TileSize ||
                    columns != p.Columns || rows != p.Rows || originX != p.Origin[0] || originY != p.Origin[1] || count != p.Records)
                    return false;

                var payloadStart = 40L + count * 8L;
                if (payloadStart > stream.Length) return false;
                var present = 0;
                for (var i = 0; i < count; i++)
                {
                    var offset = reader.ReadUInt32();
                    var length = reader.ReadUInt32();
                    if (length == 0) continue;
                    present++;
                    if (length > 4 * 1024 * 1024 || offset < payloadStart || (ulong)offset + (ulong)length > (ulong)stream.Length) return false;
                }
                return present == p.PresentRecords;
            }
            catch { return false; }
        }

        private bool PngMatches(RuntimeImageSpec spec)
        {
            var path = Absolute(spec.OutputPath);
            return PngCodec.TryReadDimensions(path, out var width, out var height) && width == spec.Width && height == spec.Height;
        }

        private void ValidateEntry(RuntimeAssetEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.TilePathTemplate)) throw new InvalidDataException("Manifest asset is incomplete.");
            if (entry.Pack == null || entry.Preview == null || entry.ValidTilesByX == null) throw new InvalidDataException("Manifest asset layout is incomplete: " + entry.Id);
            if (!string.Equals(entry.Pack.Format, "NVTILES2", StringComparison.Ordinal)) throw new InvalidDataException("Unexpected pack format: " + entry.Id);
            if (!string.IsNullOrWhiteSpace(entry.SourceVisualFingerprintSha256) && entry.SourceVisualFingerprintSha256.Length != 64) throw new InvalidDataException("Manifest historical visual fingerprint is invalid: " + entry.Id);
            if (ExpandCoordinates(entry).Count != entry.ValidTileCount) throw new InvalidDataException("Manifest coordinate count mismatch: " + entry.Id);
        }

        private List<RuntimeTileCoord> ExpandCoordinates(RuntimeAssetEntry entry)
        {
            var result = new List<RuntimeTileCoord>(entry.ValidTileCount);
            foreach (var xr in entry.ValidTilesByX)
            {
                if (xr.YRanges == null) continue;
                foreach (var range in xr.YRanges)
                {
                    if (range == null || range.Count != 2) continue;
                    for (var y = range[0]; y <= range[1]; y++) result.Add(new RuntimeTileCoord(xr.X, y));
                }
            }
            return result;
        }

        private SourceFingerprintResult CanonicalFingerprints(string root)
        {
            var files = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories)
                .Select(path => new { Path = path, Rel = path.Substring(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/') })
                .OrderBy(x => x.Rel, StringComparer.Ordinal).ToList();

            var result = new SourceFingerprintResult { Count = files.Count, TotalBytes = 0 };
            using var binaryMetadata = new MemoryStream();
            using var visualMetadata = new MemoryStream();

            foreach (var file in files)
            {
                _token.ThrowIfCancellationRequested();
                WaitIfPaused();

                var bytes = File.ReadAllBytes(file.Path);
                var rawHashBytes = Sha256Raw(bytes);
                var rawHash = Hex(rawHashBytes);
                var rel = Encoding.UTF8.GetBytes(file.Rel);

                // Historical transport-byte identity. Useful for diagnostics only because a CDN
                // may losslessly re-encode PNGs while preserving every decoded pixel.
                var length = BitConverter.GetBytes((long)bytes.LongLength);
                binaryMetadata.Write(rel, 0, rel.Length);
                binaryMetadata.WriteByte(0);
                binaryMetadata.Write(length, 0, length.Length);
                binaryMetadata.Write(rawHashBytes, 0, rawHashBytes.Length);
                result.TotalBytes += bytes.LongLength;

                // Authoritative visual identity. Cache by binary hash so repeated transparent or
                // otherwise identical payloads are decoded only once.
                if (!result.VisualKeyByBinaryHash.TryGetValue(rawHash, out var visualKey))
                {
                    var image = PngCodec.Decode(bytes);
                    if (image.Width != 256 || image.Height != 256)
                        throw new InvalidDataException("Unexpected source tile dimensions for " + file.Rel + ": " + image.Width.ToString(CultureInfo.InvariantCulture) + "x" + image.Height.ToString(CultureInfo.InvariantCulture) + " (expected 256x256).");
                    var rgbaHashBytes = Sha256Raw(image.Rgba);
                    var rgbaHash = Hex(rgbaHashBytes);
                    visualKey = image.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                                image.Height.ToString(CultureInfo.InvariantCulture) + ":" + rgbaHash;
                    result.VisualKeyByBinaryHash[rawHash] = visualKey;
                }

                var separator = visualKey.IndexOf(':');
                var dimensions = visualKey.Substring(0, separator).Split('x');
                var width = int.Parse(dimensions[0], CultureInfo.InvariantCulture);
                var height = int.Parse(dimensions[1], CultureInfo.InvariantCulture);
                var rgbaHashForMetadata = HexToBytes(visualKey.Substring(separator + 1));
                var widthBytes = BitConverter.GetBytes(width);
                var heightBytes = BitConverter.GetBytes(height);
                visualMetadata.Write(rel, 0, rel.Length);
                visualMetadata.WriteByte(0);
                visualMetadata.Write(widthBytes, 0, widthBytes.Length);
                visualMetadata.Write(heightBytes, 0, heightBytes.Length);
                visualMetadata.Write(rgbaHashForMetadata, 0, rgbaHashForMetadata.Length);
            }

            binaryMetadata.Position = 0;
            visualMetadata.Position = 0;
            using (var sha = SHA256.Create()) result.BinaryLock = Hex(sha.ComputeHash(binaryMetadata));
            using (var sha = SHA256.Create()) result.VisualLock = Hex(sha.ComputeHash(visualMetadata));
            return result;
        }

        private string StagingPath(RuntimeAssetEntry entry) => Path.Combine(_stagingRoot, SafeId(entry.Id));
        private string TilePath(string staging, int zoom, int x, int y) => Path.Combine(staging, zoom.ToString(CultureInfo.InvariantCulture), x.ToString(CultureInfo.InvariantCulture), y.ToString(CultureInfo.InvariantCulture) + ".png");
        private string AssetReadyPath(RuntimeAssetEntry entry) => Path.Combine(_stateRoot, SafeId(entry.Id) + ".ready");
        private string MismatchMarkerPath(RuntimeAssetEntry entry) => Path.Combine(_stateRoot, SafeId(entry.Id) + ".source-mismatch");
        private string Absolute(string relative) => Path.Combine(_pluginRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        private string PackPath(RuntimeAssetEntry entry) => PersistentAssetCache.GetPackPath(entry.Pack.OutputPath);

        private void WriteAssetReadyMarker(RuntimeAssetEntry entry) => AtomicWriteText(AssetReadyPath(entry), AssetSignature(entry) + Environment.NewLine);

        private bool MismatchMarkerMatches(RuntimeAssetEntry entry)
        {
            try
            {
                var path = MismatchMarkerPath(entry);
                if (!File.Exists(path)) return false;
                var first = File.ReadLines(path).FirstOrDefault();
                return string.Equals(first?.Trim(), AssetSignature(entry), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private void WriteMismatchMarker(RuntimeAssetEntry entry, SourceFingerprintResult observed)
        {
            var text = AssetSignature(entry) + Environment.NewLine +
                       "expectedVisual=" + (entry.SourceVisualFingerprintSha256 ?? string.Empty) + Environment.NewLine +
                       "observedVisual=" + (observed?.VisualLock ?? string.Empty) + Environment.NewLine +
                       "expectedBinary=" + (entry.SourceFingerprintSha256 ?? string.Empty) + Environment.NewLine +
                       "observedBinary=" + (observed?.BinaryLock ?? string.Empty) + Environment.NewLine +
                       "expectedFiles=" + entry.ValidTileCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                       "observedFiles=" + (observed?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                       "historicalBytes=" + entry.SourcePngBytes.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                       "observedBytes=" + (observed?.TotalBytes ?? 0).ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
            AtomicWriteText(MismatchMarkerPath(entry), text);
        }

        private string AssetSignature(RuntimeAssetEntry entry)
        {
            return AssetSignatureCore(entry, "builtin-installer-v4-seam-free-preview");
        }

        private string AssetSignatureV3(RuntimeAssetEntry entry)
        {
            return AssetSignatureCore(entry, "builtin-installer-v3-structural-lock");
        }

        private string AssetSignatureCore(RuntimeAssetEntry entry, string rendererVersion)
        {
            var value = string.Join("|", rendererVersion, _structureLock.PinnedMetadataCommit ?? string.Empty,
                entry.Id, entry.Map, entry.Layer, entry.TilePathTemplate, entry.Zoom, entry.ValidTileCount,
                entry.Pack.OutputPath, entry.Pack.FullWidth, entry.Pack.FullHeight, entry.Pack.TileSize,
                entry.Pack.Columns, entry.Pack.Rows, entry.Pack.Origin[0], entry.Pack.Origin[1], entry.Pack.Records, entry.Pack.PresentRecords,
                entry.Preview.OutputPath, entry.Preview.Width, entry.Preview.Height,
                entry.Warm?.OutputPath ?? string.Empty, entry.Warm?.Width ?? 0, entry.Warm?.Height ?? 0);
            return Sha256Bytes(Encoding.UTF8.GetBytes(value));
        }

        private static void MoveOrCopyFile(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (File.Exists(destination)) return;
            try
            {
                File.Move(source, destination);
                return;
            }
            catch
            {
                // Same-volume moves are normally instant. Fall back to copy+verify semantics
                // when a filesystem/mod-manager restriction prevents the move.
            }

            var part = destination + ".migrate.part";
            TryDelete(part);
            File.Copy(source, part, true);
            AtomicMove(part, destination);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        private static void TryDeleteEmptyDirectoryTree(string root)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
                var dirs = Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                    .OrderByDescending(x => x.Length).ToArray();
                foreach (var dir in dirs)
                {
                    try
                    {
                        if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir, false);
                    }
                    catch { }
                }
                if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
                    Directory.Delete(root, false);
            }
            catch { }
        }

        private void CleanupStaging(RuntimeAssetEntry entry)
        {
            try
            {
                var path = StagingPath(entry);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            catch (Exception ex) { Log("WARN could not remove staging for " + entry.Id + ": " + ex.Message); }
        }

        private bool IsValidPng(string path)
        {
            try
            {
                if (!PngCodec.TryReadDimensions(path, out var width, out var height)) return false;
                return width > 0 && height > 0;
            }
            catch { return false; }
        }

        private void Report(string stage, string asset, int percent, int completed, int total, string detail)
        {
            _progress?.Invoke(new ProgressSnapshot { State = "running", Stage = stage, Asset = asset, Percent = Math.Max(0, Math.Min(100, percent)), Completed = completed, Total = total, Failed = _failedAssets, Detail = detail });
        }

        private void ReportFinished(bool allReady, string stage, string detail)
        {
            _progress?.Invoke(new ProgressSnapshot { State = allReady ? "ready" : "partial", Stage = stage, Asset = string.Empty, Percent = 100, Completed = _manifest.Assets.Count(a => IsPrepared(a)), Total = _manifest.Assets.Count, Failed = _failedAssets, Detail = detail, Finished = true, AllReady = allReady });
        }

        private int PhasePercent(double phase)
        {
            var total = Math.Max(1, _manifest.Assets.Count);
            return (int)Math.Floor(((_currentIndex + Math.Max(0.0, Math.Min(1.0, phase))) / total) * 100.0);
        }

        private void WaitIfPaused()
        {
            while (_pauseRequested != null && _pauseRequested())
            {
                _token.ThrowIfCancellationRequested();
                Thread.Sleep(250);
            }
        }

        private void Log(string message)
        {
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(_stateRoot);
                    File.AppendAllText(_logPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch { }
        }

        private static string SafeId(string value)
        {
            var sb = new StringBuilder();
            foreach (var ch in value ?? string.Empty) sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' ? ch : '_');
            while (sb.ToString().Contains("__")) sb.Replace("__", "_");
            return sb.ToString().Trim('_');
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            foreach (var c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }

        private static string Short(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "Unknown error.";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= 280 ? text : text.Substring(0, 280) + "...";
        }

        private static void AtomicMove(string source, string destination)
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(source, destination);
        }

        private static void AtomicWriteText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static string Sha256File(string path)
        {
            using var input = File.OpenRead(path); using var sha = SHA256.Create(); return Hex(sha.ComputeHash(input));
        }
        private static byte[] Sha256Raw(byte[] data) { using var sha = SHA256.Create(); return sha.ComputeHash(data); }
        private static string Sha256Bytes(byte[] data) => Hex(Sha256Raw(data));
        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex) || (hex.Length & 1) != 0) throw new InvalidDataException("Invalid hexadecimal hash.");
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}