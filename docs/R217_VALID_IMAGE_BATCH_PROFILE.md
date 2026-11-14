# R2.17 part 2 — generated valid PNG/JPEG batch workload

## Goal and boundaries

Profile the existing Windows candidate-generation path using **actual decodable synthetic images** rather than mislabeled random bytes. This is a generated-only and source-preserving test. It does not activate production original replacement, hardlink merge, production undo, MP4 re-encoding, or any JPEG transcoding that hasn't been validated against a trusted external executable.

## Input and controlled phases

The Windows smoke suite creates 12 PNG plus 12 JPEG images in its disposable unique test root. It alternates repetitive/tiled and seeded high-entropy patterns; all PNGs contain valid IHDR, zlib-compressed IDAT, CRCs, IEND and tEXt metadata. PNG raw filtered RGBA scanlines are deliberately stored with zlib NoCompression to give the optimizer a meaningful but **artificial** reduction opportunity. JPEGs are encoded using Windows Presentation Foundation (WPF) JPEG BitmapEncoder, with distinct picture patterns and 86 quality level. No internet, user image, or third-party executable is used.

1. Image generation (kept separate from user-facing runtime).
2. Combined JPG/PNG preflight inventory using the actual application implementation.
3. Actual JPEG batch **plan** calculation and its real image marker checks plus independent WIC full decode. **No jpegtran.exe is downloaded or executed**, so actual JPEG lossless candidate throughput **has not been measured**.
4. PNG source CRC/chunk validation and WIC full decode.
5. **Actual** PNG preview batch using PngBatchPreviewEngine.Run and its existing source safety restrictions, candidate verification, journal, and progress messages.
6. Independent candidate WIC full-pixel equality checks against each original, plus output and saving checks.
7. Repeat PNG batch invocation to measure resumed-journal behavior and verify it does not produce duplicate candidates.
8. Independent SHA-256 fingerprint of all generated JPG and PNG source files before and after the entire exercise.

These are sequential phase samples on a hosted Windows runner. The report records milliseconds and current-thread managed allocations for several phases; these are **not** isolated codec algorithm costs, valid estimates of peak application RAM, or cold-cache disk throughput. The diagnostic output reports only aggregate synthetic counters, with no paths, usernames, photos, security descriptors, or real media.

## Safety and CI gates

- PNG/JPG combined preflight counts exactly 12+12; no source writes or size/identity changes permitted.
- Both PNG and JPEG must pass Windows WIC decoding; JPEG markers must parse; PNG chunks and CRCs must parse.
- PNG batch must have **no failed entries**, create at least one smaller preview, and retain the complete original input bytes.
- Each retained PNG preview must compare equal at every decoded BGRA pixel via **independent WIC decoding**, in addition to the production engine's existing filtered-scanline and preserved-metadata verification.
- Re-running the same PNG batch must validate previous candidates, not generate additional ones.
- No jpegtran.exe, internet, external binary download or user media use. The JPEG lossless **candidate-generation gate remains incomplete** and must not be claimed green from WIC/marker/planning checks alone.
- Continue full pre-existing 438+ synthetic safety assertions and R2.8–R2.12 NTFS probes, WPF build and win-x64 publish.
- JSON report: diagnostics/r217-valid-image-profile.json. Actual reclaimed disk capacity stays **0 B**.

## Interpretation

A high PNG reduction on this fixture is expected because the PNG IDAT is deliberately stored uncompressed, and it must **not** be extrapolated to an average user's already-compressed PNG images. A mix of realistic PNG encoder settings, dimensions, metadata and photo-like sources remains future work. JPEG lossless preview requires an explicitly chosen authentic libjpeg-turbo jpegtran.exe; it cannot be substituted by lossy WPF JPEG re-encoding. The provided WPF encoding of the **test source** is only fixture creation, not the application's conversion method.

## Remaining R2.17 requirements

- Controlled warm/cold cache and per-scan peak memory measurements for realistic large libraries.
- Actual JPEG candidate-generation benchmark with an **explicitly approved, integrity-verified libjpeg-turbo tool**, never a quietly executed untrusted executable.
- GUI stop/resume and byte-level progress during a single huge file, and simpler repeat dialog UX without silently reusing paths.
- Controlled end-to-end on-disk release gates before any real-space recovery feature is enabled.

## Windows CI result

Pending this change's first Windows workflow run; do not mark completion or infer actual performance numbers until verified.
