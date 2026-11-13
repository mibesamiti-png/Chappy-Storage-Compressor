# Chappy Storage Compressor — GitHub bootstrap status

Snapshot: **R2.6 source kit (local, not yet uploaded to this public repository)**.

| Area | Source implementation estimate | Verification |
|---|---:|---|
| R1 Duplicate detection / hardlink prototype | 90% | Production merge disabled |
| R1.x Safety / crash recovery | 98% | Windows C# / NTFS tests pending |
| R2 Lossless JPG/PNG candidate creation | 68% | Candidate-only; original replacement disabled |
| R3 Undo successful deduplication | 15% | Incomplete |
| R4 Batch queue / capacity analysis | 76% | Windows validation pending |

**Overall source implementation estimate: 69.4%.** These are subjective feature-coverage scores copied from the R2.6 local kit; they are **not** CI pass rates or release readiness.

### Current repository status

- The repository is public; the bootstrap workflow and compiler probe are committed.
- **Windows bootstrap compiler probe: PASS** (GitHub Actions [run #43468251610](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43468251610)). This verifies the Windows runner, .NET 10 compiler, SHA-256, and disposable TEMP file I/O only, **not** the R2.6 app.
- The **full R2.6 C#/WPF sources and original R2.6 test suite have not yet been uploaded**. Until then CI can only prove the Windows SDK/toolchain via an isolated bootstrap probe.
- No real photos/videos, test media from the user's disks, logs, secrets or backups are committed.
- Recovery of physical disk space is not demonstrated: **0 B**. Original modification remains disabled.
- Next blocker: import the R2.6 SourceKit files, then run and fix Windows builds, tests and publishing.

### Release gate

Never enable automatic hardlink consolidation or in-place replacement simply because CI turns green. Verify synthetic Windows NTFS transactions, crash paths, permissions/ADS, independent backup and media equivalence first.
