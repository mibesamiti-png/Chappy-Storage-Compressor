# Chappy Storage Compressor — R2.8 synthetic measurement status

Updated 2026-11-13 UTC. R2.7 source tracking was integrated into `main` through PR #1 (merge `dbfa6a34`). It builds a version 0.2.6 WPF application, with CI fixes and source organization. The version is intentionally unchanged until a functional release gate is achieved.

## Overall roadmap

| Phase | Source implementation estimate | Gate |
|---|---:|---|
| R1 Duplicate detection / hardlink prototype | 90% | Synthetic safety checks pass; production merging disabled |
| R1.x Failure recovery / safety | 98% | Windows synthetic tests pass; full fault-injection matrix incomplete |
| R2 Lossless JPG/PNG candidate creation | 68% | Candidate-only; original replacement disabled |
| R3 Restore independent files | 15% | Implementation incomplete |
| R4 Batch queue / capacity analysis | 76% | Synthetic CI passes; large dataset benchmarks pending |

**Overall estimated source implementation: 69.4%.** This number is only an unweighted historical estimate of source coverage, not release readiness, measured reliability, or a data-loss probability.

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
3. **R2.8 [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2):** first synthetic NTFS allocation/free-space measurement and exception-based rollback completed; dedicated VHDX, crash/power loss, ACL/ADS, concurrency, and application integration remain open.
4. Establish a release gate for future opt-in mutations only after rigorous rollback and on-disk metadata verification.

Source changes: [PR #1](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/1).
