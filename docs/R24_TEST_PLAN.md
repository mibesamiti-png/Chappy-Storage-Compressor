# R2.4 test plan — interrupted preparation and rollback cleanup

Updated: 2026-11-14 JST. **Synthetic-only**: no production photo/video is permitted to be opened for modification.

## State-machine checkpoints

| Checkpoint | Expected journal | Expected user data | Required action |
|---|---|---|---|
| r23-after-external-copy | absent | unchanged | Inspect backup; verified synthetic cleanup only |
| r23-after-stage-copy | absent | unchanged | Inspect backup + stage; verified synthetic cleanup only |
| r24-after-journal-temp | absent (temp record present) | unchanged | Require valid JSON matching source/stage hashes and sizes |
| r23-after-journal | present | unchanged | Re-enter existing rollback procedure |
| r23-after-replace | present | candidate in original path; original in rollback | Restore independent original using journal |
| r24-after-journal-cleanup | absent | restored original | Inspect and clean verified orphan external copy |
| r24-before-final-preflight | present if source mutated | source changed by synthetic test writer | Reject File.Replace; manual review |

## Tests added

- C# `TestR24PreparationRecovery`: three pre-journal stops; safe cleanup; retry; original SHA-256, file identity and full snapshot unchanged; post-journal crash cleanup; backup tamper rejection; last-moment source modification rejection; alternate named stream rejection on NTFS; reserved-name directory collision; reject no-sentinel directory.
- Independent Python reference: `python tools/r24_reference_check.py`. Tests 13 synthetic state observations; this **cannot** demonstrate Windows `File.Replace`, NTFS ACL correctness, physical write durability or hardware failure safety.
- Static contract: `python tools/r24_contract_check.py` examines gating and regression wiring only.

## Required Windows evidence

1. Run `RUN_DIAGNOSTICS.bat` as a normal user on a test NTFS disk; ensure `RESULT.txt` says PASS.
2. Archive build, smoke and publish logs, .NET SDK version and Windows version.
3. Record actual values of original file ID, hardlink count, creation/modified times, attributes and NTFS security descriptor after cutover and rollback.
4. Repeat with deliberate file locks/writer process. Test named alternate streams on local NTFS.
5. Repeat with different physical backup volume and nearly full disk. Do not rely on source-machine `TEMP` as an independent backup.
6. Only after compile and NTFS tests pass, design production image-specific fidelity verification and opt-in replacement. **Do not enable currently.**

## Current results

- R2.4 Python reference: 13/13 PASS.
- R2.4 static source contract: 25/25 PASS.
- Windows C# compilation: NOT RUN in authoring environment (no .NET SDK).
- Windows NTFS tests: NOT RUN.
- Production image file mutation: DISABLED; actual freed disk space: 0 B.
