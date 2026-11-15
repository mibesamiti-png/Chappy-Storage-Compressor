# R2.17 Part 21 — source inventory approval / native child after managed parent death

## Goal and correctness boundaries

Part 20 killed the native `jpegtran.exe` directly, and separately killed a managed Core worker *after* native encoding had already completed. Part 21 closes a different and important gap: **the combined WPF Yes dialog previously approved the output plan but did not compare the displayed source inventory again just before starting the first preview engine**. Part 21 also observes what happens when the managed test process itself dies while its official native jpegtran child has been observed alive.

## Input Yes recheck

`PersonalMediaBatchPreflight.Inspect` now returns a sorted SHA-256 digest of the **eligible PNG/JPG source path/size/mtime/attributes metadata entries**. No image content is read and no source file is modified to construct the digest. `VerifyAfterConfirmation` runs the same read-only scan after Yes and requires identical PNG/JPG counts, source bytes, skipped-special and scan-error counts, and that fingerprint. Normal fluctuations in `OutputAvailableBytes` do not invalidate consent. The WPF combined button performs this immediately after its existing `CombinedBatchResumeInspector.VerifyAfterConfirmation`, before creating a plan, opening preview files or invoking jpegtran.

The synthetic isolated suite checks unchanged scans; a deliberately changed free-space field; a new same-sized PNG; a rename with the same counts and total bytes; and a changed UTC modified timestamp, with exact source byte restoration and no output mutation. A real WPF routed `UnifiedBatchButton` Yes callback injects one disposable PNG into the input folder and requires a visible WPF handler failure with no plan or actual codec run.

This is **not** a full input-byte snapshot. If an adversary changes content while preserving path, bytes, length, mtime and metadata, the approval snapshot alone may not detect that. The PNG/JPEG preview engines independently validate input source identity, hashes, candidate decoded pixels and protected markers; the exact approval-to-encode race is not atomic.

## Native subprocess continuing after its managed parent dies

The standalone Core test project adds a strictly guarded new child process mode. It creates a 5120×3072 synthetic WIC JPEG (never a personal photo or video), starts the official source-pinned SIMD jpegtran through the real JPEG batch engine, and captures its **actual native PID and start time** immediately after `Process.Start` while `HasExited=false`. It writes the checkpoint outside the output tree, then calls `Process.GetCurrentProcess().Kill()` on its **own managed worker**, without cancellation and without cleanup.

The independent parent test waits for the worker to die, resolves the native PID only if creation time matches, and observes the exact native child until exit. The native child may continue independently on Windows; a bounded emergency kill is permitted only if PID, start time and executable path all match the official tool, and is recorded as a test failure. A candidate may or may not have been created by the time the native tool exits. The acceptance test requires the saved plan, absence of an item journal, unchanged synthetic JPEG SHA-256/mtime, and at most one leftover. If a leftover exists, **both** the read-only combined inspector and direct JPEG queue must refuse it byte-for-byte; the test alone may explicitly delete that exact disposable orphan. If absent, the saved plan must still be safe to resume without deleting anything. In both cases a real official-jpegtran retry must generate one smaller candidate whose protected marker audit and WIC RGB pixels are identical; the next retry must be idempotent.

The native check proves the encoder process was **alive at the in-process checkpoint**, NOT the number of bytes encoded at that instant, nor whether the child remained alive after the parent was killed (reported opportunistically). The kill is of the guarded Core smoke worker, not a production GUI process, and is not a hardware power cut.

## CI and safety

Windows CI requires distinct Part21 source confirmation fields from `r217-safe-preview-resume.json`, a real WPF Yes/late-source refusal from `r217-wpf-routed-click.json`, and `r217-native-parent-death.json` with exact official source commit, process identity, bounded native child exit, validated failure handling and pixel-/marker-equivalent retry, plus explicit **no** real user media, real disk recovery, or production original replacement/hardlink merging. All existing Part 1–20 NTFS, official codec, WPF, and win-x64 publish tests remain mandatory.

No automatic deletion of orphan candidate content. Production deduplication, original replacement and independent-file Undo remain **disabled**. Actual user disk capacity recovered: **0 B**. Historic 69.4% is not release readiness. A green PR-head run is necessary but does not independently prove postmerge-main push CI was green.
