# R2.17 part 3 — byte-level SHA-256 scan feedback and deterministic cancellation

## User-visible behavior

The original WPF "① 読み取り専用スキャン" scan button remains a read-only duplicate discovery operation. Previously, an individual very large JPG/PNG/MP4 file could spend a long time in SHA-256 without updating the progress bar. The application now distinguishes stages:

- **Stage 1 — inventory**: indeterminate progress because the directory's total inventory size/count is not yet known.
- **Stage 2 — NTFS deep metadata**: indeterminate progress, with the existing textual count messages.
- **Stage 3 — SHA-256 candidates**: progress becomes a determinate percentage based on byte counts over all same-size candidates; the text additionally shows the current file's byte percentage while reading.
- **Finished**: progress is 100%, including scans with zero same-size hash candidates.
- **Canceled**: the cancellation token interrupts inside a single file's SHA operation; no partial duplicate-result object is returned.

Byte progress is an indicator of **work covered**, not amount compressed or freed, nor even verified duplicate content. Even candidates rejected by full NTFS metadata checks count as completed work; sources remain untouched. No file paths are used in typed progress records.

## Implementation

- Safety.ComputeSha256 retains the original SHA-256 behavior, pooled 256 KiB buffers, and default call syntax. The new optional callback reports bytes-read every **4 MiB** or EOF. A token canceled inside the callback throws before any digest can be returned. Buffer return retains the clear-on-return safety behavior.
- Scanner.Run optionally reports ScanHashProgress while preserving the existing human-readable IProgress<string> interface. This avoids breaking existing callers and tests. The progress record includes settled candidate file count, total candidate count, accumulated bytes and current file bytes; percentages are bounded to 0–100.
- The WPF scan button subscribes to typed progress and updates JobProgress and ProgressText on the UI synchronization context. The existing Stop_Click cancellation path is retained. Queued callbacks check the busy state and token to avoid showing misleading progress after a cancellation. The progress bar resets from indeterminate in all outcomes.
- **No scan checkpoint or automatic resume feature** is introduced. A stopped scan must be started again. PNG/JPG *candidate batch* resume is a separate existing workflow.

## Generated-only Windows safety checks

tests/R217HashByteProgressTests.cs creates **two separate generated 12 MiB MP4-named paths** with byte-identical data and aligned timestamps in the isolated NTFS test root:

1. Independent framework SHA256.HashData vs Safety.ComputeSha256 exact digest.
2. At least three byte-level callbacks, monotonic progress, exact expected length and a mid-file progress event.
3. Scanner.Run must still find **one safe exact duplicate group**, with 2 deep metadata checks and 2 SHA attempts.
4. Total candidate progress must move monotonically, end at 100%, and show a nonzero subfile percentage while hashing.
5. Cancel Safety.ComputeSha256 synchronously on its first mid-file callback: verify OperationCanceledException, no digest.
6. Cancel Scanner.Run synchronously during the first file's hash: verify OperationCanceledException, no partial ScanResult is returned.
7. Independently SHA-256-check both originals after all tests, and confirm their NTFS identities remain separate.

The suite writes an aggregate JSON report to diagnostics/r217-hash-byte-progress.json. The CI checks all key fields plus continued shutdown of production hardlink merge and original replacement. Tests do not use or mutate actual user JPG/PNG/MP4 files.

## Limits and next work

- The Windows CI compiles WPF and probes the new typed progress and cancellation from the actual scanner; **automated UI clicking of the visible Stop button is NOT performed**. Manual GUI interaction testing remains outstanding.
- Cancellation occurs during buffered hashing, not necessarily the exact millisecond the button is clicked; a 256 KiB read can finish first.
- Stage 1 inventory and full NTFS capture are not yet determinate as a percentage of total work. The byte-based percentage starts at the SHA stage; therefore the bar is **not an overall full-scan elapsed-time estimate**.
- Huge data sets with many repeated sizes still need controlled cache/disk performance profiling.
- All production source replacements, hardlink merges and independent undo remain disabled. Actual user freed capacity is **0 B**.

## Windows CI result

Pending first PR workflow; do not assert verification or an exact pass count until confirmed.
