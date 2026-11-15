# R2.17 part 13 — read-only confirmation-time output recheck

## Priority and behavior

The combined PNG/JPG batch already inspects its output folder and saved journals before presenting an explicit Yes/No confirmation. That modal dialog may remain open while an unrelated Windows process adds files or a valid format plan disappears/appears. Part 13 adds a **second read-only `CombinedBatchResumeInspector.Inspect`** after Yes and before either preview engine begins. The recheck compares the value of the PNG/JPEG plan presence and planned entry counts to what the user approved; any inspection failure or changed summary refuses the run.

- It never creates, deletes, repairs, or overwrites candidate files, journals, user input, or tools.
- Cancellation is checked before and after the recheck; the WPF handler retains the existing busy/Stop transitions.
- The existing strict per-engine validation still happens when each candidate batch runs; there is no additional authorization for source-file replacement or hardlink consolidation.
- The user's previously confirmed output folder is **not silently substituted** if a plan appears/disappears while the dialog is open.

## Synthetic regression evidence

The existing disposable NTFS combined-resume suite now additionally checks:

1. Empty output and PNG-only and two-format valid plans all pass unchanged reread.
2. An unfamiliar output file created *after the displayed inspection* is refused; its bytes stay unchanged.
3. A JPEG plan created after a PNG-only prompt is refused even if the current folder is otherwise valid.
4. A JPEG plan removed after a two-format prompt is refused; its test-owned source files and saved plan are preserved.

The new JSON proof keys are `postConfirmationReadOnlyRecheckVerified`, `postConfirmationForeignFileRefused`, `newPlanAfterConsentRefused`, and `removedPlanAfterConsentRefused`. Existing Windows C# smoke, real WPF routed UI, source-pinned jpegtran, synthetic NTFS safety/fault checks and win-x64 build **must** remain passing. CI outcome is not inferred from this design note.

## Limits and release safety

This is an **additional fail-closed inspection**, not a transactional filesystem snapshot. An outside writer can still change source or output immediately after the second inspection, and different journal contents with the same plan counts are not necessarily detected by summary comparison alone. The format engines remain responsible for independent validation and must be reviewed separately for adversarial concurrency. Automated physical clicks/native folder dialogs and visible desktop behavior are not covered.

Only generated synthetic files are test inputs. Production original replacement, hardlink merge and independent Undo stay disabled. Actual user capacity recovered **0 B**. Do not call this feature completed until PR-head and postmerge-main Windows CI both pass.
