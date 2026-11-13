# Chappy Storage Compressor — R2.7 verified status

Updated 2026-11-13 UTC. The tracked-source R2.7 branch builds a version 0.2.6 WPF application, with CI fixes and source organization. The version is intentionally unchanged until a functional release gate is achieved.

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

## Remaining risks / safeguards

- The **two remaining CS0162 unreachable-code warnings** are from intentional user-facing and engine-level production mutation locks; these must not be removed to silence diagnostics.
- Only generated synthetic data used in CI. No GUI interactive tests or original personal photos/videos were used.
- PNG/JPG previews are output candidates. Physical disk capacity recovered from end-user media has **not been measured**.
- Original replacement and hardlink consolidation remain **DISABLED** in ordinary app builds.
- Real NTFS crash/power-loss resilience, ADS, ACL, simultaneous mutation and test-drive performance still require controlled evaluation.
- `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip` is retained for audit as a pinned historical source; the tracked tree is now the intended editable source.
- No user media, local diagnostic logs, credentials or private backups are included.

## Next gate

1. Merge and validate R2.7's directly tracked source without relaxing mutation locks.
2. Check historical static scripts and update CI to run only against reviewed source, reducing the old pinned ZIP path to archival use.
3. Add a synthetic-only measured disk-space-recovery test with independent backup and fault injection; report actual disk free space, not just logical byte savings.
4. Establish a release gate for future opt-in mutations only after rigorous rollback and on-disk metadata verification.

Source changes: [PR #1](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/1).
