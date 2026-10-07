"""Bundle a verified local map cache into an Extended installation ZIP (no network).

Usage: python tools/bundle-maps.py --base-zip BASE.zip --plugin-root INSTALLED_PLUGIN --output FULL.zip
The resulting bundle is local; creating it does not publish or grant artwork redistribution rights.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import struct
import zipfile

PREFIX = 'BepInEx/plugins/DynamicMaps-Extended/'
MANIFEST = 'Assets/DMEXT-ASSET-MANIFEST-v2.0.0.json'
STRUCTURE = 'Assets/DMEXT-UPSTREAM-STRUCTURE-v2.0.0.json'

def safe_id(value):
    return re.sub('_+', '_', ''.join(c if c.isalnum() or c in '.-' else '_' for c in value)).strip('_')

def checked_path(root, relative):
    relative = relative.replace('\\', '/')
    parts = PurePosixPath(relative)
    if parts.is_absolute() or '..' in parts.parts or ':' in relative:
        raise ValueError('Unsafe asset path: ' + relative)
    target = (root / relative).resolve()
    target.relative_to(root.resolve())
    return target

def png_matches(path, spec):
    with path.open('rb') as stream:
        header = stream.read(24)
    if len(header) != 24 or header[:8] != b'\x89PNG\r\n\x1a\n' or header[12:16] != b'IHDR':
        raise ValueError('Invalid PNG: ' + str(path))
    if struct.unpack('>II', header[16:24]) != (spec['width'], spec['height']):
        raise ValueError('PNG dimensions do not match manifest: ' + str(path))

def pack_matches(path, spec):
    size = path.stat().st_size
    with path.open('rb') as stream:
        if stream.read(8) != b'NVTILES2':
            raise ValueError('Invalid tile pack: ' + str(path))
        header = stream.read(32)
        expected = (spec['fullWidth'], spec['fullHeight'], spec['tileSize'], spec['columns'],
                    spec['rows'], *spec['origin'], spec['records'])
        if len(header) != 32 or struct.unpack('<IIIIIiiI', header) != expected:
            raise ValueError('Pack layout mismatch: ' + str(path))
        index = stream.read(spec['records'] * 8)
        payload_start = 40 + spec['records'] * 8
        if len(index) != spec['records'] * 8:
            raise ValueError('Truncated index: ' + str(path))
        present = 0
        for offset, length in struct.iter_unpack('<II', index):
            if not length:
                continue
            present += 1
            if length > 4 * 1024 * 1024 or offset < payload_start or offset + length > size:
                raise ValueError('Invalid tile payload range: ' + str(path))
        if present != spec['presentRecords']:
            raise ValueError('Missing tile records: ' + str(path))

def signature(entry, structure):
    p, preview, warm = entry['pack'], entry['preview'], entry.get('warm') or {}
    fields = ['builtin-installer-v4-seam-free-preview', structure.get('pinnedMetadataCommit', ''),
              entry['id'], entry['map'], entry['layer'], entry['tilePathTemplate'], entry['zoom'],
              entry['validTileCount'], p['outputPath'], p['fullWidth'], p['fullHeight'], p['tileSize'],
              p['columns'], p['rows'], *p['origin'], p['records'], p['presentRecords'],
              preview['outputPath'], preview['width'], preview['height'], warm.get('outputPath', ''),
              warm.get('width', 0), warm.get('height', 0)]
    return hashlib.sha256('|'.join(map(str, fields)).encode()).hexdigest()

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--base-zip', type=Path, required=True)
    parser.add_argument('--plugin-root', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.base_zip.resolve() == args.output.resolve():
        raise ValueError('Output must differ from base ZIP')
    root = args.plugin_root.resolve()
    additions = {}
    with zipfile.ZipFile(args.base_zip) as base:
        metadata = [base.read(PREFIX + name) for name in (MANIFEST, STRUCTURE)]
        manifest, structure = map(json.loads, metadata)
        digest = hashlib.sha256()
        for name, data in zip((MANIFEST, STRUCTURE), metadata):
            digest.update(Path(name).name.encode() + b'\0' + data + b'\0')
        state_root = root / 'AssetCache/State'
        global_marker = state_root / (manifest['assetSet'] + '.ready')
        if global_marker.read_text(encoding='utf-8-sig').strip().lower() != digest.hexdigest():
            raise ValueError('Global readiness marker does not match the packaged metadata')
        for entry in manifest['assets']:
            marker = state_root / (safe_id(entry['id']) + '.ready')
            if marker.read_text(encoding='utf-8-sig').splitlines()[0].strip().lower() != signature(entry, structure):
                raise ValueError('Stale asset marker: ' + entry['id'])
            pack = checked_path(root / 'AssetCache/Packs', entry['pack']['outputPath'])
            pack_matches(pack, entry['pack'])
            additions['AssetCache/Packs/' + entry['pack']['outputPath']] = pack
            additions['AssetCache/State/' + marker.name] = marker
            for kind in ('preview', 'warm'):
                spec = entry.get(kind)
                if not spec:
                    continue
                image = checked_path(root, spec['outputPath'])
                png_matches(image, spec)
                additions[spec['outputPath']] = image
        additions['AssetCache/State/' + global_marker.name] = global_marker
        snapshots = {key: (path.stat().st_size, path.stat().st_mtime_ns) for key, path in additions.items()}
        print(f'PASS: all {len(manifest["assets"])} layers match the runtime readiness checks.', flush=True)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        report = ('DynamicMaps Extended 2.1.0 - COMPLETE INSTALLATION\n\n'
                  'Includes the release DLL and all 42 prepared raster layers.\n'
                  'Requires original DynamicMaps, MapVariants and applicable map mods.\n'
                  'Exit SPT and extract into the SPT root, allowing overwrite.\n'
                  'Keep Maps, Assets and AssetCache together with the DLL.\n'
                  'No runtime downloader or map generation is included.\n'
                  'Re-extract this complete ZIP to repair missing artwork.\n'
                  'First-use artwork loading can still cause a brief hitch.\n'
                  'Third-party artwork terms and attribution remain applicable.\n')
        with zipfile.ZipFile(args.output, 'w', compression=zipfile.ZIP_DEFLATED,
                             compresslevel=1, allowZip64=True) as output:
            for info in base.infolist():
                if info.is_dir():
                    continue
                name = info.filename.replace('\\', '/')
                if not name.startswith(PREFIX):
                    raise ValueError('Unexpected installation entry: ' + name)
                relative = name[len(PREFIX):]
                if relative in additions or '/AssetCache/' in name or relative == 'BUNDLE-README.txt':
                    continue
                output.writestr(name, base.read(info))
            for index, (relative, path) in enumerate(additions.items(), 1):
                output.write(path, PREFIX + relative.replace('\\', '/'))
                if index % 20 == 0:
                    print(f'Packed {index}/{len(additions)} map files', flush=True)
            output.writestr(PREFIX + 'BUNDLE-README.txt', report)
        for key, path in additions.items():
            if snapshots[key] != (path.stat().st_size, path.stat().st_mtime_ns):
                raise ValueError('Source changed during packaging: ' + str(path))
    with zipfile.ZipFile(args.output) as archive:
        bad = archive.testzip()
        if bad:
            raise ValueError('Archive integrity check failed: ' + bad)
        names = set(archive.namelist())
        if not all(PREFIX + relative.replace('\\', '/') in names for relative in additions):
            raise ValueError('Missing bundled assets')
        if any('/Staging/' in name or name.endswith('asset-preparation.log') for name in names):
            raise ValueError('Unexpected staging/log files')
    with args.output.open('rb') as stream:
        checksum = hashlib.file_digest(stream, 'sha256').hexdigest()
    args.output.with_suffix('.sha256.txt').write_text(checksum + '  ' + args.output.name + '\n', encoding='utf-8')
    args.output.with_suffix('.README.txt').write_text(report, encoding='utf-8')
    print(f'PASS: complete ZIP CRC verification; {args.output.stat().st_size / 1e9:.2f} GB.', flush=True)
    print(args.output, flush=True)

if __name__ == '__main__':
    main()
