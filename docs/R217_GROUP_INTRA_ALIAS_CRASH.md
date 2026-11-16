# R2.17 Part 28 — actual intra-alias Undo process deaths across independent group ledgers

## Problem and approach

Part27 proved that a generated four-name duplicate group could resume after a worker died **between** committed independent-alias Undo checkpoints. It deliberately did not handle a death *inside* an alias's `IndependentUndoEngine.Restore`: a newly copied stage, the `File.Replace` switch to a new NTFS identity, and final metadata normalization can all be interrupted before the group checkpoint is updated. After cutover, an uncheckpointed independent alias must **never** be blindly passed to `Restore` again.

This part makes the group's existing durable checkpoint also track the **single active alias** and its exact generated stage path. The actual test-gated Core Undo engine emits only CHAPPY_TESTING phase callbacks at `intent-written`, `stage-validated`, `post-cutover-before-metadata`, and `metadata-restored-before-ledger`. The group ledger flushes each phase *before* the test worker is killed; phase names are never trusted in isolation. This does not change production write permissions.

## Recovery classification

Read-only `CompletedGroupLedger.Inspect` reopens original per-name metadata and durable group checkpoint and compares:
- expected master NTFS ID and exact link count, computed as representative + store link + not-yet-detached aliases;
- SHA-256 and exact byte equality against the unmodified representative;
- all previously detached alias IDs and original per-name timestamps, attributes and owner+DACL;
- all not-yet-started shared aliases and the one active alias, including current stage identity/bytes/metadata when present.

Two strictly test-only explicit paths are possible:

1. **Shared alias:** Master ID and expected link count still match. An absent, or exactly validated test-owned stage may be retired (no foreign/mismatched stage deletion), the journal's current active phase is cleared, and the real Core Undo is attempted again with a fresh stage. An unknown or partially written stage is **ManualReview**, not automatically cleaned.
2. **Already-independent alias:** Master link count has decreased **exactly by one**, the replaced alias is on the same volume with link count 1 and exact original bytes, all other aliases are proven unchanged, and the known stage is absent. Only the independent alias's original owner/group/DACL, attributes and timestamps are normalized using the existing guarded test-only Core recovery method; its actual independent NTFS identity is then durably appended to the group's completed Undo checkpoint. The system must not repeat `File.Replace`.

A malformed stage, unknown external link, incomplete or duplicated checkpoint, unexpected alias identity, journal collision or changed source is refused without modifying unknown bytes. The existing Part25 single-pair completed-ledger crash recovery and Part27 between-alias proof remain mandatory.

## Windows evidence

The new `tests/R217GroupIntraAliasCrashTests.cs` runs **four separate genuinely killed managed processes** against uniquely generated NTFS data. Each scenario has two distinct group manifests: one four-file group selected for deliberate interruption and an independent three-file group that must remain verified throughout. At both pre/post-cutover stages the system checks read-only state before explicit recovery. The stage-validated case changes the generated stage and proves refusal/preservation before restoring the exact synthetic fixture; the post-cutover case injects an unexpected *test-owned* master link and proves refusal before explicit test cleanup. The interrupted group is then safely completed, and the second group is subsequently undone, with every source SHA-256, NTFS link identity, timestamps, attributes, owner+DACL and journal verified. Machine-readable `diagnostics/r217-group-intra-alias-crash.json` is a strict Windows CI gate.

## Nonclaims and safety boundary

The four forced managed-process deaths occur at deterministic callback points; this is **not** physical power loss, interrupted NTFS/cache flush guarantees, arbitrary concurrent writers, unlimited group sizes, arbitrary real-user owner/ACL/SACL/ADS/EFS, VHDX measured allocation or production opt-in consent. If a worker dies during a copy before a fully valid stage exists, that unknown partial evidence remains **ManualReview** rather than auto-cleanup. A single active alias per group is modeled, not parallel destructive Undo. Cross-group interactions are only **sequentially** exercised on disposable generated data, not a global crash-atomic transaction.

All test mutations require `CHAPPY_TESTING` and exact generated TEMP NTFS sentinel; normal WPF hardlink merging, original JPG/PNG replacement and independent Undo are still disabled. Real user storage reclaimed **0 B**. Historical 69.4% source coverage is not product readiness. Merge only after complete PR-head Windows CI and then independently verify postmerge main CI.
