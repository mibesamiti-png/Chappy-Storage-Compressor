#!/usr/bin/env python3
"""Cross-platform filesystem-identity reference. NOT NTFS or C# behavior proof.

Creates and deletes only temporary synthetic files; does not access user media.
"""
import hashlib
import os
from pathlib import Path
import tempfile


def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        for chunk in iter(lambda: stream.read(8192), b''):
            h.update(chunk)
    return h.hexdigest()


def snapshot(path):
    s = path.stat()
    return (s.st_dev, s.st_ino, s.st_nlink, s.st_size, s.st_mtime_ns, s.st_mode)


def identity_guard(path, expected):
    """Reference implementation of snapshot-before/read/snapshot-after invariant."""
    before = snapshot(path)
    if before[2] != 1 or before[3] != len(expected):
        return False
    h = digest(path)
    after = snapshot(path)
    return h == hashlib.sha256(expected).hexdigest() and before == after and after[2] == 1


count = 0

def check(ok, description):
    global count
    print(('PASS' if ok else 'FAIL') + '  ' + description)
    if not ok:
        raise AssertionError(description)
    count += 1

with tempfile.TemporaryDirectory(prefix='chappy-r26-synthetic-') as tmp:
    base = Path(tmp)
    original = base / 'original.bin'
    stage = base / 'stage.bin'
    backup = base / 'external.bin'
    original_data = b'raw-original-' * 8192
    candidate_data = b'new-candidate-' * 1024
    original.write_bytes(original_data)
    stage.write_bytes(candidate_data)
    backup.write_bytes(original_data)
    initial_identity = snapshot(original)
    check(identity_guard(original, original_data), 'pristine original passes reference guard')
    check(identity_guard(stage, candidate_data), 'pristine staged candidate passes reference guard')
    check(identity_guard(backup, original_data), 'pristine backup passes reference guard')
    stage.write_bytes(b'X' * len(candidate_data))
    check(not identity_guard(stage, candidate_data), 'same-size stage content tamper is rejected')
    stage.write_bytes(candidate_data)
    backup.write_bytes(b'Y' * len(original_data))
    check(not identity_guard(backup, original_data), 'same-size backup content tamper is rejected')
    backup.write_bytes(original_data)
    alias = base / 'stage-alias.bin'
    os.link(stage, alias)
    check(not identity_guard(stage, candidate_data), 'late hardlink creation is rejected')
    alias.unlink()
    # Preserve the original inode under an alias, but swap a new same-byte
    # inode into the old path. Hash-only verification would wrongly accept it.
    old_snapshot = snapshot(backup)
    retained = base / 'external-previous-identity.bin'
    backup.rename(retained)
    backup.write_bytes(original_data)
    check(digest(backup) == digest(retained), 'identity-swap keeps exactly equal SHA-256')
    check(snapshot(backup)[1] != old_snapshot[1], 'same-byte path replacement changes physical file identity')
    check(identity_guard(backup, original_data), 'fresh stable snapshot may verify new path (not persisted journal identity)')
    check(snapshot(original) == initial_identity and original.read_bytes() == original_data,
          'failed preflight scenarios do not modify synthetic original')

print(f'R2.6 independent filesystem reference: {count}/{count} PASS; not C# or Windows NTFS proof')
