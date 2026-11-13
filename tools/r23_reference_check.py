#!/usr/bin/env python3
"""Portable synthetic file-state reference, NOT a Windows File.Replace test.

Only operates in TemporaryDirectory() created here. The test uses two rename
operations instead of Windows' File.Replace; it checks the planned journal and
rollback state-machine invariant, not NTFS crash consistency.
"""
from pathlib import Path
import hashlib
import os
import shutil
import tempfile


def digest(p):
    return hashlib.sha256(Path(p).read_bytes()).hexdigest() if Path(p).exists() else None


def check_case(stop):
    with tempfile.TemporaryDirectory(prefix="ChappyStorageSmoke_R23_") as temp:
        directory = Path(temp)
        original = directory / "original.bin"
        candidate = directory / "candidate.bin"
        external = directory / "external.bin"
        stage = directory / "stage.bin"
        local = directory / "local-rollback.bin"
        quarantine = directory / "quarantine.bin"
        journal = directory / "journal.json"
        original.write_bytes(b"original-data\x00" * 20000)
        candidate.write_bytes(b"optimized\x01" * 1500)
        old, new = digest(original), digest(candidate)
        shutil.copyfile(original, external)
        if stop != "after-external":
            shutil.copyfile(candidate, stage)
        if stop not in {"after-external", "after-stage"}:
            journal.write_text('{"source":"synthetic"}', encoding="utf-8")
        if stop in {"after-cutover", "after-quarantine", "after-restore"}:
            # NOT atomic; models the normal post-File.Replace state only.
            os.replace(original, local)
            os.replace(stage, original)
        if stop in {"after-quarantine", "after-restore"}:
            os.replace(original, quarantine)
        if stop == "after-restore":
            os.replace(local, original)

        if journal.exists():
            assert digest(external) == old
            assert digest(stage) in {new, None}
            if digest(original) == new and digest(local) == old:
                os.replace(original, quarantine)
            if not original.exists() and digest(local) == old and digest(quarantine) == new:
                os.replace(local, original)
            assert digest(original) == old, f"failed to restore at {stop}"
            assert not local.exists()
            if quarantine.exists():
                assert digest(quarantine) == new
                quarantine.unlink()
            if stage.exists():
                stage.unlink()
            journal.unlink()
        assert digest(original) == old and digest(candidate) == new
        assert not journal.exists()
    print("PASS", stop)


def check_tamper():
    with tempfile.TemporaryDirectory(prefix="ChappyStorageSmoke_R23_") as temp:
        p = Path(temp)
        original, candidate = p / "original.bin", p / "candidate.bin"
        original.write_bytes(b"original" * 30000)
        candidate.write_bytes(b"candidate" * 3000)
        external = p / "external.bin"
        shutil.copyfile(original, external)
        old = digest(original)
        external.write_bytes(external.read_bytes() + b"tamper")
        assert digest(external) != old
        # Under a fail-closed policy, the original remains untouched.
        assert digest(original) == old
    print("PASS backup-tamper-refusal")


if __name__ == "__main__":
    for name in ("after-external", "after-stage", "after-journal", "after-cutover",
                 "after-quarantine", "after-restore"):
        check_case(name)
    check_tamper()
    print("R2.3 independent reference: 7/7 PASS (NOT a .NET/NTFS test)")
