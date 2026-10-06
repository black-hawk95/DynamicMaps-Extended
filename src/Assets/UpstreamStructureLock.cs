using System.Collections.Generic;
using Newtonsoft.Json;

namespace DynamicMapsExtended
{
    internal sealed class UpstreamStructureLock
    {
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("release")] public string Release { get; set; }
        [JsonProperty("sourceRepository")] public string SourceRepository { get; set; }
        [JsonProperty("pinnedMetadataCommit")] public string PinnedMetadataCommit { get; set; }
        [JsonProperty("currentMetadataUrl")] public string CurrentMetadataUrl { get; set; }
        [JsonProperty("maps")] public List<UpstreamStructureMap> Maps { get; set; }
    }

    internal sealed class UpstreamStructureMap
    {
        [JsonProperty("normalizedName")] public string NormalizedName { get; set; }
        [JsonProperty("tilePath")] public string TilePath { get; set; }
        [JsonProperty("tileSize")] public int TileSize { get; set; }
        [JsonProperty("minZoom")] public int MinZoom { get; set; }
        [JsonProperty("maxZoom")] public int MaxZoom { get; set; }
        [JsonProperty("transform")] public List<double> Transform { get; set; }
        [JsonProperty("bounds")] public List<List<double>> Bounds { get; set; }
        [JsonProperty("coordinateRotation")] public double CoordinateRotation { get; set; }
        [JsonProperty("layers")] public List<UpstreamStructureLayer> Layers { get; set; }
    }

    internal sealed class UpstreamStructureLayer
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("tilePath")] public string TilePath { get; set; }
    }
}