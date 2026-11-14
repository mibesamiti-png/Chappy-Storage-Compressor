# R2.17 part 1 — realistic same-size-heavy synthetic scanner benchmark

## Scope

This is the first, **read-only** portion of [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14). It benchmarks the actual Core/Scanner.cs Windows NTFS read-only duplicate finder, and improves its per-file hash-stage progress frequency. It **does not** release production deduplication, original replacement, recovery or undo.

### Generated-only datasets

The Windows smoke test creates and removes two isolated folders **inside its unique disposable test root**:

| Dataset | Input | Scan expectation |
|---|---|---|
| Unique-heavy | 48 JPG/PNG/MP4-named files with distinct lengths (640–828 KiB) | 48 shallow records, **0** full NTFS checks, **0** hashes, **0** groups |
| Same-size-heavy | 72 independent 1 MiB files, including 3 byte-identical pairs and 66 nonidentical collisions, plus 2 ADS-case names and 2 existing-hardlink names | **76** shallow and deep records; **73** hashes; **3** safe byte-exact groups |

Total on-disk test fixture is approximately **110 MiB** of generated content. Extensions are category labels; **the random-byte fixtures are not decodable JPG/PNG/MP4 media**. Actual codec throughput testing with generated *valid* images belongs to the remaining R2.17 scope.

### Acceptance checks

- Execute two sequential samples of each corpus and output diagnostics/r217-same-size-scanner.json.
- Assert exact count and safe group membership. A separately computed SHA256.HashData(FileStream) reference plus independent full-byte comparison confirms exactly the three ordinary-media pairs; application hashing and equality helpers are not the reference oracle.
- Assert the ADS-staged file and the two existing hardlink names are excluded from reported savings groups.
- Independently fingerprint all generated unnamed file streams before/after scanning; preserve the separate named ADS and the hardlink relationship.
- Pre-canceled scan must stop. The scanner now reports incremental SHA-256 candidate progress at a bounded cadence (approximately 20 updates per batch), not only after every 100 files. Long **single-file** hashing still lacks byte-level progress.
- The existing safety tests and failure-injection probes must keep passing; originals, production hardlink merge, replacement and undo remain locked.

### Performance measurement interpretation

Each sample records elapsed time, process **read transfer bytes** from Windows GetProcessIoCounters, managed bytes allocated on the current thread, working set at completion, peak working set since **process launch**, and private bytes at completion. The report also includes OS/CPU-count/process architecture/NTFS metadata, with **no file paths or usernames**.

Do not interpret Windows process read transfer bytes as physical HDD/SSD reads, or since-process-launch peak working set as per-scan peak RAM. The files are freshly generated and already read by the independent pre-scan fingerprinting step. Therefore the first sample is labeled **cache state unknown**, *not* cold cache, and the repeat is not a scientifically controlled warm cache test. The process may do unrelated I/O. Comparisons between datasets have different workloads. Neither a throughput multiplier nor a timing pass threshold is asserted.

The reported duplicate capacity remains **hypothetical**. Production recovered space is exactly **0 B**, because this change never modifies user media.

### Remaining R2.17 work

1. Controlled warm/cold runs with more sizes, independent disk-throughput sampling, and accurate per-phase memory peaks.
2. Generate valid synthetic PNG/JPEG images and profile the combined candidate workflow without changing codec or external-executable trust rules.
3. Test cancellation and progress in the actual WPF GUI while hashing one very large file; improve byte-level feedback if needed.
4. Simplify repeat preview selection without automatically running untrusted jpegtran.exe or reusing unsafe folders.
5. Keep production mutation gates and advanced ACL/ADS exclusions unchanged until separate end-to-end safety approval.

## Verified result — Windows GitHub Actions 2026-11-14

[Successful CI run #43593782763](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43593782763) on commit d87b1f3bd571c3290681f5ecd51546c172f3c3d1:

- **438/438** combined C# safety assertions passed (R2.16 baseline 426 plus twelve R2.17 checks).
- Existing R2.8–R2.12 NTFS safety subprocess probes, WPF build, and win-x64 EXE publish all passed.
- **115,769,344 bytes** of generated inputs; 48 unique-size and 76 same-size NTFS file paths.
- The 48 unique-size corpus produced 0 deep security checks, 0 hash attempts, and 0 groups on both runs.
- The 76 same-size corpus produced 76 deep security checks, 73 hash attempts, and exactly 3 independently verified safe groups on both runs.

| Dataset | First elapsed | Repeat elapsed | Windows process logical read transfer |
|---|---:|---:|---:|
| Unique-heavy | 1.409 ms | 1.123 ms | ~0 MiB in each sample |
| Same-size-heavy | 69.203 ms | 69.739 ms | ~79 MiB in each sample |

These are **single hosted-run samples** with an unknown OS cache state after fixture creation/fingerprinting, not reproducible physical disk speed claims or a speedup percentage. Read-transfer counters include process-level logical I/O. The same-size workload correctly receives *no* shortcut that bypasses NTFS/ADS/identity checks. Recovered user capacity is still **0 B** because production source mutation remains disabled.

The diagnostic artifact includes the machine-readable R2.17 JSON alongside prior smoke/NTFS evidence. Future code changes must re-run the same Windows workflow; do not treat the above results as permanent release certification.
