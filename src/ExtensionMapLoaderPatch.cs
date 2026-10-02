using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace DynamicMapsExtended
{
    [HarmonyPatch]
    internal static class ExtensionMapLoaderPatch
    {
        private static readonly HashSet<string> AcceptableExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".json", ".jsonc" };

        private static FieldInfo _writeTimesField;
        private static FieldInfo _mapDefsField;
        private static MethodInfo _mapDefLoadMethod;
        private static MethodInfo _filteredMethod;
        private static MethodInfo _changeAvailableMethod;

        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("DynamicMaps.UI.Controls.MapSelectDropdown")
                ?? throw new MissingMemberException("DynamicMaps.UI.Controls.MapSelectDropdown not found");

            return AccessTools.Method(type, "LoadMapDefsFromPath", new[] { typeof(string) })
                ?? throw new MissingMethodException(type.FullName, "LoadMapDefsFromPath");
        }

        private static void Postfix(object __instance)
        {
            if (__instance == null) return;

            try
            {
                LoadExtensionDefinitions(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Could not load DynamicMaps Extended definitions: {e}");
            }
        }

        private static void LoadExtensionDefinitions(object dropdown)
        {
            var mapsRoot = Plugin.MapsRoot;
            if (string.IsNullOrEmpty(mapsRoot) || !Directory.Exists(mapsRoot))
            {
                Plugin.Debug($"Extension Maps folder not found: {mapsRoot}");
                return;
            }

            var dropdownType = dropdown.GetType();
            _writeTimesField ??= AccessTools.Field(dropdownType, "_writeTimes");
            _mapDefsField ??= AccessTools.Field(dropdownType, "_mapDefs");
            _filteredMethod ??= AccessTools.Method(dropdownType, "FilteredMapDefs");
            _changeAvailableMethod ??= AccessTools.Method(dropdownType, "ChangeAvailableMapDefs");

            if (!(_writeTimesField?.GetValue(dropdown) is IDictionary writeTimes) ||
                !(_mapDefsField?.GetValue(dropdown) is IDictionary mapDefs))
                throw new MissingMemberException("DynamicMaps map-definition dictionaries were not found.");

            var mapDefType = AccessTools.TypeByName("DynamicMaps.Data.MapDef")
                ?? throw new MissingMemberException("DynamicMaps.Data.MapDef not found");

            _mapDefLoadMethod ??= AccessTools.Method(mapDefType, "LoadFromPath", new[] { typeof(string) });
            if (_mapDefLoadMethod == null)
                throw new MissingMethodException(mapDefType.FullName, "LoadFromPath");

            var changed = false;
            foreach (var path in Directory.EnumerateFiles(mapsRoot, "*.*", SearchOption.AllDirectories)
                         .Where(p => AcceptableExtensions.Contains(Path.GetExtension(p))))
            {
                var writeTime = File.GetLastWriteTimeUtc(path);
                if (writeTimes.Contains(path) && writeTimes[path] is DateTime previous && previous == writeTime)
                    continue;

                var mapDef = _mapDefLoadMethod.Invoke(null, new object[] { path });
                if (mapDef == null)
                {
                    Plugin.Log?.LogWarning($"DynamicMaps Extended could not load MapDef: {path}");
                    continue;
                }

                ResolveLayerPaths(mapDef);
                writeTimes[path] = writeTime;
                mapDefs[path] = mapDef;
                changed = true;

                var displayName = AccessTools.Property(mapDef.GetType(), "DisplayName")?.GetValue(mapDef) as string ?? Path.GetFileName(path);
                Plugin.Debug($"Extension MapDef loaded: {displayName} ({path})");
            }

            if (!changed) return;

            if (_filteredMethod == null || _changeAvailableMethod == null)
                throw new MissingMemberException("DynamicMaps dropdown refresh methods were not found.");

            var filtered = _filteredMethod.Invoke(dropdown, null);
            _changeAvailableMethod.Invoke(dropdown, new[] { filtered });

            Plugin.Debug("DynamicMaps dropdown refreshed with extension MapDefs.");
        }

        private static void ResolveLayerPaths(object mapDef)
        {
            var layersProperty = AccessTools.Property(mapDef.GetType(), "Layers");
            if (!(layersProperty?.GetValue(mapDef) is IDictionary layers))
                return;

            foreach (DictionaryEntry entry in layers)
            {
                var layerDef = entry.Value;
                if (layerDef == null) continue;

                var imagePathProperty = AccessTools.Property(layerDef.GetType(), "ImagePath");
                if (imagePathProperty == null || !imagePathProperty.CanWrite) continue;

                var imagePath = imagePathProperty.GetValue(layerDef) as string;
                if (string.IsNullOrWhiteSpace(imagePath) || Path.IsPathRooted(imagePath))
                    continue;

                var normalized = imagePath.Replace('/', Path.DirectorySeparatorChar);
                var absolute = Path.GetFullPath(Path.Combine(Plugin.ExtensionRoot, normalized));

                var root = Path.GetFullPath(Plugin.ExtensionRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (!absolute.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Extension image path escapes its plugin folder: {imagePath}");

                imagePathProperty.SetValue(layerDef, absolute);
            }
        }
    }
}
