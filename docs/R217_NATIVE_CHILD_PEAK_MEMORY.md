# R2.17 part 10 — native jpegtran child peak resident memory

## Why the previous memory probe was insufficient

Part 9 measured peak and sampled **parent .NET test-process** working set. That measurement excludes the external native jpegtran process that actually performs JPEG Huffman optimization. It cannot be used to estimate the native encoder's resident memory.

Part 10 adds an OS-tracked **per-child, process-lifetime peak** measurement, without changing or enabling production JPEG source replacement. Source-built jpegtran v3.2.1 remains pinned to full official Git commit `7b4993786d856cbccabfc0ed2f923ba000e326cc` in Windows CI. Both source-built variants (SIMD-disabled and native SIMD) remain temporary CI tools.

## What the test does

1. Generate **three entirely synthetic**, independent baseline JPEG sources (2560 × 1600) with WPF's JPEG encoder. The patterns use gradients, stripes and deterministic noise, not real user photos or downloaded image data.
2. For each source and each of the two pinned jpegtran binaries, launch **a separate process** with the same `-copy all -optimize -outfile` arguments the production engine uses. Restrict input to the generated source and output to its dedicated disposable preview directory.
3. While each child is alive, poll its OS `PeakWorkingSet64` (peak resident working-set bytes over this **child process's lifetime**) and `PeakVirtualMemorySize64` and `PeakPagedMemorySize64` counters with a 2-ms target wait between observations, then wait for exit. Windows .NET throws when querying this property after process exit. The OS tracks its peak up to each successful observation; any rise just before exit could be missed. Record the actual exit code and elapsed wall time. A missing or zero observed peak, or no successful live observation, is a test failure, not a silent fallback to parent-process memory.
4. Independently check that the direct native candidate is **smaller**, has the same protected JPEG marker snapshot and exactly the same WIC-decoded pixels as its generated source.
5. Run the real production `JpegLosslessPreviewEngine.CreatePreview` path separately with the same input and tool. The two kept outputs must have **exactly equal size, SHA-256 and decoded pixels**, proving the measurement uses equivalent JPEG output rather than a different, untested CLI path.
6. Recheck source hashes and last-write timestamps after all six native child runs.
7. Record per-child details in `diagnostics/r217-jpegtran-child-memory.json`. A dedicated workflow gate checks six successful process exits, three fixtures per tool, memory counters, byte-for-byte parity and production write-lock invariants.

## Memory interpretation

- **Supported:** The **highest observed, OS-maintained peak working-set counter** of each live jpegtran child. This is not a guess based on parent-process memory. Unlike instantaneous working-set polling, each OS peak counter tracks earlier peaks since that child started, but the last moments **after the final poll** could be missed.
- **Not supported:** exact entire-child-lifetime peak, full Chappy app peak, parent-plus-child simultaneous resident peak, private-commit bytes, GPU memory, per-JPEG memory across all supported resolutions, Windows disk-cache memory, worst-case 48MP peak, or guaranteed lower memory usage of SIMD vs baseline.
- **Not controlled:** CPU clock/throttling, warm/cold filesystem caches, SSD/HDD I/O scheduling, parallel machine load, true physical memory pressure. Therefore there is **no universal SIMD speedup or memory-efficiency claim**.
- The direct process and app engine run **sequentially**, so per-child peak measures native compression only. The real application additionally decodes/validates both images, and its total memory can be higher.

## Safety

All test sources and direct/app candidates live under uniquely generated disposable test directories. No personal media, real photos, production file replacement, hardlink consolidation, or independently destructive undo are used. The two test binaries are built from official pinned source on GitHub Actions, not distributed with the app. **Real recovered disk space: 0 B.**

## Verified CI result

Pending final Windows CI output and machine-readable diagnostic review. Do not present any numerical peak until the test and its independent workflow gate pass.
