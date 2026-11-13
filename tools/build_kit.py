#!/usr/bin/env python3
"""Produce a deterministic source-kit archive + a SHA-256 file manifest."""
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED, BadZipFile

root = Path(__file__).resolve().parents[1]
manifest = root / 'SOURCE_MANIFEST.json'
archive = root.parent / 'ChappyStorageCompressor_R2_6_SourceKit.zip'
files = sorted(p for p in root.rglob('*') if p.is_file() and p != manifest and
               '__pycache__' not in p.parts and '.git' not in p.parts)
entries = [{'name': p.relative_to(root).as_posix(), 'size': p.stat().st_size,
            'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in files]
manifest.write_text(json.dumps({'release': 'R2.6', 'fileCountExcludingManifest': len(entries),
    'windowsBuildVerified': False, 'productionMutationsEnabled': False, 'contents': entries}, ensure_ascii=False, indent=2) + '\n',
    encoding='utf-8')
with ZipFile(archive, 'w', ZIP_DEFLATED, compresslevel=9) as z:
    for p in sorted([*files, manifest]):
        z.write(p, (Path('ChappyStorageCompressor_R2_6') / p.relative_to(root)).as_posix())
with ZipFile(archive) as z:
    bad = z.testzip()
    assert bad is None, f'bad ZIP member: {bad}'
    for entry in entries:
        zipped = z.read('ChappyStorageCompressor_R2_6/' + entry['name'])
        assert hashlib.sha256(zipped).hexdigest() == entry['sha256']
print(f'Archive: {archive} | {archive.stat().st_size:,} bytes | {len(entries)+1} files | ZIP+SHA256 verified')
