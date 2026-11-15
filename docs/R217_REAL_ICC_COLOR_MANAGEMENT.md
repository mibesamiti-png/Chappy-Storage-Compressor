# R2.17 part 8 — Real Windows sRGB ICC profile and WIC color conversion

## Goal

Part 7 preserved synthetic ICC-like JPEG APP2 markers, but its fabricated 132-byte header was not a fully transformable color profile. Part 8 upgrades the test to a **real Windows-loadable sRGB ICC profile** with actual Windows Imaging Component color transformations.

## Verified data boundaries

- Read only the Windows OS's installed sRGB color-profile file, selected from the known standard Windows color-profile directory. Reject missing profiles and reparse points.
- Validate actual ICC header size, acsp signature, RGB color space, XYZ/Lab connection space and nonempty tag table.
- Open the copied test-only profile using WPF ColorContext(Uri), and hash the OpenProfileStream bytes to prove the OS profile was opened, not substituted.
- **Never commit, upload, or distribute the actual installed Windows profile bytes.** Only filename, byte length, and SHA-256 go into diagnostics.
- Run only pinned official-source libjpeg-turbo 3.2.1 jpegtran built by existing CI, both non-SIMD and SIMD. No real user JPEGs.

## Test workflow

Generate four disposable WIC JPEGs (640 × 480) containing multiple numbered ICC_PROFILE APP2 segments carrying the *entire genuine OS ICC profile*. Some also carry artificial COM comments. Test with the production JpegLosslessPreviewEngine.CreatePreview in four separate candidate-only output directories in ABBA order: non-SIMD, SIMD, SIMD, non-SIMD.

The test independently:

1. Reassembles source and candidate ICC APP2 fragments. Both must equal the OS profile byte-for-byte.
2. Rejects missing, duplicate or out-of-order ICC APP2 chunks, including deliberate negative test cases.
3. Verifies protected JPEG headers with production JpegMarkerAudit.
4. Uses WPF ColorConvertedBitmap with the real source profile and a standard destination color context; resulting BGRA32 pixel buffers must be identical.
5. Verifies independent SHA-256 and original file timestamps are unchanged in every pass.

## Performance instrumentation

Separate per-pass elapsed times for the production candidate engine (includes jpegtran process, WIC/marker checks, hashing) and the independent ICC/WIC color audit. Record managed allocations on the calling test thread and beginning/ending overall process working-set snapshots.

**Not claimed:** isolated encoder CPU timing, memory peak attributable to JPEG, warm/cold cache control, SSD/HDD bandwidth, physical UI mouse testing, guaranteed SIMD performance, or behavior on real user photos. These require dedicated tests. No production original replacement or hardlink merge is enabled, and actually freed disk space remains **0 B**.

## Verified Windows CI — 2026-11-15

[PR Windows workflow #43690606882](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43690606882): **29/29 workflow steps succeeded**, including source-pinned official libjpeg-turbo 3.2.1 non-SIMD and SIMD builds, WPF build, win-x64 publish, routed Start/Stop UI tests, NTFS R2.8–R2.12 crash/security probes, and **502/502 synthetic C# safety checks** (previous 493, plus 9 for the new real ICC test).

Machine-readable evidence: `diagnostics/r217-real-icc-color.json`, downloadable in the GitHub workflow diagnostics artifact.

| Single-host observation (generated images only) | Result |
| --- | ---: |
| Genuine OS-installed ICC profile | `sRGB Color Space Profile.icm` |
| ICC profile length | **3,144 bytes** |
| JPEG ICC APP2 segments | Multiple numbered fragments, complete byte-exact reassembly |
| Missing/duplicate ICC fragments | Rejected in negative tests |
| Generated images | 4 WIC JPEGs, 640×480 |
| Actual lossless-preview passes | 4 (non-SIMD, SIMD, SIMD, non-SIMD) |
| Candidates accepted and independently color-verified | **4/4 per pass** |
| Total input JPEG bytes | **42,948 B** |
| Total candidate JPEG bytes | **29,866 B** |
| Candidate-size difference | **13,082 B** (approximately **30.46%** of these specific artificial JPEG bytes) |
| Production engine time: baseline pass 1 / pass 2 | **70.78 ms / 64.50 ms** |
| Production engine time: SIMD pass 1 / pass 2 | **66.11 ms / 65.14 ms** |
| Additional independent ICC/WPF color audit time | **18.37–21.86 ms per pass** |
| Test-thread managed allocation sample | approximately **12.1 MB per pass** |
| Process working-set snapshots | approximately **75.2–83.6 MB** in these measurements, not peak attributable use |
| Original SHA-256 / timestamps | Unchanged |
| Production disk space recovered | **0 B** |

These values are **not real-photo compression predictions** and do not demonstrate a reliable SIMD speedup or per-image memory peak. The generated gradients are unusually compressible and the Windows CI caches, process state and hardware are uncontrolled. The goal of this step is to verify that a genuine color-transformable ICC profile survives the candidate path and results in identical explicitly color-converted pixels.

The first CI attempt failed only at compilation of the newly added test due to an array Length/Count typo and a PreviewBytes property mismatch; both were corrected in PR before the successful workflow. No app original-file processing logic was changed.
