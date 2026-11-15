# R2.17 part 15 — real WPF combined preview routed Click, deterministic native-dialog answers

## Intent
Part 5 and Part 12's STA WPF harness covered the real **Scan/Stop** routed Click handlers, not the user's combined PNG+JPG preview button. It could not safely use native modal picker dialogs on an unattended GitHub runner. Part 15 keeps the same production Windows pickers/Yes-No message box as the defaults but moves those calls into four **private per-window delegates** (tool picker, output picker, confirmation and error display). A test uses reflection to install deterministic responses on its isolated test window only. This is a routed WPF control test, **not** native dialog or physical mouse automation.

## Grouped synthetic test matrix
The existing STA/WPF isolated test additionally:
1. Raises the actual `UnifiedBatchButton` routed `Click` while the *tool picker* is canceled; the folder picker, confirmation and engines never run.
2. Supplies a fake local jpegtran path but cancels the *output picker*. No confirmation or candidate operation occurs.
3. Supplies synthetic local paths, runs the actual read-only `CombinedBatchResumeInspector` and `PersonalMediaBatchPreflight`, then answers No to approval; verifies the displayed input counts and source-safe warning, restored controls, and no candidate/journal side effects.
4. Answers Yes but cancels the actual WPF handler's active `CancellationTokenSource` from the confirmation delegate, and verifies the normal `OperationCanceledException` UI path.
5. Creates a foreign file after the displayed approval and answers Yes. The real post-Yes inspector must refuse it, show the safety-error path (captured by an in-process delegate), and preserve the foreign bytes.
6. Repeats the real WPF `Click` with the same folders and answers No. Both pickers and explicit confirmation must be invoked again rather than silently starting a saved session.

The tool is a **synthetic text file with the jpegtran.exe name and is never executed**; both preview sources are disposable generated files. Original SHA-256s are unchanged, no candidate file or journal is created, and the Merge action remains disabled. Only the GUI's selection/consent/error adapters are simulated; real folder and file safety inspections run without bypass.

The new report keys are `combinedToolPickerCancelRefused`, `combinedOutputPickerCancelRefused`, `combinedNoConsentRefused`, `combinedCancellationDuringConsentRefused`, `combinedLateForeignFileRefused`, `combinedRetryRequiresNewConsent`, and `syntheticCombinedInputHashesUnchanged`. The strict Windows CI gate requires all seven and `actualNativeDialogAutomationVerified=false` while retaining older WPF Scan/Stop, synthetic NTFS fault/security, exact JPEG/PNG preview tests and win-x64 build/publish.

## Limits and safety
No real visible desktop, native picker keyboard/mouse automation, accessibility API interaction or physical GUI visual verification occurs in CI. The Yes->successful preview-engine path is not executed by this WPF test; real generated JPEG/PNG batch engines are already covered separately by Part 11. The adapter functions do not weaken the existing format engine, post-consent SHA-256 plan/journal comparison, NTFS special-file refusal or Windows cancellation logic. **No original file replacement, production hardlink merge, production Undo or actual user disk capacity recovery.** No user media tested.

To close the release gate, pass the final PR-head Windows CI and independently verify push-triggered main CI.
