#!/usr/bin/env python3
"""R2.5 file-link reference model using a disposable local directory.

This tests generic filesystem assumptions only. It cannot prove Windows NTFS
hardlink, alternate-stream, or File.Replace semantics, nor execute any C# code.
"""
import json
import os
from pathlib import Path
import stat
import tempfile


def safe_plain_single(path):
    try:
        info = path.lstat()
        return stat.S_ISREG(info.st_mode) and info.st_nlink == 1
    except OSError:
        return False


def model_approve(artifact, *, named_streams=False):
    return safe_plain_single(artifact) and not named_streams


def main():
    checks = 0
    def check(condition, description):
        nonlocal checks
        if not condition:
            raise AssertionError(description)
        checks += 1
        print('PASS', description)

    with tempfile.TemporaryDirectory(prefix='chappy-r25-python-reference-') as folder:
        temp = Path(folder)
        data = temp/'external.bin'
        data.write_bytes(b'ORIGINAL' * 128)
        check(model_approve(data), 'one standalone regular file is structurally eligible')
        alias = temp/'alias.bin'
        os.link(data, alias)
        check(not model_approve(data), 'identical main bytes with a second hardlink refused')
        check(data.read_bytes() == alias.read_bytes(), 'full byte equality alone cannot prove unlink safety')
        alias.unlink()
        check(model_approve(data), 'independent file after removing extra name')
        symlink = temp/'symlink.bin'
        symlink.symlink_to(data)
        check(not model_approve(symlink), 'symlink target refused')
        check(not model_approve(temp/'missing.bin'), 'missing file refused')
        check(not model_approve(temp), 'directory used as reserved name refused')
        check(not model_approve(data, named_streams=True), 'model flags ADS-like metadata separately')
        journal = temp/'journal.json'
        journal.write_text(json.dumps({'OriginalSha': 'A'*64, 'CandidateSha': 'B'*64}))
        check(model_approve(journal), 'standalone journal eligible in model')
        journal_link = temp/'journal-link.json'
        os.link(journal, journal_link)
        check(not model_approve(journal), 'shared journal is not an authoritative rollback record')
    print(f'R2.5 Python filesystem reference: {checks}/{checks} PASS; NOT a C# / NTFS test')

if __name__ == '__main__':
    main()
