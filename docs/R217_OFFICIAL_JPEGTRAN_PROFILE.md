# R2.17 part 6 — real jpegtran profiling from full-commit-pinned official source

## Trust boundary: official libjpeg-turbo source, no downloaded executable

The Windows CI checks out the official upstream repository, tag 3.2.1:

- Official upstream: https://github.com/libjpeg-turbo/libjpeg-turbo
- Exact 3.2.1 tag commit: 7b4993786d856cbccabfc0ed2f923ba000e326cc
- The workflow rejects any checkout whose actual full Git commit differs.
- The Windows CI compiles **jpegtran-static** locally with CMake and Visual Studio 2026, WITH_SIMD=OFF, ENABLE_SHARED=OFF, ENABLE_STATIC=ON and WITH_TESTS=OFF.
- The build product is copied, named jpegtran.exe for compatibility, **only into the temporary CI runner work directory**. No downloaded installer/binary or external source tree is committed or distributed.
- The CI records a SHA-256 digest of the locally built executable and validates the program version.
- Without the explicitly supplied CI environment variable, ordinary local C# smoke tests **skip** the external-program profile; they do not download or execute unknown tools.

This is a **non-SIMD source build**, so performance must NOT be presented as representative of an official tuned Windows build, real user hardware, cold-cache disk performance, or guaranteed reductions.

## What the actual production app JPEG engine is tested against

The test produces 8 different WIC-decodable baseline JPEG files (640×480 each, all synthetic) using a mixture of regular, gradient and noisy pixels. Four have extra COM markers containing only fixed synthetic text.

Using the **actual** JpegBatchPreviewEngine.Run and its JpegLosslessPreviewEngine.CreatePreview code path:

1. Independently decode and marker-audit all source JPEG files.
2. Pass the verified, CI-source-built jpegtran.exe; the real engine invokes `-copy all -optimize -outfile` on source JPEGs and writes separate preview candidates.
3. Measure wall-clock time for the first batch and for repeat/restart; record allocated managed bytes for the single calling thread and process working set at beginning/end (neither is a per-phase peak or physical drive bandwidth).
4. Require all entries finish without failure, a positive count of *smaller* retained candidates, and theoretical candidate-size savings >0.
5. Independently decode **all kept candidates** with Windows Imaging Component (Bgr24), verify **every decoded pixel** against the corresponding original and compare JpegMarkerAudit protected APP/COM/quantization/SOF snapshots.
6. Verify independently computed SHA-256, file lengths and last-write timestamps of every generated source remain unchanged.
7. Re-run the real JPEG batch with the same input/output/tool and verify no additional candidates, same byte-count estimate, valid plan+journal inspection, and no changed originals.
8. Save a structured report in diagnostics/r217-pinned-jpegtran-profile.json, with explicit scope limitations and true/false fields enforced by a separate CI step.

The earlier tests for generated valid JPEG marker/WIC decoding (R2.17 part 2), in-file cancellation (part 3), safe batch resume (part 4), and WPF routed-button Click tests (part 5) remain enabled and separate.

## What it does NOT prove

- **Actual freed Windows disk space is 0 B.** The test writes independent preview candidates outside the originals, consumes space and does not replace, move or delete originals.
- It is not a benchmark of libjpeg-turbo's official optimized installer, native SIMD acceleration, SSD/HDD throughput, real photo content or guaranteed compression ratio.
- The test does not involve real user JPG/PNG/MP4, does not include the official Windows installer and does not download an executable.
- It does not validate every possible Exif/ICC/XMP encoding or unusual nonbaseline JPEG; candidate validation remains conservative and fails closed for unsupported structures.
- No GUI mouse actions or production hardlink consolidation, original replacement, independent undo were enabled by this change.

## Verified Windows CI result — 2026-11-14

[PR Windows CI #43619218988](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43619218988): **26/26 workflow steps passed**, including building the exact source-pinned official libjpeg-turbo 3.2.1 tool, WPF build and win-x64 publish, previous R2.8–R2.12 NTFS security/crash tests, the routed WPF Start/Stop UI test, and **486/486 C# safety assertions** (previously 476, plus 10 newly measured real-jpegtran assertions).

The machine-readable artifact `diagnostics/r217-pinned-jpegtran-profile.json` recorded:

| Observation on this single Windows CI host | Result |
| --- | ---: |
| libjpeg-turbo official tag | 3.2.1 |
| Full source commit verified before building | `7b4993786d856cbccabfc0ed2f923ba000e326cc` |
| Tool reported version | `libjpeg-turbo version 3.2.1 (build 20261114)` |
| Source-built SIMD | Disabled |
| Generated baseline JPEG input files | **8** (4 with synthetic comments) |
| Successfully kept smaller candidates | **8 / 8** |
| Failures, deferred, not-smaller skips | **0 / 0 / 0** |
| Independently verified pixel-identical candidates | **8 / 8** |
| Protected JPEG metadata marker matches | **8 / 8** |
| Input byte total | **690,225 B** |
| Candidate byte total | **598,438 B** |
| Candidate-size savings | **91,787 B** (**13.30% of input bytes**) |
| Initial real JPEG batch wall-clock | **284.64 ms** |
| Repeated batch/resume wall-clock | **5.74 ms** |
| Current-thread managed allocations during first batch | **2,383,920 B** |
| Process working set before and after | **79,126,528 B → 74,252,288 B** (not per-operation peak) |
| Real user disk free-space increase | **0 B** |

All generated source JPEG files retained their pre-run SHA-256, length and modified timestamps. Rerunning the batch created **no duplicate JPEG previews**. The data do not prove cold-cache throughput or an optimized SIMD official binary's performance, and the apparent process working-set difference is **not** a memory-saving claim.

The initial PR CI attempt failed only because the new GitHub Windows runner had Visual Studio 2026, not the specified Visual Studio 2022. The generator was corrected to **Visual Studio 18 2026** and the final PR CI then passed with the actual official-source executable.
