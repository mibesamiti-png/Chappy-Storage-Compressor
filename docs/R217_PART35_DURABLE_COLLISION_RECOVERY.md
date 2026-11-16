# R2.17 Part 35 — durable collision extraction/recovery ledger (synthetic-only)

## Goal

Part34 introduced the user-approved destination collision policy: never overwrite existing data; collect collisions in a nested `同名ファイル` folder with ` (1)`, ` (2)` filenames; place `同名ファイル一覧.txt` at the outermost output root. Part34 had **no durable recovery record before output creation**. An actual process death after a file was copied but before the report was saved could leave outputs of unknown ownership.

Part35 implements an **append-only intent / per-file receipt / report receipt / verified completion** experiment, exclusively in generated disposable Windows NTFS fixtures. The product's normal write entrypoints remain disabled.

## Generated-only protocol

1. **Read-only preview:** `CollisionCollectionPlan.Inspect` supplies the exact original relative names, proposed collision paths, collection folder nesting and top-level report name.
2. **Before output:** `CollisionRecoveryLedger.BeginSynthetic` validates the unique `ChappyStorageSmoke_` TEMP sentinel, output root, all sources under `part35-incoming`, source NTFS identities, SHA-256, attributes, timestamp and owner/DACL; checks the plan is still fresh. It writes a durable, GUID-identified `*.intent.json` to `.chappy-collision-journal` using CreateNew, Flush(true), and no-overwrite rename. Nothing is copied before durable intent exists.
3. **Per output:** `ResumeSynthetic` first reopens all durable evidence with `InspectSynthetic`. Every already-receipted file is checked for its original output NTFS volume/index, exact bytes/SHA, length, timestamps, attributes and owner/DACL hash; existing unreceipted outputs are **never automatically adopted**. It validates unchanged sources and uses CreateNew to write only the next missing destination. Once the bytes have been Flushed(true) and independently rehashed, a new `*.item-0000.json` receipt is durably recorded. No existing receipt is edited or removed.
4. **Top-level report:** after all outputs have verified receipts, the original Part34 UTF-8 BOM report is created under its precomputed root filename using CreateNew and Flush(true). Its own ID, SHA-256, metadata and owner/DACL are stored in a separate immutable `*.report.json` receipt. Report names never overwrite an existing memo.
5. **Completion:** only after all files and the memo are revalidated, write a separate `*.complete` marker. The read-only inspector reports `VerifiedCompleted` only if all receipts and actual NTFS files agree. It reports `ExplicitResumeAvailable` only for a valid, contiguous receipt prefix with all later destinations and report absent; other states are `ManualReview`.
6. **No auto-cleanup:** if a process dies after output Flush(true) but **before** receipt durability, the orphaned generated output is left untouched and manually reviewable. If a source, a receipt, or a destination was replaced/edited (including identical SHA but unexpected file identity), resume is prohibited. Unknown `*.tmp` or extra journal filenames also block resume. No completion is inferred from a matching SHA alone.

## Executed Windows evidence

The Part35 CI launches **three separate generated-only child processes that actually kill themselves** at different Core checkpoints:

- **After first file receipt:** the first output has a durable receipt and stable NTFS ID. Explicit restart preserves that ID, skips the output, copies the remaining generated entries, creates the outermost collision memo, then writes completed evidence.
- **After file flush, before receipt:** output has matching bytes but no completed receipt. Inspector shows `ManualReview`; explicit retry refuses, does not delete the orphan or touch the original destination.
- **After report receipt, before final marker:** all outputs and the collision memo are verified but marker is absent. Explicit restart creates only the final marker, preserving the memo's original NTFS ID and bytes.

Other adversarial generated-only tests: source modified after intent; foreign output added after intent; completed file receipt corrupted; unrecognized incomplete `*.tmp` journal. Every scenario asserts existing original file ID/SHA, input bytes unless deliberately edited, and no unsafe overwrite.

A strict machine-readable `diagnostics/r217-part35-durable-collision-replay.json` report and PR-head Windows CI are required. All earlier Part1–34, WPF, win-x64 publish and low-space/VHDX tests remain required.

## Clear remaining risks

This protocol demonstrates **restart classification at synthetic process-kill checkpoints**, not true atomic multi-file filesystem transactions or physical power-cut durability. An unreceipted but intact output requires **manual review**, by design, until a separate safe repair/approval policy is specified. The immutable file receipts are **not cryptographically authenticated against a malicious administrator editing both the journal and output**. Concurrent path changes between final validation and OS calls, directory handle rebinding, file permissions, SACL/ADS/EFS, actual disk power loss and source-media deletion semantics are still unresolved.

The shipped executable does **not** contain the Part35 mutation methods (`#if CHAPPY_TESTING`), does not offer archive extraction, and cannot perform production hardlink consolidation, original replacement or independent Undo. No real user files were changed, and **0 B end-user capacity was reclaimed**. A historical 69.4% source coverage estimate is not product readiness.

Do not merge until PR-head CI is green and independently verify postmerge main CI.
