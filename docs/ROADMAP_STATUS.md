# Chappy Storage Compressor — R2.6 verified status

Updated: 2026-11-13 UTC. Sources: pinned R2.6 uploaded ZIP, GitHub Actions run [#43474468842](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842).

| Workstream | Source-implementation estimate | Current gate |
|---|---:|---|
| R1 duplicate detection / hardlink prototype | 90% | Windows synthetic tests pass; production linking disabled |
| R1.x safety / crash recovery | 98% | 359 C# safety assertions pass in synthetic Windows run; real-world fault matrix incomplete |
| R2 JPG/PNG lossless candidate creation | 68% | Candidate-only; originals not automatically replaced |
| R3 restore independent files | 15% | Unfinished |
| R4 batch queue / capacity analysis | 76% | Synthetic tests pass; large disk and UI tests pending |

**Overall code coverage estimate: 69.4%** (unweighted mean of historical subjective estimates; unchanged by the CI breakthrough). This number is **not** product readiness, not 359-test pass rate, and not a guarantee of no data loss.

## Verified in the Windows CI

- Windows / .NET 10 WPF application build: **PASS**, 0 errors and 6 warnings.
- R2.6 C# isolated synthetic safety test harness: **359/359 PASS**, including duplicate detection, synthetic hardlinks, PNG/JPG candidate checks, journaling, rollback, tampered artifacts, metadata and synthetic fault injection.
- Self-contained win-x64 publish: **PASS**; [compiled artifact](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842/artifacts/14061958635) named `chappy-win-x64-unverified`.
- [Test diagnostics](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842/artifacts/14063440051) archived.
- Windows independently generated NTFS hardlink and backup restoration probe: **PASS**.
- GitHub repository contains the original pinned `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip`, not the expanded source tree.

## Unverified and blocked for production

- No end-user GUI click-through / installer testing or personal media testing.
- Real recovered disk capacity: **no measurement** (do not count synthetic estimates as actual savings).
- Production file replacement and hardlink merge stay **disabled**.
- The ZIP was immutable during CI. CI applies two targeted **test-only** compatibility patches: inclusion of shared `GlobalUsings.cs` in the smoke-test project, and explicit sentinel stream disposal in `PrepareTestDirectory`. These must be incorporated into the next published source revision.
- Six compiler warnings remain, including `CA2014` potential stack overflow on stackalloc inside loops.
- Need Windows real NTFS identity/ADS/ACL/performance tests and reproducible recovery criteria prior to a production release.

## Next development gate

Move R2.6 files from archived ZIP into normal tracked source directories; permanently fix the test harness; reduce warnings; run CI from the tracked tree; build a measurable, safely revertible *synthetic-only* disk-space recovery gate. **Do not enable production mutation only because CI is green.**
