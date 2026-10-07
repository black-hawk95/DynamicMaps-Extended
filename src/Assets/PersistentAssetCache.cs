using System;
using System.IO;

namespace DynamicMapsExtended
{
    // Prepared tile packs ship inside the plugin folder.
    internal static class PersistentAssetCache
    {
        private const string CacheFolderName = "AssetCache";

        internal static string Root => Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, CacheFolderName));
        internal static string PacksRoot => Path.Combine(Root, "Packs");
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

            // Static raster artwork (including Manimal Abstract) ships its pack
            // beside the preview instead of in AssetCache/Packs.
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

    }
}
