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

The first successfully verified Windows PR workflow [#43700433762](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43700433762) passed **33/33 steps**, including official source-built non-SIMD/SIMD jpegtran, real WPF application build, existing NTFS crash/security probes, routed WPF UI click checks, Win-x64 publish, and **516/516 C# safety assertions** (previous 510 + 6 new checks).

The verified `diagnostics/r217-jpegtran-child-memory.json` recorded six direct native-child samples:

| Generated fixture, 2560×1600 | No-SIMD: highest observed child working-set peak | Native SIMD: highest observed child working-set peak | Input bytes → preview bytes |
| --- | ---: | ---: | ---: |
| Repeated gradient/stripe (0) | **15,704,064 B** | **15,728,640 B** | 158,110 → 101,280 |
| Seeded noisy pixels (1) | **15,781,888 B** | **15,728,640 B** | 3,409,203 → 3,174,788 |
| Synthetic regular pattern (2) | **15,704,064 B** | **15,732,736 B** | 160,913 → 117,769 |
| Total candidate-size difference across 3 fixtures | | | **334,389 B**, approximately **8.97%** |

The per-child native process execution elapsed times for these samples were **38.15, 171.77, 33.52 ms** (non-SIMD) and **31.41, 132.83, 34.63 ms** (SIMD); per-run observations varied. Every direct native output was independently identical in bytes and SHA-256 to the application engine output, with source SHA-256 and timestamps unchanged. The OS peak counters had **3–15 live samples per child**.

**Interpretation:** The figures are *observed Windows peak resident working-set counters from the native jpegtran children*, not parent .NET memory. The roughly 4.35-GB native process peak **virtual address-space** counter is an address-space metric, not 4.35 GB of physical RAM. None of these small artificial fixtures establish worst-case 48MP peak memory, guaranteed low RAM use, or a reliable performance difference between builds. The very last instant before each child exited may not have been sampled.

Actual user's disk space freed is **0 B**; no original replacement, media deletion or hardlink consolidation occurred.

The earliest test [CI #43700101877](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43700101877) failed because .NET cannot read `Process.PeakWorkingSet64` after the child has already exited. The implementation was changed to read OS-maintained peak counters **while the child is alive**, and the diagnostic contract explicitly acknowledges that the final lifetime maximum may be missed if it changes after the last successful live poll.
