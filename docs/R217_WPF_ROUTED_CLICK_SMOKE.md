# R2.17 part 5 — routed WPF Start/Stop UI smoke test

## Scope and intention

R2.17 part 3 added in-file hashing byte feedback and cancellation token integration; part 4 improved safety and confirmation for combined JPEG/PNG candidate-batch resume. Those had compiled WPF UI but **did not exercise the application's real XAML Click event handlers** in CI.

The separate tests/ChappyStorageCompressor.UiSmokeTests.csproj now loads the **actual application's WPF XAML** and calls genuine routed ButtonBase.ClickEvent events for ScanButton and StopButton. It uses an STA thread and a WPF dispatcher synchronization context so the existing async void Scan_Click, Progress<T>, and Stop_Click paths run in their normal synchronization model. This is **not physical mouse automation** or a real user-desktop visual inspection; no interactive desktop window is shown.

## Synthetic test procedure

The harness creates a unique disposable test root on the Windows CI runner's NTFS disk and internally points the app's pending-operation journal to a separate test directory (via reflection used **only inside this test harness**, without changing production Journal API). It then:

1. Loads real application XAML and verifies that production MergeButton remains disabled.
2. Creates two separate **32 MiB generated MP4-named files** with exactly identical bytes and aligned timestamps.
3. Raises the real WPF ScanButton routed Click; verifies scanner busy state, indeterminate inventory bar and Stop enabled.
4. Raises the real WPF StopButton routed Click before the UI dispatcher is pumped; verifies immediate **中断要求中…** text, Stop disabled, and an actual canceled CancellationTokenSource.
5. Pumps the WPF dispatcher until the asynchronous scan completes cancellation; verifies UI returned to idle, no partial groups, no production Merge.
6. Raises ScanButton routed Click again; pumps until completion; verifies one exact duplicate group and 100% progress on the existing UI.
7. Uses independent framework SHA-256 comparisons to confirm both synthetic files remained byte-identical to their initial contents and remain two separate paths.
8. Writes machine-readable results to diagnostics/r217-wpf-routed-click.json and deletes the synthetic fixtures.

The test provides a bounded failure timeout and is run from RUN_DIAGNOSTICS.bat alongside existing 476+ C# safety assertions, without requiring real photographs or running jpegtran.exe.

## Limits

- **Verified only:** XAML initialization, actual WPF event registration and event handler invocation, dispatcher-based UI state transitions, cancellation token signaling, second scan final UI results and unchanged source SHA-256.
- **Not verified:** physically moving a mouse, visible desktop rendering, pixel-level screenshots or dialog selection, human-perceived UI responsiveness, or Stop clicked specifically during a single SHA-256 chunk. Existing part-3 synthetic tests separately inject cancellation at a guaranteed hash byte boundary.
- The test samples whether intermediate SHA progress text is seen by the dispatcher but intentionally makes no fragile timing claim about that sample.
- The test does not open external executable dialogs, nor does it perform JPEG candidate optimization; the latter remains a separate verified-tool gate.
- All production hardlink merges, source replacement, independent undo remain disabled. Actual saved disk capacity is **0 B**.

## Windows CI results

Pending verified workflow results. Do not mark the WPF Click test completed or claim physical desktop automation until it passes on Windows.
