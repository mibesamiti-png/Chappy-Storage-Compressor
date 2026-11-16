# R2.17 Part 36 — 同名ファイル調査 UI（読み取り専用）

## End-user problem

Part34 defined the collision policy: keep existing files, put incoming duplicates in the nested `同名ファイル` folder with ` (1)`, ` (2)` suffixes, and write `同名ファイル一覧.txt` at the outermost root. Part35 added a durable, generated-only copy/receipt/memo crash ledger. When the inspector says **ManualReview**, the prior status exposed only a generic explanation; it did not show users which exact output or receipt was missing or mismatched.

## New read-only interface

The shipped WPF `RecoveryWindow` now has a **third tab: 「同名ファイル調査（読み取り専用）」**. It runs `CollisionEvidenceReview.Inspect(selectedRoot)` on a background thread independently of legacy/undo history. Rows show:

- **Kind**: operation record, individual file, outermost `同名ファイル一覧.txt`, or unknown evidence.
- **Attention**: requires manual review vs destination evidence match.
- **State**: planned/not yet written, output exists without a receipt, malformed or inconsistent receipt, destination evidence match, or record incomplete.
- **Original relative path / destination relative path** and exact record ID.
- **Detailed explanation** when selected in the existing lower detail pane.

Only the **selected root's direct `.chappy-collision-journal` directory** is inspected. It does not traverse into unrelated folders. An empty journal is not claimed to prove other locations are clear. If a journal folder is a file/reparse point, is unreadable, has unknown names/`*.tmp`, or an intent/receipt is invalid, the view preserves the evidence and flags it.

Output receipts are independently checked against existing NTFS file **volume/index**, SHA-256 content, length, creation/write times, attributes and owner/DACL security hash. Expected source paths **are never opened or followed from the JSON**; this prevents an untrusted journal from causing the app to hash arbitrary external sources. A successful destination proof is labeled **「保存先証拠一致（元データは未照合）」**, explicitly not a full safe Undo/extraction or permission to resume. Even if the root is clean, production writes remain forbidden.

The new tab **cannot enable the recovery Apply button**, has no repair/rename/delete/retry controls, and does not create journals, report files, directories or partial outputs. Existing legacy recovery Apply is restricted to its own previous tab. WPF refresh treats errors in the collision journal separately from errors in the legacy ledger so malformed data cannot silently disappear from a general "all clear".

## Generated NTFS test matrix

`tests/R217CollisionEvidenceReviewTests.cs` adds eight generated-only cases:

1. Empty root: no journal created, no read/write side effect.
2. Durable intent only: both destination file rows and the root memo show pending, not completed.
3. Durable completed copy/receipt: matching NTFS ID, SHA, metadata and memo are visible; source-independent proof **not** claimed.
4. Interrupted after output Flush(true), before a receipt: orphaned output marked **ManualReview** at the exact destination, never adopted.
5. Corrupted receipt: error and destination path surfaced without altering receipt or originals.
6. Identical SHA but different NTFS file index: flagged ManualReview despite matching bytes and timestamps.
7. Unknown incomplete `*.tmp` in the evidence folder: visible as an attention row, never ignored or deleted.
8. Edited journal with path traversal: rejected before following the malicious path, with original synthetic data untouched.

The existing routed WPF UI smoke test now asserts the third tab is present and read-only, remains without an enabled Apply button, displays no false cross-folder "all clear", and creates no journal when selected.

CI requires `diagnostics/r217-part36-collision-manual-review.json`, all old Part1–35 NTFS tests, WPF publish and independent postmerge main CI.

## Limits and next gates

Part35 **writes remain compiled only under `CHAPPY_TESTING`**. This Part36 viewer is in the shipped app but **does not perform extraction**; general ZIP decompression is not yet implemented here. The read-only viewer trusts metadata as evidence only; hostile administrator edits, reparse races after final path checks, arbitrary ACL/SACL/ADS/EFS and actual power-cut durability are not proven. It does not currently scan descendant output roots or provide safe manual-resolution actions.

All production deduplication, original media replacement and independent Undo remain disabled; real user files modified: **none**, recovered end-user disk capacity: **0 B**. Historical source coverage estimate 69.4% is not release readiness.
