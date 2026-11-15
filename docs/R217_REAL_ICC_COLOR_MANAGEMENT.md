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

## Windows CI results

Pending verified Windows CI and structured diagnostics/r217-real-icc-color.json. Do not claim success until tested.
