# R2.17 Part 30 — read-only recovery evidence dashboard and fail-closed orphan handling

## Goal

Before enabling any real source-file mutation, the Recovery Wizard must **not equate an empty legacy pending journal with a clean application state**. Earlier experiments created three independent durable evidence families in addition to the original journal:

- `Journal` / `RecoveryService`: in-flight legacy hardlink operations and malformed pending records.
- `CompletedMergeLedger`: pair-level prepared/completed/restored records.
- `CompletedGroupLedger`: 3+ independent filenames, durable group manifests, staged/intra-alias Undo and per-alias checkpoints.
- `IndependentUndoCrashJournal`: single-alias explicit crash-phase history.

Part30 presents all four without granting any file-write permissions.

## Changes

**Core:** `RecoveryEvidenceOverview.Inspect` validates the selected target root and assembles all four existing inspectors. Every row identifies source, record/file reference, raw verification state, human explanation and whether the user needs to investigate. Exceptions from individual evidence families become **ManualReview** rows instead of silently hiding that source. Cancellation is propagated. Empty inspectable history means *no unresolved records found*, **not** production readiness: `ProductionMutationEnabled` and `Summary.ProductionMergeOrUndoAllowed` are constant false. This class contains **no recovery/mutation method**.

**Evidence hardening:** `CompletedGroupLedger.Inspect` previously skipped **all** `*.checkpoint.json` files, even when their manifest had disappeared. Now an orphaned or invalid-name checkpoint appears as `ManualReview`. Matched checkpoints are still validated through the original strict `ReadCheckpoint` code. There is no cleanup of untrusted evidence.

**WPF:** The existing Recovery Wizard now has two visibly separate tabs, one for the prior explicit legacy in-flight journal actions, and a second `統合・Undo証拠の横断チェック（読み取り専用）` tab with no mutation controls. The selected target folder is passed from the main window; if none/invalid is selected, the history is **未検証**. The new tab's selection and tab switch **always disable** the legacy Apply button. Checks run off the UI thread, with status and details readable on selection. The original Completed Ledger read-only button also displays multi-alias group counts, interrupted stages and manual-review evidence.

**Verification:** `tests/R217RecoveryEvidenceOverviewTests.cs` constructs generated three-name NTFS media, tests empty inspection without store creation, orphaned checkpoint, real test-only completed group, injected unknown hardlink, malformed two-file ledger, malformed interrupted Undo record, incomplete legacy pending journal, and completed-group independent Undo interruption/resume. It verifies read-only re-inspection never performs any restoration. The routed WPF smoke test constructs the **actual new RecoveryWindow XAML**, raises a real routed Loaded event (not a physical display or click), checks that the history tab does not enable Apply or create a store, and checks source SHA/NTFS identity. A strict Windows CI report is required for both tests.

## What remains unresolved

The current Recovery Wizard still contains **separately selected and explicitly confirmed** legacy transaction recovery actions, which predate this read-only history tab. The new history tab **never** authorizes or invokes those actions. The product's hardlink consolidation, original PNG/JPG replacement and group independent Undo remain disabled. This part does not prove physical power failure, volume flush ordering, concurrent hostile writers, arbitrary Windows ACL/SACL/EFS/ADS cases, or full real-user metadata recovery. It does not change the results of Part29's synthetic isolated VHDX experiment.

Real user capacity recovered: **0 B**. Historical 69.4% source estimate is not product readiness. A green PR-head Windows CI and a separate postmerge main CI are required.
