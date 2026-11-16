# R2.17 Part 37 — collision evidence integrity and safe review guidance

## User-facing improvement

Part36 introduced a read-only `同名ファイル調査` tab. It correctly highlighted unknown `.tmp` evidence as a separate row but could still show a parent **operation record** as matching because all its named receipts were intact. The result could be visually confusing: a fully verified-looking operation next to a foreign orphan. Part37 changes the parent operation to **ManualReview** whenever any unrecognized evidence exists in that selected root's collision journal. Local verified files may still be labeled **保存先照合済み** (their identity was genuinely checked), but the whole operation is explicitly *not* green.

The selected row in the existing WPF detail pane now displays a **【確認するときの注意】** paragraph relevant to its classification. For example: do not delete or overwrite an output lacking a receipt, retain unknown journal files, and preserve the original data before investigating a broken record. These are **read-only advisory texts**, not commands and not automatic repair steps. Selecting the new tab still cannot enable the legacy Apply button.

## Stronger report integrity

The original Part35 intent persists each incoming original relative path, actual `同名ファイル` collection path and ` (1)` / ` (2)` name, collection reason and UTF-8 report name. The Part36 reader already verifies the actual output against the report receipt's SHA, file ID, timestamps, attributes and owner/DACL. Part37 also **reconstructs the exact expected root `同名ファイル一覧.txt` from the manifest** using the same `CollisionCollectionPlan.ReportText` logic and UTF-8 BOM. It compares the expected SHA against the report receipt before accepting that report. Consequently, merely updating a changed memo's SHA value inside its receipt does not make a semantically altered report look safe.

Further fail-closed checks:
- A recorded `CollectionRelativePath` is only an exact one-or-more-level `同名ファイル/同名ファイル/...` nesting, never an arbitrary directory.
- Collected files must reside beneath that recorded collection path; collection presence and the root report name must agree with `Collected` flags.
- The root report destination must not be the same destination as an incoming file.
- Unknown `.tmp`, unexpected receipt, foreign completion marker and malformed journal record are retained for manual review. They are never deleted by the reviewer.
- Untrusted `SourcePath` in JSON remains **data only**, never opened or dereferenced. File verification uses only confined output paths inside the selected root.

## Windows evidence

`R217CollisionTriageIntegrityTests` exercises seven generated NTFS cases:
1. Fully valid completed journal including canonical root report remains destination-verified.
2. Unknown `.tmp` added to a verified journal turns the entire parent operation into **ManualReview**, without invalidating independently verified local file IDs.
3. Artificially alter only the collision count in the memo **without changing its NTFS file ID, length, timestamps, or security**, and edit the report receipt SHA to match those altered bytes. The canonical mapping still rejects the memo.
4. Forge an arbitrary `CollectionRelativePath` in the intent; parent operation becomes ManualReview.
5. Flip the intent's `Collected` flag without changing the report; parent operation refuses the inconsistent plan.
6. Orphan a `.complete` marker with no intent; it appears as unknown evidence, not successful restoration.
7. Create a disposable unreceipted output; the viewer shows specific instructions to **preserve it**, not auto-adopt, delete or overwrite.

In addition, the real WPF routed-interaction smoke test selects an injected diagnostic display row (UI event-only; **no fabricated on-disk recovery evidence**), verifies that safety guidance renders in the details pane, that Apply remains disabled, and that existing original files preserve SHA-256 and NTFS identities. Strict Windows JSON diagnostics are required in `diagnostics/r217-part37-consistent-collision-triage.json`. All earlier Part1–36 gates, UI/WPF and win-x64 publish, Part29 disposable VHDX capacity and Part35 crash worker processes remain required.

## Release limits

This is an **integrity improvement to a read-only review**. The shipped app contains no Part35 extraction/Undo mutation path; Part35 copy/replay is compiled only with `CHAPPY_TESTING`. No physical power loss, arbitrary path-race immunity, malicious administrator who can simultaneously forge *all* journal/memo/intent evidence, or production crash-safe multi-file commit is proven. Viewer evidence classification does not authorize any destructive operation or full recovery. No user files changed and **0 B real-user disk capacity recovered**. The historical 69.4% source estimate is not product readiness.

Merge only after full PR-head and independent postmerge main Windows CI succeed.
