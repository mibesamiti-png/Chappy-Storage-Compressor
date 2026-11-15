# R2.17 part 9 — Exif orientation fidelity, alternate ICC and sampled memory

## Purpose

The production JPEG lossless-preview path must preserve Exif orientation because the same encoded DCT image may display at an entirely different angle when its TIFF Orientation tag changes. R2.17 parts 6–8 verified JPEG candidate equivalence and a genuine Windows sRGB ICC/WPF color conversion; this step adds **explicit 1/3/6/8 orientation preservation** and limited, honestly scoped process memory sampling.

## Synthetic Exif fixtures and oracles

Four generated-only 512×320 WIC JPEG sources carry independently constructed APP1 Exif/TIFF IFD0 tag **0x0112** (`SHORT, count=1`), with values 1 (normal), 3 (180°), 6 (90° clockwise) and 8 (270° clockwise).

The C# test performs a separate bounded JPEG APP1/TIFF parser, enforcing Exif's short/value/count, little-endian IFD0 bounds, singular orientation and supported values. It deliberately creates **two disposable negative cases**: reserved value 9 and a changed *valid* value 6→1. Both must be rejected.

Each original is sent into the production **JpegLosslessPreviewEngine.CreatePreview** using both a non-SIMD and a native SIMD `jpegtran.exe` built from the same verified libjpeg-turbo 3.2.1 source commit. Every kept candidate must pass:

- Exact APP1 Exif payload bytes and parsed orientation.
- The production `JpegMarkerAudit` protected JPEG header audit.
- Full decoded Bgr24 pixel equality before applying display orientation.
- Independent explicit pixel-coordinate transformation for each Exif value and full post-transformation rendered pixel equality, including width/height reversal for 90°/270°.
- SHA-256 and original update-time integrity after processing.

This test does not rely solely on WIC or application viewer behavior to infer Exif orientation.

## Non-sRGB Windows ICC profile probe

The test also checks known Windows system RGB ICC profiles with names such as **scRGB Color Space Profile.icm** and **wscRGB.icc**. A candidate must be in the OS color-profile directory, not a reparse point, have a valid ICC header, be *byte-distinct* from the installed sRGB profile, and open in WPF ColorContext.

**If an alternative profile is available**, it is copied only into the disposable test folder, embedded in numbered ICC_PROFILE APP2 segments in an additional generated JPEG, preview-optimized by source-pinned jpegtran, reassembled byte-exactly and color-converted with WPF. This optional probe is honestly recorded; **a missing/unsupported alternate profile does not pass as tested non-sRGB support**. No Windows profile bytes are committed or uploaded.

## Process memory measurement limitations

For each Exif candidate batch, the test records separate production-engine and independent post-processing audit wall-clock durations, managed allocations on the calling .NET test thread, before/after whole-process working-set snapshots, and **maximum observed whole-process working set from a 10-ms sampler**. It also reports the OS process-lifetime PeakWorkingSet64 counter.

**Neither metric is the true peak memory attributable to the jpegtran child process or a single JPEG operation**. The sampler can miss short peaks; the OS counter includes work since the test process started; external jpegtran is not included in this process's working-set readings. Controlled cold-cache disk I/O, memory attribution and native child-process peaks remain unverified.

## Safety and CI scope

Only disposable generated JPEGs and runner-provided official-source jpegtran tools are used. No personal JPG/PNG/MP4, no output overwriting of originals, no production hardlink merge or independent Undo. No real freed disk space (0 B). The source implementation percentage is kept at its historical 69.4% pending a separately evidenced assessment.

## Result

Pending Windows GitHub Actions verification. `diagnostics/r217-exif-orientation-memory.json` is mandatory CI evidence.
