# Chappy Storage Compressor — R2.12 synthetic owner/DACL preservation verification

Updated 2026-11-13 UTC. R2.7 source tracking was integrated into `main` through PR #1 (merge `dbfa6a34`). It builds a version 0.2.6 WPF application, with CI fixes and source organization. The version is intentionally unchanged until a functional release gate is achieved.

## Overall roadmap

| Phase | Source implementation estimate | Gate |
|---|---:|---|
| R1 Duplicate detection / hardlink prototype | 90% | Synthetic safety checks pass; production merging disabled |
| R1.x Failure recovery / safety | 98% | Windows synthetic tests pass; full fault-injection matrix incomplete |
| R2 Lossless JPG/PNG candidate creation | 68% | Candidate-only; original replacement disabled |
| R3 Restore independent files | 15% | Separate synthetic detachment tests pass; app undo and metadata guarantees not implemented |
| R4 Batch queue / capacity analysis | 76% | Synthetic CI passes; large dataset benchmarks pending |

**Overall estimated source implementation: 69.4% (unchanged).** This number is only an unweighted historical estimate of source coverage, not release readiness, measured reliability, or a data-loss probability.

## Verified R2.7 tracked-source test result

[Windows pull-request CI run #43479758919](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43479758919) for commit `706b4d789ccc099f50fd7132f92470601e8a3932`:

- Source files imported from verified R2.6 SourceKit ZIP into tracked `Core/`, `tests/`, `tools/`, WPF and docs.
- .NET 10 WPF application build **PASS**: **0 errors, 2 warnings**, down from 6 warnings with archived R2.6.
- C# isolated synthetic safety suite **359/359 PASS**.
- Self-contained win-x64 publish **PASS**, artifact `chappy-win-x64-unverified`.
- Windows independent NTFS hardlink/backup restore probe **PASS**.
- R2.6 structural checks and independent Python filesystem reference checks **PASS**.
- Two source-level test fixes: shared imports for smoke tests, explicit synthetic sentinel handle disposal.
- Four PNG `CA2014` stackalloc-in-loop warnings eliminated by reusing the small spans outside loops.

## R2.8 — measured physical savings on generated Windows NTFS files

New test in [PR #3](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/3); verified [Windows CI #43487742467](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43487742467):

- Two independent 16 MiB synthetic duplicates occupied **32 MiB** of unique-file-ID allocation; after synthetic hardlink consolidation the shared file occupied **16 MiB**. The measured volume free-space increase on that run was **16 MiB**.
- Deliberately uncompressed generated PNG reduced from **1,049,371 B to 3,596 B**, with all non-IDAT chunks and inflated filtered scanlines identical.
- Independent same-volume PNG backup, injected precommit stop, postcommit rollback: **PASS**; exact original SHA-256 restored. Backups consume space, and the PNG logical reduction is **not credited** as recovered end-user disk capacity.
- Existing **359/359** C# synthetic checks, WPF build (0 errors / 2 safety-lock warnings), and win-x64 publish: **PASS**.
- The experiment uses a disposable Windows runner `RUNNER_TEMP` sandbox only. The measurement code is not the production application. Production mutation is still disabled.

[Full measurement table and remaining gates](R28_VERIFIED_RESULTS.md).

## R2.9 — actual child-process termination and guarded journal replay

[PR #4](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/4) / [successful Windows CI #43491388797](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43491388797).

- **9/9** independently launched synthetic child processes were force-killed at durable checkpoints: before journal, after journal, after commit, during rollback stage, plus five adversarial recovery cases.
- **7/7** byte-exact SHA-256 restores and **6/6** fail-closed refusals.
- Unknown/missing journal, altered backup, added hardlink alias, active writer, ADS and read-only attributes remain blocking safety conditions.
- Recovery is **idempotent** on a second replay for completed synthetic transactions.
- The test harness uncovered and corrected a native `BY_HANDLE_FILE_INFORMATION` FILETIME struct alignment bug before any unsafe restoration.
- Existing **359/359** C# checks, R2.8 physical-allocation test, WPF build and win-x64 EXE publish remain PASS.

[Detailed R2.9 test scope, results and limitations](R29_PROCESS_CRASH_GATE.md). This proves **test-only process termination handling**, not sudden loss of power or crash consistency of storage hardware. The independent test backup stays on the same runner drive. No user media was altered, and no extra end-user disk space was recovered in this test.

## R2.10 — synthetic restoration of independent files after deduplication

[PR #5](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/5) / [verified Windows CI #43496747086](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43496747086).

- **4/4** forcibly terminated synthetic child process scenarios passed, with a journal/no-journal distinction.
- **4/4** safe independent restorations (regular operation plus three restart scenarios). Both path names retained the SHA-256 data but used different NTFS file IDs.
- **4/4** editing-independence checks passed: changing one detached name did not modify the other name.
- **7/7** fail-closed refusals passed: missing journal, altered stage bytes, added hardlink, exclusive writer, unexpected ADS, read-only alias and modified shared source data.
- Creation/write timestamps and ordinary file attributes were checked for equality on these synthetic fixtures.
- Independent restoration *increases* physical NTFS allocation; any observed volume free-space delta is noisy and not proof of production capacity changes.
- Baseline **359/359** C# safety checks, R2.8 measurements, R2.9 recovery tests, WPF build and Windows EXE publish remain successful.

[Detailed test plan, evidence and limits](R210_INDEPENDENT_UNDO_GATE.md). This is separate experimental code under `bootstrap/`; **the normal application still cannot perform production deduplication or independent-file restoration**. In particular, the experiment does not prove security ACL/ownership inheritance, nondefault streams, third-party writers, EFS, VHDX, or power-failure durability.

## R2.11 — owner/DACL, alternate-stream and identity fail-closed preflight

[R2.11 PR #7](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/7) and [verified Windows CI #43501540844](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43501540844):

- **7/7** clean metadata baselines and **7/7** unsafe conditions rejected, without changing original synthetic file bytes.
- **2/2** owner+DACL security-descriptor changes detected (target and staged file), even with matching SHA-256 data. Original owner/DACL descriptor is captured in memory and not published in logs.
- **1/1** named NTFS ADS injection detected, with the stream left intact after refusal.
- Extra hardlink alias, same-hash stage file identity swap, read-only original and active exclusive writer also rejected.
- Baseline **359/359** C# checks, R2.8 allocation measurement, R2.9 process-kill recovery, R2.10 independent-file undo, WPF build and win-x64 publish remain successful.

This is an independent `bootstrap/` synthetic **preflight refusal test only**: it does not prove actual ACL/owner persistence through cutover, user-owned metadata restore, SACL/EFS, power-loss safety, race-free file mutation or normal-app integration. Normal app hardlink merge/replacement remain disabled. [Full details](R211_NTFS_METADATA_GUARD.md).

## R2.12 — synthetic owner+DACL preservation across replace and rollback

[PR #8](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/8) / [verified Windows CI #43516165756](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43516165756).

- One generated synthetic NTFS file with a deliberately customized DACL was replaced by an independently staged candidate whose owner+DACL was explicitly copied from the original.
- **1/1** post-replacement owner+DACL SDDL match and staged SHA-256 verified.
- **1/1** independent-backup rollback restored the exact original SHA-256 bytes, owner+DACL SDDL and creation timestamp.
- **5/5** unsafe preflight changes refused, including stage/target DACL tampering, unexpected ADS, identical-hash stage file identity substitution and an extra hardlink.
- Existing **359/359** C# safety tests, R2.8 synthetic NTFS space measurement, R2.9 actual subprocess-kill recovery, R2.10 independent-file detach, R2.11 metadata refusal, WPF build and Windows x64 EXE publish all pass.

[Details and limitations](R212_SECURITY_PRESERVATION_GATE.md). The owner+DACL preservation result is **one synthetic same-account scenario**, not arbitrary ACL/owner preservation or integration into the shipped application. SACL/EFS, nondefault named streams, complex inheritance, real power loss, concurrent source modification and isolated VHDX remain open. **Production deduplication, original-file replacement and undo remain disabled.**

## Remaining risks / safeguards

- The **two remaining CS0162 unreachable-code warnings** are from intentional user-facing and engine-level production mutation locks; these must not be removed to silence diagnostics.
- Only generated synthetic data used in CI. No GUI interactive tests or original personal photos/videos were used.
- PNG/JPG previews are output candidates. Physical disk capacity recovered from end-user media has **not been measured**.
- Original replacement and hardlink consolidation remain **DISABLED** in ordinary app builds.
- Real NTFS crash/power-loss resilience, ADS, ACL, simultaneous mutation and test-drive performance still require controlled evaluation.
- `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip` is retained for audit as a pinned historical source; the tracked tree is now the intended editable source.
- No user media, local diagnostic logs, credentials or private backups are included.

## Next gate

1. **Completed:** merge R2.7's directly tracked source without relaxing mutation locks. Validate the merged `main` CI.
2. Check historical static scripts and update CI to run only against reviewed source, reducing the old pinned ZIP path to archival use.
3. **R2.8–R2.10 [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2):** synthetic allocation, process-kill recovery and independent hardlink detach passed; VHDX, true OS/power interruption, ACL/ownership/ADS and production app integration remain open.
4. Establish a release gate for future opt-in mutations only after rigorous rollback and on-disk metadata verification.

Source changes: [PR #1](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/1).
