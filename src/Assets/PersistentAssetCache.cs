using System;
using System.IO;

namespace DynamicMapsExtended
{
    // Runtime-downloaded NVTILES2 packs, readiness state, and resumable staging all live
    // under the single DynamicMaps-Extended plugin folder. Release ZIPs intentionally do
    // not contain AssetCache, so normal drag-and-drop/overwrite updates keep the cache.
    // A deliberate full uninstall (deleting DynamicMaps-Extended) removes the cache too.
    internal static class PersistentAssetCache
    {
        private const string CacheFolderName = "AssetCache";
        private const string LegacySiblingFolderName = "DynamicMaps-Extended-AssetCache";

        internal static string Root => Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, CacheFolderName));
        internal static string PacksRoot => Path.Combine(Root, "Packs");
        internal static string StateRoot => Path.Combine(Root, "State");
        internal static string StagingRoot => Path.Combine(Root, "Staging");

        // Temporary v1.5 test builds used a sibling cache next to the plugin folder.
        // Keep this path only so the final one-folder layout can migrate that data locally
        // once, then remove the obsolete sibling folder when it is empty.
        internal static string LegacySiblingRoot
        {
            get
            {
                var pluginParent = Path.GetDirectoryName(Plugin.ExtensionRoot);
                if (string.IsNullOrWhiteSpace(pluginParent)) pluginParent = Plugin.ExtensionRoot;
                return Path.GetFullPath(Path.Combine(pluginParent, LegacySiblingFolderName));
            }
        }

        internal static string GetPackPath(string logicalOutputPath)
        {
            if (string.IsNullOrWhiteSpace(logicalOutputPath)) return string.Empty;
            var relative = logicalOutputPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(PacksRoot, relative));
        }

        internal static string GetPackPathForPreview(string absolutePreviewPath)
        {
            if (string.IsNullOrWhiteSpace(absolutePreviewPath)) return string.Empty;
            try
            {
                var preview = Path.GetFullPath(absolutePreviewPath);
                var root = Path.GetFullPath(Plugin.ExtensionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var prefix = root + Path.DirectorySeparatorChar;
                if (!preview.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return Path.ChangeExtension(preview, ".tiles");

                var relativePreview = preview.Substring(prefix.Length);
                var relativePack = Path.ChangeExtension(relativePreview, ".tiles");
                return GetPackPath(relativePack);
            }
            catch
            {
                return Path.ChangeExtension(absolutePreviewPath, ".tiles");
            }
        }

        internal static string ResolvePackForPreview(string absolutePreviewPath)
        {
            if (string.IsNullOrWhiteSpace(absolutePreviewPath)) return string.Empty;

            var cached = GetPackPathForPreview(absolutePreviewPath);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                return cached;

            // Compatibility fallback for older pre-cache test builds that stored the pack
            // immediately beside the generated preview.
            try
            {
                var local = Path.GetFullPath(Path.ChangeExtension(absolutePreviewPath, ".tiles"));
                if (File.Exists(local)) return local;
                return cached;
            }
            catch
            {
                return cached;
            }
        }

        internal static void EnsureDirectories()
        {
            Directory.CreateDirectory(PacksRoot);
            Directory.CreateDirectory(StateRoot);
            Directory.CreateDirectory(StagingRoot);
        }
    }
}