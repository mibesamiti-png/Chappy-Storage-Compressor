#!/usr/bin/env python3
"""Independent *Python reference* checks. Not an execution of C# code."""
import hashlib
import tempfile
from pathlib import Path


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(4096), b""):
            h.update(block)
    return h.digest()


def reference_estimate(folder, candidates, duplicate_groups):
    root = folder.resolve()
    amounts = {}
    for relative, claimed_source_length, delta in candidates:
        path = (root / relative).resolve()
        if not path.is_relative_to(root) or not path.is_file():
            continue
        if path.stat().st_size != claimed_source_length or delta < 0 or delta > claimed_source_length:
            continue
        amounts[path] = max(amounts.get(path, 0), delta)
    preview = sum(amounts.values())
    saving, overlap = 0, 0
    for group in duplicate_groups:
        paths = [(root / name).resolve() for name in group]
        if len(set(paths)) != len(paths) or not all(p.is_file() and p.is_relative_to(root) for p in paths):
            continue
        values = [p.read_bytes() for p in paths]
        if not all(v == values[0] for v in values):
            continue
        # Mirrors the bytewise verification in Scanner, plus a fresh digest.
        if not all(digest(p) == digest(paths[0]) for p in paths):
            continue
        saving += (len(paths) - 1) * len(values[0])
        deltas = [amounts.get(p, 0) for p in paths]
        overlap += sum(deltas) - max(deltas)
    return preview, saving, overlap, preview + saving - overlap


def run():
    with tempfile.TemporaryDirectory(prefix="chappy-r21-ref-") as base:
        root = Path(base)
        (root / 'one.jpg').write_bytes(bytes(range(100)))
        (root / 'two.jpg').write_bytes(bytes(range(100)))
        (root / 'unique.png').write_bytes(b'Z'*80)
        candidates = [('one.jpg', 100, 10), ('two.jpg', 100, 10),
                      ('unique.png', 80, 5), ('../escape.jpg', 100, 100)]
        group = [['one.jpg', 'two.jpg']]
        assert reference_estimate(root, candidates, group) == (25, 100, 10, 115)
        print('PASS: overlapping duplicates counted once')
        (root / 'two.jpg').write_bytes(b'changed-file')
        assert reference_estimate(root, candidates, group) == (15, 0, 0, 15)
        print('PASS: changed source rejected')
        (root / 'two.jpg').write_bytes(bytes(range(100)))
        assert reference_estimate(root, candidates + [('one.jpg', 100, 4)], group) == (25, 100, 10, 115)
        print('PASS: repeated path not double counted')
        assert reference_estimate(root, candidates, []) == (25, 0, 0, 25)
        print('PASS: no duplicate group leaves preview estimate unchanged')
    print('Reference checks 4/4 PASS (not C# Windows tests)')

if __name__ == '__main__':
    run()
