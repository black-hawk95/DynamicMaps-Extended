using System.Collections.Generic;
using Newtonsoft.Json;

namespace DynamicMapsExtended
{
    // Runtime reads only installation paths. Full provenance remains in the packaged JSON.
    internal sealed class RuntimeAssetManifest
    {
        [JsonProperty("assets")] public List<RuntimeAssetEntry> Assets { get; set; }
    }

    internal sealed class RuntimeAssetEntry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("pack")] public RuntimeAssetPath Pack { get; set; }
        [JsonProperty("preview")] public RuntimeAssetPath Preview { get; set; }
        [JsonProperty("warm")] public RuntimeAssetPath Warm { get; set; }
    }

    internal sealed class RuntimeAssetPath
    {
        [JsonProperty("outputPath")] public string OutputPath { get; set; }
    }
}
