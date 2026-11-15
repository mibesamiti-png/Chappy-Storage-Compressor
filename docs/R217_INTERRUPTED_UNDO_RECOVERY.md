# R2.17 Part 25 — real interrupted Undo process death and synthetic-only replay

## The actual failure window

Part 23 demonstrated a real Core independent Undo from a known original in-memory file snapshot; Part 24 persisted the completed merge history, so the original metadata could be reconstructed after restart. But the independent Undo method still had a multi-step, non-atomic sequence:

1. Copy a hardlinked alias into a staged **independent** NTFS file.
2. Validate hash, owner/group/DACL, timestamps, attributes and file IDs.
3. Call `File.Replace(stage, alias, null)`. After this returns, the alias is **no longer a hardlink**, even if the process immediately dies.
4. Reapply captured per-alias owner/group/DACL and timestamps/attributes that Windows may transfer from the replaced file.
5. Re-check all identities/bytes/security and mark the completed operation `restored`.

A crash in step 3 or 4 cannot safely be repaired by blindly calling the original Undo again, because the alias is **already independent**. This must be detected and treated as a separate recovery path.

## Production surface

`Core/IndependentUndoCrashJournal.Inspect(inputRoot)` reads versioned, bounded JSON records in `.ChappyDedupeStore/.chappy-independent-undo-recovery`, validates GUIDs and path containment, NTFS attributes, no unknown streams/reparse ancestors, original content hash, actual linked vs independent identity, stage evidence and expected link counts. Untrusted evidence is marked **ManualReview**, not automatically deleted, renamed or applied.

The real WPF **「保存済み統合履歴を確認（読み取り専用）」** button now includes `独立Undo中断履歴`: unfinished, manual-review and verified-finished counts. It never writes journals, copies stages, calls `File.Replace` or enables production Merge/Undo. The routed WPF test asserts the empty-history view remains read-only and leaves original file IDs/hashes and store absence untouched.

## Exactly guarded synthetic phase changes

Only when compiled with `CHAPPY_TESTING` and operating under an exact `ChappyStorageSmoke_` system temporary root with a `.chappy-part23-generated-only` sentinel, `IndependentUndoEngine.Restore(...testOnlyCompletedOperationId:...)`:

- loads the original file identity, hash, timestamps, attributes and owner/group/DACL from the **completed merge ledger**, not the current file;
- saves a flush-to-disk `intent` **before** copying the first staged byte;
- marks `staged` after independent stage hash/file-ID/security/time comparison;
- marks `cutover` after `File.Replace` and before finishing metadata;
- marks `metadata` only after exact independent file-ID, source/master byte and restored metadata validation;
- allows an explicit, separately authorized `ResumeSynthetic` to finish the ledger. A completed Undo record is eventually marked `finished` with no unknown file deletion.

Actual test-only process kills are positioned **at** `intent-written`, `stage-validated`, `post-cutover-before-metadata` (immediately after `File.Replace` and before the `cutover` state update), `metadata-restored-before-ledger`, and `after-ledger-restored-before-journal`. An independent parent process reopens the journal after real worker death.

When the alias remains shared, recovery is **not automatic**: it must validate the exact original link IDs, both source/master hashes and the known independently staged file. An explicit test-only recovery may remove only its own identity-checked stage, archive the original attempt, then repeat the real Core Undo. If the stage has changed, nothing is deleted and the state is ManualReview.

When the alias is already independent, recovery **never** creates a fresh stage or repeats `File.Replace`. It requires alias ID different from master, alias link count 1, exact expected decrement of the original master's link count, intact master and alias SHA-256, and matching original metadata **from the saved completed ledger**. Only then can it reapply security/ordinary attributes/timestamps on the independent alias, verify, mark the completed history restored and finalize the Undo phase. A properly restored ledger record survives a crash immediately before phase finalization.

## Acceptance evidence

`diagnostics/r217-interrupted-undo-replay.json` records five real independent worker kills, two successful *before cutover* explicit retries, three successful *after cutover* recovery continuations, exact original byte/NTFS identity/security/time preservation, and a deliberately changed stage that fails closed without deletion. Windows CI compares every checkpoint name and strict source/safety boolean, retains all prior 43 test steps, source-pinned official libjpeg-turbo, WPF routing, NTFS crash/ACL/ADS, and win-x64 publish.

## Explicit nonclaims

This code does not create atomic NTFS transactions or guarantee survival of disk cache on a real sudden power cut. It tests a single alias per completed ledger in disposable NTFS, not multiple independently modified names/real-user permissions/SACL/EFS/ADS, third-party concurrent writers, isolated VHDX physical allocation or user-consented production GUI cutover. No untrusted or unknown file is automatically removed. All production hardlink consolidation, independent Undo and original replacement are still disabled. Real-user capacity recovered **0 B**; old estimated source coverage **69.4%** is not release readiness.
