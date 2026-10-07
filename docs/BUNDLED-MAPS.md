# Complete map packaging

The complete ZIP contains the addon, all 42 prepared raster packs, 42 previews, 10 warm atlases and 43 provenance/readiness records. Original DynamicMaps and MapVariants remain separate dependencies.

Build the base package with `build.ps1`, then use Python 3.11 or newer:

```text
python tools/bundle-maps.py --base-zip BASE.zip --plugin-root PREPARED_EXTENDED_FOLDER --output DynamicMaps-Extended-2.1.0-SPT4.1.zip
```

The bundler uses metadata from the base ZIP. It verifies the prepared cache's metadata hash, per-layer signature, NVTILES2 index/layout and image dimensions, then checks all ZIP CRCs and writes a SHA-256 sidecar. Source files are read only. ZIP64 is supported; staging files, logs and unrelated cache files are excluded.

The DLL does not download or generate artwork and has no repair downloader. Missing artwork requires re-extracting the complete ZIP. A base package is a developer intermediate, not a complete installation.

Frozen v2.0.0 manifest filenames and readiness signatures remain unchanged to preserve the verified artwork set. High-resolution content and world alignment are not altered by packaging.

No GitHub upload is performed. Third-party artwork retains its attribution and applicable upstream terms.
