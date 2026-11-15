# R2.17 part 7 — JPEG protected metadata and native SIMD comparison

## Purpose and safeguards

The prior R2.17 part 6 already confirmed that an official-source libjpeg-turbo 3.2.1 non-SIMD `jpegtran.exe` could generate JPEG lossless previews without changing originals. This round compares **native SIMD enabled** and **SIMD disabled** versions built from the **same fully pinned upstream source** and tests more diverse synthetic JPEG metadata.

The Windows CI:
- Reuses the source at commit `7b4993786d856cbccabfc0ed2f923ba000e326cc` from the official libjpeg-turbo repository. The commit is checked again before the native SIMD build.
- Keeps the previous SIMD-disabled source build for comparison.
- Installs **NASM 2.16.03** (Chocolatey version 2.16.3) solely on the disposable CI runner for compiling x64 assembly. Chocolatey's pinned package carries the NASM official x64 installer SHA-256 `657E1252676CFB26A008835C20A760F731C8E0414469A4ED0F83F0FB059CDD35`. The CI checks the running assembler version.
- Configures `WITH_SIMD=ON`, `REQUIRE_SIMD=ON` and checks both CMake build settings. Any missing SIMD tool, checkout mismatch, or build error fails closed.
- Builds a second `jpegtran.exe` and records two distinct executable SHA-256 fingerprints. Both binaries are temporary CI artifacts used **only against synthetic generated files** and are **not distributed** in the published app.

## Generated JPEG variants

Eight JPEGs are independently encoded by Windows Imaging Component at 640×480 resolution. The test injects only artificial APP/COM marker payloads: minimal Exif TIFF with orientation 1, XMP XML text, a two-segment synthetic ICC profile envelope, Photoshop APP13 resource payload, plain COM, and combinations.

This validates a controlled range of **synthetically structured** markers, **not** the correctness of every production Exif tag, ICC color conversion, camera RAW workflow, multipage image, privacy metadata, or application-specific marker encoding.

For every retained preview:
1. Use the production `JpegLosslessPreviewEngine.CreatePreview` path with actual locally built `jpegtran.exe -copy all -optimize`.
2. Independently compare WIC-decoded Bgr24 pixels byte-for-byte.
3. Compare the production `JpegMarkerAudit` snapshot and an **independent raw APPn/COM byte collector** that preserves order, content and segment lengths.
4. Check original JPEG SHA-256 and modified timestamps after each comparison run; verify no source mutation.

## Timing

The same eight generated files are processed in **ABBA order**: baseline (no SIMD), SIMD, SIMD, baseline. Each pass uses a separate empty candidate directory. The result records four elapsed wall-clock durations and the median of two per variant, plus candidate bytes. These times **include** process launch, safety checks, JPEG WIC pixel validation and other I/O, and are therefore not a pure encoder-speed microbenchmark. All four passes must yield independently verified smaller previews.

**No speedup claim is asserted** from a single CI runner, since thermal state, CPU scheduling, filesystem cache (cold/warm), real HDD/SSD reads and OS noise are uncontrolled. It is a demonstration of behavioral correctness and a scoped performance sample, not an expected reduction for real photographs.

## Safety gates and exclusions

- No real JPG, PNG or MP4 files. No production source replacement, hardlink consolidation or independent undo. Original media remain untouched.
- Only **candidate-size differences** are reported. Actual production disk free-space recovery remains **0 B**.
- No interactive physical desktop mouse UI verification.
- The existing non-SIMD JPEG batch/resume test, 486 C# assertions, WPF routed UI tests, NTFS failure-recovery probes and win-x64 publication remain mandatory.

## Verified Windows CI — November 15, 2026

[Windows PR CI #43688471702](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43688471702) **SUCCESS: 28/28 steps**, including source-pinned non-SIMD and **required native x64 SIMD** builds, NASM 2.16.03, WPF application build and publish, the WPF routed Click test, previous R2.8–R2.12 NTFS probes, and **493/493 C# safety checks** (previous 486 + 7 new metadata/SIMD checks).

The independent structured report `diagnostics/r217-jpeg-metadata-simd.json` gives these **single-host generated-fixture observations**:

| Property | Result |
| --- | ---: |
| Official source libjpeg-turbo | v3.2.1, commit `7b4993786d856cbccabfc0ed2f923ba000e326cc` |
| NASM version verified | 2.16.03 |
| Distinct pinned-source binaries | 2; SHA-256 fingerprints differed |
| Generated JPEGs | 8, with synthetic Exif, XMP, split ICC, Photoshop APP13 and COM combinations |
| Successful smaller, independently decoded candidates | **8/8 per run, four runs total** |
| Full decoded pixel and raw APP/COM byte matches | **all candidates** |
| Input bytes across the 8 files | **545,113 B** |
| Accepted candidate bytes per run | **492,314 B** |
| Per-run theoretical candidate reduction | **52,799 B** (approximately **9.69%**) |
| No-SIMD pass 1 | **306.1212 ms** |
| Native-SIMD pass 1 | **292.1094 ms** |
| Native-SIMD pass 2 | **321.6682 ms** |
| No-SIMD pass 2 | **375.4172 ms** |
| No-SIMD median of two | **340.7692 ms** |
| SIMD median of two | **306.8888 ms** |
| Changes to originals' SHA-256/timestamps | **None** |
| Actual recovered Windows storage | **0 B** |

**Interpretation:** This small single-CI sample shows a shorter median observed end-to-end candidate-generation time for the SIMD build, but includes source checks, subprocess overhead, WIC decode, hashing and file operations. CPU scheduling, cache temperature and filesystem effects are uncontrolled; the difference **cannot establish a portable SIMD speedup or guaranteed savings**. Both builds generated identical candidate total sizes. The synthetic ICC profile exercises marker retention but is not a complete validation of real color-profile management.

Generated fixtures and both build binaries remained CI-only; no original photo/video, hardlink consolidation, original replacement or independent undo was enabled.
