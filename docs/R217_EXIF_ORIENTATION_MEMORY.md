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

## Real non-sRGB ICC profile test (OS or CC0 Display P3)

First check known Windows system RGB ICC profiles, such as scRGB and wscRGB, only within the OS color directory. A candidate must be a non-reparse regular file, have a valid ICC header, differ in bytes from installed sRGB, and successfully open in WPF ColorContext. On the actual Windows CI host for the first Exif test, **no usable alternative system RGB profile was available**, and this result was recorded as missing rather than falsely claiming verification.

To enable a deterministic real wide-gamut profile test, a tiny **456-byte DisplayP3Compat-v2-micro.icc** is included as test-only base64 under the original creator's **CC0 public-domain dedication**, not as a Windows proprietary ICC or application asset. Provenance is pinned to [saucecontrol/Compact-ICC-Profiles](https://github.com/saucecontrol/Compact-ICC-Profiles), commit `bdd84663061bc4ae95ca70decff54f581e27f702`, blob `f201c4c2cf633ee4fb7a40f0e69a43ca0649c40b`, with independently confirmed SHA-256 `a917f04df365b7331e614a32ea309975006e851b5a39ecb9ace8f07172e9ef6e`. The checksum and ICC signature/size are enforced before use.

If no OS-provided non-sRGB profile qualifies, the SHA-pinned CC0 Display P3 profile is loaded from test-only bytes. The test embeds the chosen real ICC into a separate generated JPEG, runs the real SIMD source-pinned jpegtran preview, independently reassembles the ICC APP2 fragments, checks Exif and source SHA unchanged, and requires **actual WPF ColorConvertedBitmap-transformed pixels to match** before and after preview. A missing or unusable alternate real profile is now **test failure**, not success. No external ICC executable or binary download happens during CI; this fixture is public-domain test data.

## Process memory measurement limitations

For each Exif candidate batch, the test records separate production-engine and independent post-processing audit wall-clock durations, managed allocations on the calling .NET test thread, before/after whole-process working-set snapshots, and **maximum observed whole-process working set from a 10-ms sampler**. It also reports the OS process-lifetime PeakWorkingSet64 counter.

**Neither metric is the true peak memory attributable to the jpegtran child process or a single JPEG operation**. The sampler can miss short peaks; the OS counter includes work since the test process started; external jpegtran is not included in this process's working-set readings. Controlled cold-cache disk I/O, memory attribution and native child-process peaks remain unverified.

## Safety and CI scope

Only disposable generated JPEGs and runner-provided official-source jpegtran tools are used. No personal JPG/PNG/MP4, no output overwriting of originals, no production hardlink merge or independent Undo. No real freed disk space (0 B). The source implementation percentage is kept at its historical 69.4% pending a separately evidenced assessment.

## Verified Windows CI — November 15, 2026

[Windows source test run #43698017577](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43698017577) succeeded: **32/32 workflow steps**, including the new early test compilation and **distinct Core/WPF runner log gate**, existing synthetic Windows NTFS fault/security tests, Win-x64 publish, all **510/510 C# safety checks** (previous 502 + 8 new Exif/orientation/memory checks), and WPF routed Scan/Stop.

The machine-readable report in `diagnostics/r217-exif-orientation-memory.json` recorded:

| This single hosted Windows run | Observed |
| --- | ---: |
| 512×320 synthetic Exif JPEGs | **4** — orientation 1,3,6,8 |
| Official pinned non-SIMD retained candidates | **4/4** |
| Official pinned SIMD retained candidates | **4/4** |
| Exif value 9 and valid-value tampering 6→1 | **Both correctly rejected** |
| Original and candidate post-orientation full decoded pixel match | **All four, both tool builds** |
| Protected JPEG APP1/header metadata and original source hashes/timestamps | **Unchanged** |
| Estimated smaller-candidate bytes per four-image set | **7,717 B** |
| Non-SIMD real production engine time | **63.08 ms** |
| SIMD real production engine time | **66.10 ms** |
| Additional independent Exif/pixel audit per four-image set | **14.77 ms / 7.68 ms** |
| Calling test-thread managed allocations | **~9.46 MB** per mode |
| Sampled whole-test-process maximum working set | **83.4 MB / 81.3 MB** |
| Number of 10-ms memory samples | **6 / 5** |
| Windows alternate RGB ICC found | **No** |
| Public-domain CC0 Display P3 ICC fallback | **Yes, 456 B** |
| Real Display P3-to-BGRA WPF color transforms match | **Yes** |
| Production reclaimed disk space | **0 B** |

The CC0 profile was verified against upstream source commit `bdd84663061bc4ae95ca70decff54f581e27f702` and SHA-256 `a917f04df365b7331e614a32ea309975006e851b5a39ecb9ace8f07172e9ef6e`. It is distributed as minimal public-domain test data only and is never used to convert user photos.

**Interpretation:** Both tool variants accepted the same four smaller candidates and preserved Exif display orientation; the first hosted run does not establish one build is faster. The recorded 10-ms samples are **whole parent process working sets** and can miss transient peaks; they **exclude the external jpegtran child process**, are not per-operation maximum memory, and are not benchmarks of production workloads. The actual disk space recovered remains **0 B**.

An earlier CI iteration passed 510 assertions but the added test preflight accidentally caused the Core runner's log to contain WPF output. This was corrected by limiting preflight to the Core compile and adding a mandatory pair of log-identity checks. The final report explicitly requires both suites to run.
