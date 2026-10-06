using System;
using System.Collections.Generic;
using System.IO;

namespace DynamicMapsExtended
{
    internal static class FactoryClassicAssets
    {
        internal static string Root => Path.Combine(Plugin.MapsRoot, "Factory_Classic");
        internal static string DefinitionPath => Path.Combine(Root, "Factory_Classic.jsonc");

        private static readonly string[] RequiredRelativeFiles =
        {
            "Factory_Classic.jsonc",
            "Layers/factory_layer_-1.png",
            "Layers/factory_layer_0.png",
            "Layers/factory_layer_1.png",
            "Layers/factory_layer_2.png"
        };

        internal static bool Available
        {
            get
            {
                foreach (var rel in RequiredRelativeFiles)
                {
                    if (!File.Exists(Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar))))
                        return false;
                }

                return true;
            }
        }

        internal static string Describe()
        {
            var missing = new List<string>();
            foreach (var rel in RequiredRelativeFiles)
            {
                var path = Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                    missing.Add(rel);
            }

            return missing.Count == 0
                ? $"ready, root={Root}"
                : $"INCOMPLETE, missing={string.Join(", ", missing)}";
        }
    }
}