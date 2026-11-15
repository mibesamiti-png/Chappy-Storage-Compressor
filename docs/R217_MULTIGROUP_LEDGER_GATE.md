# R2.17 Part 26 — multiple independent completed ledgers and a pre-mutation multi-alias refusal

## The issue closed by this change

Part24/25 persisted a completed record for **one original alias** with its exact NTFS master identity and hardlink count. For a duplicate group of three or more filenames, the second completed merge changes the same master's link count and invalidates the first immutable record. The previous synthetic opt-in path could start that additional merge and only discover the incompatible history after creating the hardlink/pending journal. This is not safe evidence for a general N-alias Undo.

Part26 deliberately **does not claim arbitrary multi-alias support**. When `CHAPPY_TESTING` and `testOnlyRecordCompletedMerges:true` are used, `DedupeEngine.Run` skips groups with more than two filenames **before** a master link, pending recovery record, backup rename, or original alias mutation is attempted. It reports a skipped group through normal progress. The old Part23 test-only, no-completed-ledger demonstration remains unchanged. Production dedupe is still compile-time disabled.

## New concrete multi-group proof

`tests/R217MultiGroupLedgerTests.cs` creates only synthetic data under the previously established sentinel-verified `ChappyStorageSmoke_` TEMP NTFS root:

- Three independent two-file groups, one each for a generated decodable PNG, a generated decodable JPEG, and synthetic MP4-like bytes. All copies have independent initial NTFS identities and verified SHA-256, attributes, timestamps, owner+DACL.
- A fourth **three-file** synthetic MP4-like group. Despite matching bytes, the completed-ledger opt-in must leave all three original identities and hashes unchanged. It must not create partial history or any pending journal.
- One call to the actual `Scanner.Run` and `DedupeEngine.Run(...testOnlyRecordCompletedMerges:true)` must successfully merge only the three supported pairs and persist three independently re-openable completed records. All three stored master link counts are 3.
- A new read-only ledger inspection after the Core run (without reusing original scan snapshots) must verify 3 completed operations and exact logical savings.
- A deliberately added **test-owned extra hardlink** to one master must downgrade only that group's record to `ManualReview`; independent Undo authorization for that operation must throw while the other two completed records remain verified and usable.
- Two unaffected groups are actually detached by the real `IndependentUndoEngine.Restore`, using original metadata reconstructed from their durable records, then marked restored. The generated extra link is explicitly identity/hash-verified and removed **by the test only**; the formerly blocked third group is then restored independently.
- All three Undo results must have independent NTFS identities, `LinkCount=1`, exact original source bytes, creation/write timestamps, attributes and owner+DACL, plus durable individual Undo evidence. A completed record already marked restored cannot authorize a second Undo. The unsupported triple must remain untouched throughout.

The core runner produces `diagnostics/r217-multigroup-ledger.json`. Windows CI has a dedicated strict schema/boolean/count gate. All previously mandatory Parts 1–25 official JPEG/PNG, WPF, NTFS ACL/ADS, real worker-kill and win-x64 build checks remain in place.

## Boundaries and next engineering gate

This validates **three disjoint two-file groups**, not a single group with three or more merged aliases. A future N-alias transaction must have a group-level authoritative manifest recording every expected link and original per-alias metadata, crash/restart transitions when new aliases are added or independently restored, and exact link-count reconciliation **without accepting unexplained external names**. It must handle interruption mid-batch and verify each alias's identity before and after mutation. Neither an optimistic dynamic link-count comparison nor silently editing earlier immutable evidence is an acceptable substitute.

The tests are in-process integrations plus historical Part25 independently killed workers. Part26 itself does **not** add a multi-group actual-process-kill or true abrupt power-loss VHDX/cache-durability test. There is no arbitrary ACL/SACL/ADS/EFS support, no production approval or user-data mutation, and no real end-user capacity reclaimed (0 B). The historical source coverage estimate (69.4%) remains unchanged and is **not** release readiness. This PR is eligible for merge only after final PR-head Windows CI succeeds; postmerge main CI is separate.
