# R2.17 Part 23 — duplicate-first representative previews + integrated independent Undo

## What is actually new?

Part 22 focused on candidate-start races. Part 23 returns to the user's actual goal: **deduplicate before needlessly compressing exact copies, with a safe way to restore independent file behavior afterward**.

Two linked deliverables are intentionally separated:

1. **Normal WPF, usable now:** `DedupeFirstWorkflowPlan.Inspect` revalidates exact groups from the real `Scanner.Run` (root restrictions, metadata snapshots, NTFS identities, hashes, byte comparisons and unresolved journal check) and calculates one representative PNG/JPEG preview candidate per eligible image duplicate group, aliases potentially avoided, hypothetical logical saving, and future additional storage needed to undo. The new button **「重複優先・独立Undo事前計画（読み取り専用）」** calls this real Core planner and shows the result. It never calls any mutation API. The nearby Merge button remains disabled even after successful planning. A genuine routed WPF Click test checks this behavior and unchanged SHA-256/file identities on 2×32MiB generated MP4s.
2. **Generated-only Windows Core integration:** `R217IntegratedDedupeUndoTests` creates **seven independent generated JPG/PNG/MP4 files** in three fully verified duplicate groups. It calls actual `Scanner.Run`, read-only `DedupeFirstWorkflowPlan.Inspect`, then the existing `DedupeEngine.Run` (**only permitted in Core builds with `CHAPPY_TESTING`**). This creates four real NTFS hardlink aliases and master links with verified content and file identities. Real `PngLosslessOptimizer.CreatePreview` and official-source-pinned SIMD `jpegtran.exe` optimize one actual image representative **per duplicate group**, not a candidate for every alias. The separate Core `IndependentUndoEngine.Restore` (**test-only mutation**) creates fresh individual file identities by copying to a separate staged file and `File.Replace` into each known synthetic alias. Then the experiment explicitly removes only its verified test-owned master links and checks every original byte, creation/modified timestamp, normal attributes, owner+DACL descriptor and `LinkCount=1`. Finally the test writes to a detached alias and confirms the former master did not change.

## Fail-closed checks

- If any file was renamed, retimestamped, swapped, relinked or edited after the scan, the read-only planner excludes it instead of trusting its earlier estimated bytes.
- Unexpected third-party hardlink aliases block test-only Undo **before** stage/evidence creation.
- The test-only Undo refuses an altered staged copy before cutover, preserves a disk-flushed JSON intent and the suspicious staged file for manual review; only the exact disposable fixture is explicitly cleaned by the test.
- Test-only file mutation requires an exact `ChappyStorageSmoke_` directory beneath OS temporary storage with a generated-only sentinel. Production `IndependentUndoEngine.Restore` **always throws `NotSupportedException`**.
- The production `DedupeEngine.Run` is already hard-disabled, and the WPF Merge button never becomes enabled. No general user-file modification is authorized by Part 23.

## What is still missing before an actual usable dedupe-and-Undo release?

**The key architectural gap is durable completed-operation history.** Current `DedupeEngine` journals describe only a *pending* operation and delete the record once complete. After the application restarts, it cannot safely reconstruct which aliases were created, which per-name timestamps/attributes were present, how ACLs differed, or whether a third-party alias appeared. The new integrated test deliberately passes a pre-merge `FileSnapshot` directly and retains **test-only Undo intent evidence**; this is NOT a production completion ledger or a durable crash/replay implementation. On Windows, `File.Replace` can retain timestamp/ordinary-attribute metadata from its replaced alias rather than the stage, so the test-only engine verifies independent identity and exact bytes **first**, and if Windows `File.Replace` merged or rewrote owner/group/DACL, the guarded synthetic-only path applies the exact pre-merge owner/group/DACL security descriptor to **only** the now-independent alias using `SetFileSecurityW`, re-reads and byte-compares it, then restores timestamps/ordinary attributes and repeats every check. Security descriptor restoration can fail due privileges/inheritance and must never be claimed to work on arbitrary accounts. This per-alias metadata normalization is **not atomic**: interruption in that gap still requires a future durable production replay design.

Remaining P1 release gates: completed-merge ledger with guaranteed disk durability and replay, independent Undo recovery through process death at every stage, precise byte and security metadata on real optional consented data, external-link/writer and free-space refusal, isolated VHDX allocation accounting, actual production WPF interaction and separate final user consent. For many real folders, path/size/mtime rechecks are not atomic snapshots and cannot prove that no third party changes a file between checks.

**Safety status:** no user original replacement, no production hardlink consolidation, no production Undo, no real media read by automated tests, **0 B actual user capacity freed**. Historical estimated source implementation 69.4% unchanged and not a release readiness score.

## Strict CI evidence

`diagnostics/r217-dedupe-undo-integration.json` must report 3 real scanner duplicate groups, 4 aliases, actual executed Core dedupe, one original PNG/one official JPEG representative candidate, real independent Core Undo for all 4 aliases, preserved source bytes/times/attributes/owner+DACL, a verified independent write, negative injected extra-link and stage-tamper refusals, and explicit FALSE values for all production mutations/production crash guarantees.

`diagnostics/r217-wpf-routed-click.json` now also proves a real routed read-only planning click on two generated 32MiB MP4s, explicit warning that recovery is not implemented and actual freed capacity remains zero, with normal Merge still locked and no newly created `.ChappyDedupeStore`.

All prior independent official codec, NTFS ACL/ADS/hardlink, Core crash probes, WPF routed Start/Stop/PNG/JPEG resume and x64 publish gates remain mandatory.
