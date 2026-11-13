# R2.10 — Independent-file restoration after hardlink deduplication

This is a test-only Windows NTFS experiment, NOT an end-user undo feature.

## Objective

After two generated 4 MiB files are consolidated under one NTFS file ID as hardlinks, recreate the second name as a **fully independent file identity**. Check that both names still have identical SHA-256 bytes, but later changes to the detached name cannot affect the original. This is deliberately the reverse of capacity-saving deduplication: restoring file independence **costs additional physical disk space**.

## Implementation

- Isolated source: `bootstrap/NtfsHardlinkDetachProbe.cs` and its separate .NET 10 project.
- Windows GitHub Actions only. Uses `RUNNER_TEMP` and a newly generated GUID directory with sentinel, rejects external user paths and reparse ancestors.
- Refuses files with unexpected NTFS link count, named streams, reparse points, read-only/system attributes, or changed SHA-256, size, identity.
- Copies the shared alias into an independent staged file and verifies its unique ID. Persists a small JSON journal.
- Rechecks both shared names, physical IDs, original bytes, and stage before `File.Replace(stage, alias, null)`; never edits the other shared name.
- Verifies two independent file IDs, exact SHA-256 bytes, creation/write timestamps, file attributes, and that editing the alias no longer changes the master.
- Tests replay idempotence. A missing journal is a **hard refusal**, not automatic deletion of unknown files.

## Synthetic fault matrix

| Scenario | Expected |
|---|---|
| Ordinary independent-file restore | Complete, byte-exact, independently editable |
| Forced child kill after stage but before journal | Refuse automatic replay, linked files remain intact |
| Forced child kill after journal but before cutover | Replay completes detach |
| Forced child kill just after cutover | Replay recognizes completed detach |
| Forced child kill after postcommit verification | Replay recognizes completed detach |
| Staged file content tamper | Refuse |
| Stage gains another hardlink name | Refuse |
| Writer holds exclusive handle | Refuse |
| Alias gains alternate data stream | Refuse |
| Alias is read-only | Refuse |
| Shared file changed by writer before commit | Refuse on fingerprint mismatch |

Windows CI must produce `diagnostics/r210-detach.json` with **4 separate child-process terminations**, **4 completed independent restorations** including the ordinary path, **4 independent-edit checks**, and **7 refusals** including the missing-journal case.

## Important limits

The experiment only tests generated files and is not part of the installed WPF application. **It does not establish** that NTFS ownership, ACLs, encryption, compressed attributes, nondefault ADS, user-visible file histories, external links, active applications, disk quotas, write-cache/power-loss safety, or real photos/videos can be restored. Rejecting an alternate data stream is not preserving it.

The R2.8 volume space delta was a **saving** from deduplication; R2.10's independent restoration requires additional physical allocation (typically the size of the duplicate), and free-space sampling on a shared runner is noisy. The original images and videos are never accessed. Production hardlink consolidation and production file replacement remain disabled.

The real source restore/undo engine remains blocked until those constraints are handled. See [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2) and [R2.9 process-kill tests](R29_PROCESS_CRASH_GATE.md).
