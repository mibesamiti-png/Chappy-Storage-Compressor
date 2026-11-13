# R2.9 — Synthetic process-kill and journal replay

## Scope and safety

This is an **isolated, test-only, .NET 10 Windows** process-recovery experiment in `bootstrap/NtfsProcessKillRecoveryProbe.cs`. It does not use the WPF application or operate on user data. It runs only in GitHub Actions on NTFS, creates UUID-named disposable directories directly inside `RUNNER_TEMP`, verifies a sentinel, and refuses any other input paths. Normal production deduplication and original replacement remain **disabled**.

## Crash scenarios

A parent test process generates a 1 MiB random original and a 512 KiB random candidate with known SHA-256 checksums, then launches a separate worker process. At each selected checkpoint the **worker kills its own OS process** using `Process.Kill` rather than throwing a managed exception. The parent verifies the nonzero exit code and a flushed checkpoint marker.

| Checkpoint | Expected recovery behavior |
|---|---|
| After external on-volume backup, before durable journal | Reject automatic recovery; original bytes must remain intact |
| After journal flush, before replacement | Verify backup and original; no change needed |
| Immediately after `File.Replace` | Restore exact original SHA-256 from the separate backup |
| After copying rollback stage but before rollback replacement | Resume recovery from verified stage; exact original restored |

In the successful-recovery scenarios the parent replays the same transaction a second time and asserts **idempotence**: it must not make further changes.

## Fail-closed conditions

For five independently generated transactions killed after cutover, the parent then verifies that recovery refuses to modify the target when:

1. Backup bytes were tampered with.
2. The backup acquired another hardlink name.
3. The target has an active exclusive writer handle.
4. The target has a named NTFS alternate data stream.
5. The target becomes read-only.

Once the synthetic hazard is reversed, the backup is revalidated and exact restoration must succeed. Every test cleans up its **own generated** sandbox. The harness refuses reparse-point directories/files, unexpected link counts, unknown hashes, malformed/missing journals, and untrusted stages. It does not accept arbitrary user paths.

## Recorded evidence

CI uploads `diagnostics/r29-process-crash.json` with all scenario outcomes, whether a **real separate subprocess was killed**, exact restore count, rejection count and production safety flags. The workflow fails unless all specified scenarios pass.

**Important limits:** Killing an ordinary process does **not** simulate removing electrical power or an OS crash. NTFS log durability, partial sector writes, delayed flushes, real user ACL ownership, file watcher races, and dedicated VHDX volume measurement still need independent work. The backup is independent by file identity but remains on the **same physical runner volume**.

## Verified Windows CI results (2026-11-13 UTC)

[Windows Actions run #43491388797](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43491388797) completed successfully after a native Windows struct layout fix. Diagnostic JSON: `diagnostics/r29-process-crash.json`, archived with the `chappy-windows-diagnostics` artifact.

| Result | Verified |
|---|---:|
| Child processes terminated at flushed checkpoints | **9 / 9** |
| Exact SHA-256 restores following committed cutover | **7 / 7** |
| Unsafe automatic recovery refused | **6 / 6** |
| Second recovery replay makes no new change | **PASS** |
| Existing C# synthetic safety checks | **359 / 359** |
| R2.8 independent NTFS space measurement | **PASS** |
| Windows WPF build + self-contained EXE publish | **PASS** |

The initial run failed before the checkpoint because a `BY_HANDLE_FILE_INFORMATION` P/Invoke struct mapped native FILETIME fields as 8-byte-aligned C# `long` values, shifting the NTFS link-count field. The fix represents each native FILETIME as **two 32-bit integers**, matching Windows DWORD alignment. Fail-closed behavior stopped the test before an unsafe cutover; that initial failure is not counted as a pass.

The results are *process-kill durability and replay evidence on generated TEMP data*, not power-loss safety, end-user recovery or proof of crash-consistent physical storage barriers.

## Acceptance criteria

- 9 disposable child process terminations and verified checkpoint markers.
- 7 exact byte restores after cutover (2 checkpoint scenarios + 5 guard scenarios).
- 6 fail-closed refusals (no journal + 5 unsafe guards).
- Idempotent replay where applicable.
- WPF build and existing 359 C# safety checks continue to pass.
- Production original replacement and hardlink consolidation remain disabled.
- No claims of end-user disk space recovered or power-loss resilience based on these tests.

Work follows [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2) and [R2.8 measured NTFS allocation](R28_VERIFIED_RESULTS.md).
