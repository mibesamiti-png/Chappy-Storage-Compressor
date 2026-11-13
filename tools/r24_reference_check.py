#!/usr/bin/env python3
"""Independent Python fault-state model. Does NOT execute or certify C# / NTFS."""
import hashlib
import json
from pathlib import Path
import tempfile

ARTIFACTS = ('.r23-external-copy.bin', '.r23-stage.bin', '.r23-rollback.json.tmp')


def sha(data):
    return hashlib.sha256(data).hexdigest().upper()


def inspect(d):
    if (d / '.r23-rollback.json').exists():
        return False, 'active journal'
    if (d / '.r23-original-rollback.bin').exists() or (d / '.r23-quarantined-candidate.bin').exists():
        return False, 'ambiguous rollback state'
    existing = [name for name in ARTIFACTS if (d / name).exists()]
    if not existing:
        return False, 'nothing to discard'
    if '.r23-external-copy.bin' not in existing or ('.r23-rollback.json.tmp' in existing and '.r23-stage.bin' not in existing):
        return False, 'invalid ordering'
    try:
        original, candidate = (d / 'original.bin').read_bytes(), (d / 'candidate.bin').read_bytes()
        if not (len(candidate) < len(original)):
            return False, 'candidate not smaller'
        if (d / '.r23-external-copy.bin').read_bytes() != original:
            return False, 'backup mismatch'
        if '.r23-stage.bin' in existing and (d / '.r23-stage.bin').read_bytes() != candidate:
            return False, 'candidate mismatch'
        if '.r23-rollback.json.tmp' in existing:
            data = json.loads((d / '.r23-rollback.json.tmp').read_text())
            if data != {'OriginalSha': sha(original), 'CandidateSha': sha(candidate),
                        'OriginalBytes': len(original), 'CandidateBytes': len(candidate)}:
                return False, 'journal mismatch'
    except (OSError, ValueError, KeyError):
        return False, 'invalid or unreadable record'
    return True, 'verified pre-journal state'


def exercise(case, path):
    good, reason = inspect(path)
    return good, reason


def main():
    passed = 0
    with tempfile.TemporaryDirectory(prefix='r24-reference-only-') as tmp:
        root = Path(tmp)
        original = b'ORIGINAL' * 140
        candidate = b'CANDIDATE' * 20
        def fixture(name):
            folder = root / name
            folder.mkdir()
            (folder / 'original.bin').write_bytes(original)
            (folder / 'candidate.bin').write_bytes(candidate)
            return folder
        def assertion(name, folder, expected):
            nonlocal passed
            actual, reason = exercise(name, folder)
            if actual != expected:
                raise AssertionError(f'{name}: expected {expected}, got {actual}: {reason}')
            print('PASS', name, '(' + reason + ')')
            passed += 1

        f = fixture('external-only')
        (f / ARTIFACTS[0]).write_bytes(original)
        assertion('external only can be inspected', f, True)
        (f / ARTIFACTS[0]).unlink()
        assertion('after cleanup nothing remains', f, False)

        f = fixture('stage-present')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / ARTIFACTS[1]).write_bytes(candidate)
        assertion('external and stage both valid', f, True)

        f = fixture('journal-temporary')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / ARTIFACTS[1]).write_bytes(candidate)
        record = {'OriginalSha': sha(original), 'CandidateSha': sha(candidate),
                  'OriginalBytes': len(original), 'CandidateBytes': len(candidate)}
        (f / ARTIFACTS[2]).write_text(json.dumps(record))
        assertion('validated temporary journal', f, True)
        (f / ARTIFACTS[2]).write_text('{partial')
        assertion('truncated temporary journal refused', f, False)

        f = fixture('tampered-backup')
        (f / ARTIFACTS[0]).write_bytes(original + b'BAD')
        assertion('modified backup refused', f, False)

        f = fixture('tampered-stage')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / ARTIFACTS[1]).write_bytes(candidate + b'BAD')
        assertion('modified candidate stage refused', f, False)

        f = fixture('source-changed')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / 'original.bin').write_bytes(original + b'changed')
        assertion('source changed since backup refused', f, False)

        f = fixture('missing-backup')
        (f / ARTIFACTS[1]).write_bytes(candidate)
        assertion('stage alone refused', f, False)

        f = fixture('active-journal')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / '.r23-rollback.json').write_text('some journal')
        assertion('existing transaction refused', f, False)

        f = fixture('ambiguous-rollback')
        (f / ARTIFACTS[0]).write_bytes(original)
        (f / '.r23-original-rollback.bin').write_bytes(original)
        assertion('orphan rollback refused', f, False)

        f = fixture('post-journal-cleanup')
        (f / ARTIFACTS[0]).write_bytes(original)
        assertion('verified post-journal orphan', f, True)
        (f / ARTIFACTS[0]).unlink()
        assert (f / 'original.bin').read_bytes() == original
        assertion('original unchanged after simulated cleanup', f, False)

    print(f'R2.4 Python state reference: {passed}/{passed} PASS; NOT a C# or Windows NTFS test')


if __name__ == '__main__':
    main()
