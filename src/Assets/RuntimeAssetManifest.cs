using System.Collections.Generic;
using Newtonsoft.Json;

namespace DynamicMapsExtended
{
    internal sealed class RuntimeAssetManifest
    {
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("assetSet")] public string AssetSet { get; set; }
        [JsonProperty("release")] public string Release { get; set; }
        [JsonProperty("assets")] public List<RuntimeAssetEntry> Assets { get; set; }
    }

    internal sealed class RuntimeAssetEntry
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("map")] public string Map { get; set; }
        [JsonProperty("layer")] public string Layer { get; set; }
        [JsonProperty("tilePathTemplate")] public string TilePathTemplate { get; set; }
        [JsonProperty("zoom")] public int Zoom { get; set; }
        [JsonProperty("validTileCount")] public int ValidTileCount { get; set; }
        // Historical binary + decoded-pixel fingerprints from the 2026-10-04 capture are retained
        // for provenance/diagnostics. Runtime acceptance is controlled by the separate official
        // upstream STRUCTURE lock (tile paths, transform, bounds, rotation, zoom metadata).
        [JsonProperty("sourcePngBytes")] public long SourcePngBytes { get; set; }
        [JsonProperty("sourceFingerprintSha256")] public string SourceFingerprintSha256 { get; set; }
        [JsonProperty("sourceVisualFingerprintSha256")] public string SourceVisualFingerprintSha256 { get; set; }
        [JsonProperty("validTilesByX")] public List<RuntimeAssetXRange> ValidTilesByX { get; set; }
        [JsonProperty("pack")] public RuntimePackSpec Pack { get; set; }
        [JsonProperty("preview")] public RuntimeImageSpec Preview { get; set; }
        [JsonProperty("warm")] public RuntimeImageSpec Warm { get; set; }
    }

    internal sealed class RuntimeAssetXRange
    {
        [JsonProperty("x")] public int X { get; set; }
        [JsonProperty("yRanges")] public List<List<int>> YRanges { get; set; }
    }

    internal sealed class RuntimePackSpec
    {
        [JsonProperty("outputPath")] public string OutputPath { get; set; }
        [JsonProperty("format")] public string Format { get; set; }
        [JsonProperty("fullWidth")] public int FullWidth { get; set; }
        [JsonProperty("fullHeight")] public int FullHeight { get; set; }
        [JsonProperty("tileSize")] public int TileSize { get; set; }
        [JsonProperty("grid")] public List<int> Grid { get; set; }
        [JsonProperty("columns")] public int Columns { get; set; }
        [JsonProperty("rows")] public int Rows { get; set; }
        [JsonProperty("origin")] public List<int> Origin { get; set; }
        [JsonProperty("records")] public int Records { get; set; }
        [JsonProperty("presentRecords")] public int PresentRecords { get; set; }
        [JsonProperty("missingRecords")] public int MissingRecords { get; set; }
        [JsonProperty("uniquePayloads")] public int UniquePayloads { get; set; }
        [JsonProperty("deduplicatePayloads")] public bool DeduplicatePayloads { get; set; }
        [JsonProperty("expectedBytes")] public long ExpectedBytes { get; set; }
        [JsonProperty("expectedSha256")] public string ExpectedSha256 { get; set; }
    }

    internal sealed class RuntimeImageSpec
    {
        [JsonProperty("outputPath")] public string OutputPath { get; set; }
        [JsonProperty("width")] public int Width { get; set; }
        [JsonProperty("height")] public int Height { get; set; }
    }

    internal readonly struct RuntimeTileCoord
    {
        internal readonly int X;
        internal readonly int Y;
        internal RuntimeTileCoord(int x, int y) { X = x; Y = y; }
    }
}