# R2.17 part 6 — real jpegtran profiling from full-commit-pinned official source

## Trust boundary: official libjpeg-turbo source, no downloaded executable

The Windows CI checks out the official upstream repository, tag 3.2.1:

- Official upstream: https://github.com/libjpeg-turbo/libjpeg-turbo
- Exact 3.2.1 tag commit: 7b4993786d856cbccabfc0ed2f923ba000e326cc
- The workflow rejects any checkout whose actual full Git commit differs.
- The Windows CI compiles **jpegtran-static** locally with CMake and Visual Studio 2022, WITH_SIMD=OFF, ENABLE_SHARED=OFF, ENABLE_STATIC=ON and WITH_TESTS=OFF.
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

## Windows CI result

Pending actual Windows workflow verification. Do not assert successful execution or timing results until CI artifacts are verified.
