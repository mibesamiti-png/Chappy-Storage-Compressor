# R2.16 — Two-stage scanner for large personal JPG/PNG/MP4 collections

## User-facing goal

Improve the read-only, byte-identical duplicate scan for a personal Windows NTFS HDD/SSD library without weakening source safety or enabling any actual file mutation.

### Original work

Previously the scanner called the expensive `Safety.Capture` procedure for **every** eligible file, reading NTFS file ID, security descriptor (owner/DACL), alternate-stream inventory and metadata before it knew whether any other file had the same size. It then hashed only repeated-size candidates.

### R2.16 design

**Stage 1 — shallow inventory**: Traverse the selected folder, skip transaction backups, junction/reparse directories, protected/system/offline/readonly/sparse/encrypted/compressed files and nonmedia when the media-only filter is set. For remaining files collect **path, length, UTC creation/write times and ordinary attributes** only. Group these records by byte length. An ordinary single-size file cannot be an exact byte duplicate in this same inventory, so it is not hashed and does not need an expensive NTFS security descriptor read.

**Stage 2 — full authentication for repeated-size files**: Every candidate with a repeated byte length must still pass `Safety.Capture` (including NTFS identity, owner/security descriptor, ADS exclusion, safe attributes and length), match its earlier shallow snapshot, have one hardlink name and undergo same-snapshot checks before/after SHA-256. Hash-equal candidates are rechecked with full byte comparisons. Existing duplicate-group owner/DACL/timestamp differences are preserved for the final safety filter.

The original mutation locks, original data, and prior recovery code are unchanged. The UI logs how many candidates were quickly inventoried vs. how many required expensive validation and hashing.

**Important semantics**: `ScanResult.FileCount` now counts shallow-eligible entries, rather than fully deep-validated files. A file with ADS or a pre-existing hardlink might be shallow-counted but will never enter a duplicate group without passing Stage 2. A file that changes after Stage 1 is rejected from deep candidates rather than hashed. This counter is not a count of fully approved merge candidates.

## Low-allocation content verification

R2.16 also improves the shared `Safety.ComputeSha256` and `Safety.ByteEqual` helpers: instead of allocating a new **1 MiB** buffer for every hash and two **1 MiB** buffers for every byte comparison, they rent **256 KiB** chunks from `ArrayPool<byte>.Shared`, and return every rented buffer with `clearArray: true` in `finally`. The clear-on-return policy is intentional because image/video bytes could be private. This is a memory-allocation optimization, not a change to the hash algorithm, byte equality semantics, source file contents or snapshot policy.

An additional generated-data regression checks 24 repeated SHA-256 + exact-byte comparison cycles, comparing them to a separate SHA-256 reference. It requires **under 4 MB combined managed thread allocations after pool warm-up**, avoiding the former dozens of megabytes of repeated large-array allocations. It does not establish peak process memory under concurrent operations or prove a fixed CPU speedup.

## Windows synthetic measurements

Synthetic smoke test `tests/R216MediaScannerBenchmarks.cs` creates **372 independent test paths**: 360 unique-length JPG/PNG/MP4 files, 3 identical pairs (JPG, PNG, MP4), one same-length/different-byte pair, one ADS-contaminated pair, one pair of linked names sharing a single NTFS file identity.

Acceptance:
- **3** exact duplicate groups are found, without falsely grouping distinct same-size bytes, ADS files, or existing hardlinks.
- **12** paths require full NTFS capture out of **372** shallow candidates; no unique-size file is hashed.
- A legacy-style capture-all control performs 370+ expensive metadata captures, enabling objective **call-count reduction** on this synthetic corpus.
- Cancellation, stage progress, source-file and permissions isolation are checked. Pool reuse checks supplement the original content-correctness tests.
- `diagnostics/r216-scanner-benchmark.json` contains the synthetic file count, full NTFS capture count, SHA attempt count, verified group count, elapsed milliseconds and managed allocation bytes for baseline and two-stage runs, with no paths, usernames, SIDs or raw ACL data. Windows CI validates counters.

**No fixed elapsed-time improvement threshold is asserted.** The two timings are rough single-run measurements influenced by filesystem cache, Windows scheduling and synthetic file-size distribution. The fixture is biased toward unique-length small files; it does **not** prove a fixed speedup for large real libraries with many similarly sized media files. `GC.GetAllocatedBytesForCurrentThread` is a thread allocation indicator, not peak process RAM.

## Next performance and capacity goals

Benchmark substantially larger synthetic media files and volume types, verify stable performance for hundreds of same-sized files (the case where the full checks still run), and independently profile disk throughput/peak memory. Consider carefully scoped cache/revalidation of trusted scan results while preserving current NTFS/ADS/security identity gates. Output remains an *estimate*, not recovered bytes.

**No production file replacement, hardlink consolidation, original media deletion or MP4 transcoding** is enabled. Real recovered free space is **0 B**, and the security tests remain in force.

## Verified Windows result — 2026-11-14

[R2.16 Windows CI #43585060197](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43585060197) completed successfully on the actual tracked WPF source and generated NTFS test data. The complete Windows smoke suite reported **426/426 safety checks passed**, and the existing R2.8–R2.12 NTFS subprocess and metadata tests, WPF build and win-x64 publish also passed.

Synthetic benchmark measurements (one hosted Windows runner, no warm-run average):

| Property | Measured |
|---|---:|
| Shallow-eligible synthetic JPG/PNG/MP4 paths | **372** |
| Legacy-style full NTFS security captures | **371** |
| Two-stage full NTFS security captures | **12** |
| SHA-256 attempts after two-stage filtering | **9** |
| True duplicate groups | **3** |
| Legacy-style capture-only elapsed time | **18.745 ms** |
| Complete two-stage scanner elapsed time | **7.6544 ms** |
| Capture-only managed thread allocations | **609,672 B** |
| Two-stage full scanner managed thread allocations | **302,224 B** |
| 24 warmed SHA-256 + full-byte compare cycles | **23,808 B managed thread allocations** |

**Interpretation:** Full NTFS capture calls were reduced by **96.8% of shallow file count** on the deliberately unique-size-heavy generated fixture. The two timed samples do **different amounts of work** (capture-only vs full scan with hashing/byte comparisons), so their ratio is **not** a reliable real-world speedup multiplier. The allocation figures are also *different workloads*, not a controlled baseline vs new implementation RAM test. The 24 repeated pooled operations do demonstrate that buffers are being reused in a bounded allocation regression. Never extrapolate these synthetic figures to a general filesystem throughput claim.

The first smoke run surfaced a C# switch-expression precedence mistake in the test fixture, repaired without changing the application safety logic. The first pooled-buffer regression then detected redundant `FileStream` internal buffering. The fix uses `bufferSize: 1` for direct reads into the rented 256 KiB buffers; repeated hashes/comparisons now pass the strict allocation test. **No safety gate was removed or relaxed.**
