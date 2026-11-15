# R2.17 Part 24 — durable completed-merge ledger, forced restart and independent Undo

## Why a second ledger?

The original R1 `Journal` stores temporary recovery intent for a destructive operation and deletes it once the operation succeeds. A completed merge can therefore exist as linked file names with no durable record of the original independent file ID, per-alias timestamps or security descriptor. The Part23 Undo prototype had to reuse `FileSnapshot` captured in memory just before the merge; it could not work safely after application restart.

This Part24 introduces a **separate** completed-merge ledger. It is not permission to mutate real user media.

## Core storage contract (test-only writes)

For each synthetic duplicate alias, `Core/CompletedMergeLedger.cs` persists a versioned JSON record under the dedicated `.ChappyDedupeStore/.chappy-completed-merge` folder with:

- exact original alias and verified master paths and safe backup suffix; GUID operation ID tied to the existing pending transaction journal
- original SHA-256, length, NTFS volume serial/file index/link count, creation/write timestamps, attributes and original binary owner/group/DACL security descriptor
- actual merged master file identity and expected full link count at completion, checked after reopening

The **actual existing** `DedupeEngine.Run` only uses this writer if a caller explicitly opts into the `CHAPPY_TESTING`-only `testOnlyRecordCompletedMerges` option on an isolated test root that contains the dedicated generated-only sentinel. The code path proceeds as follows:

1. Existing source and backup hashes, identities, NTFS attributes, permissions, equality and durable **pending** journal are validated.
2. A separate `<GUID>.prepared.json` is fully written and flushed to disk, then moved into place **BEFORE** deleting the only independent backup.
3. DedupeEngine deletes that backup. In normal success it independently rechecks live alias/master state and moves the prepared record to `<GUID>.completed.json` **BEFORE** clearing the old transaction journal.
4. If the managed child process dies in between, a prepared record and pending journal survive. **No automatic promotion** or deletion is permitted. The existing RecoveryService must explicitly inspect/clear the verified pending state, and a separate explicit synthetic `ConfirmPreparedSynthetic` requires exact metadata, byte, identity and link-count rechecks before upgrading prepared to completed.
5. Once an independently confirmed completed record is available, a **new** in-memory pre-merge snapshot for Undo is reconstructed from that persisted JSON, not from the original scan. Existing `IndependentUndoEngine.Restore` still performs **synthetic-only** detachment and evidence preservation. After a successful revalidation, the record is renamed to `<GUID>.restored.json`; an independent edit can no longer affect the master.

Every record file is size-bounded, untrusted path/JSON/NTFS attributes are rejected, including forged IDs, foreign root paths, reparse ancestors, duplicate IDs and unexpected hardlink counts. The production inspector never creates a store, reads sensitive descriptor bytes into UI text, changes a file, or authorizes Undo.

## Windows test contract

A new isolated `R217CompletedMergeLedgerTests` fixture:

- Uses the real `Scanner.Run` and `DedupeEngine.Run` on **two synthetic same-content MP4s** and checks a new completed record is independently reloadable after the scan is gone.
- Changes the verified master link count via a test-owned extra alias, checks the entry is **NeedsReview**, refuses an Undo snapshot and preserves all source/evidence bytes; then explicitly deletes only that test alias.
- Appends invalid bytes to the test-owned completed JSON, checks malformed evidence is preserved and does not count as verified, then restores the exact saved JSON.
- Reconstructs pre-merge metadata from the on-disk record and invokes actual test-gated `IndependentUndoEngine.Restore` with exact owner+DACL/times/identity checks, then verifies a persisted restored record.
- Starts a separate guarded Windows test executable using a **second** generated media pair; the child performs a genuine hardlink merge, persists the prepared record, deletes the independent backup and calls `Process.GetCurrentProcess().Kill()` at the existing `FaultInjection.Hit("backup-deleted")` checkpoint. The parent verifies the child died abnormally, exact original SHA-256 retained, one prepared ledger and one pending journal. It refuses premature ledger promotion, independently classifies and clears the existing pending operation with `RecoveryService`, then explicitly confirms the ledger. Finally it rehydrates original metadata from only the persisted ledger, executes isolated independent Undo and checks a restored status.

The WPF STA routed Click test uses the real new **「保存済み統合履歴を確認（読み取り専用）」** button on two generated independent MP4 files. Without any ledger, it must display zero completed entries and create no store, file or link, keep SHA/file IDs identical, and never enable the destructive Merge button.

CI requires `diagnostics/r217-completed-merge-ledger.json` and new `r217-wpf-routed-click.json` fields as strict pass gates, together with every existing Windows 42-step NTFS/official JPEG/PNG/WPF/CI test.

## Explicit limitations

- **Synthetic-only transaction writes and replay.** In the ordinary WPF executable all ledger writers/commit/Undo accessors are excluded; `DedupeEngine.Run`, `IndependentUndoEngine.Restore`, and original-media replacement remain disabled. Only safe history **reading** is available.
- **A single two-file group is proven for the completed ledger**, not arbitrary N-alias groups. The exact link-count check deliberately refuses extra aliases; a production multi-group ledger must model all known names/identities across multiple operations.
- **Not a power-cut guarantee.** Calling `FileStream.Flush(flushToDisk:true)` before a same-volume rename is not a guarantee that the NTFS directory entry and device cache survived sudden power loss. A crash after prepare but before the pending journal clears still requires independent, explicit revalidation.
- **Not fully crash-safe Undo.** The Part23 test-only `IndependentUndoEngine` still has separate stages for copying, `File.Replace`, restoring per-alias owner+DACL/times and final proof. It does not persist and replay all those phase transitions after an actual Undo process death.
- **NTFS ACL limits:** owner/group/DACL bytes are verified in same-account synthetic tests. Arbitrary SACL, alternate data streams, EFS, inheritance, inaccessible files, third-party writers/aliases, actual VHDX capacity accounting, full production GUI and opt-in approval remain outstanding.
- **No real user files touched or freed.** Actual user-space recovered **0 B**. The historical 69.4% estimate is only estimated source implementation, not release readiness. PR-head CI and postmerge main push CI are separate gates.
