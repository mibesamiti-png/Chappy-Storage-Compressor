# R2.17 part 12 — one-batch preview UX, weighted progress and safe notification rate limiting

## Why this was prioritized

The existing combined PNG/JPG preview user interface allocated **exactly half of its progress bar to PNG and half to JPG** regardless of the actual batch size. For a user with two PNGs and 8,000 JPEGs, completing two PNGs would incorrectly show 50% instead of approximately 0.025%. Both preview engines also reported progress for each item, which could enqueue one WPF dispatcher update per item of a 100,000-image resume. These notifications were merely visual, but they could cause avoidable interface work.

The R2.17 part 12 change is a **user-facing progress/UI fix**, not a JPEG/PNG codec improvement. It does not relax any existing validation or change any batch journal, original source, codec, hardlink or transaction.

## Combined preview workflow changes

- `UnifiedPreviewProgress` weights PNG and JPEG progress by approximate preflight file counts and actual batch-plan totals. It is monotonic even when actual plan count differs from preflight, clamps inconsistent updates, and handles zero- or single-format cases. After each engine finishes, the authoritative `Summary.Done/Total` determines the stage transition.
- `RateLimitedProgress<T>` rate-limits **informational UI notifications before dispatching to the WPF UI**, defaulting to one update every 125 ms while keeping the first update. PNG `Failed` and `Deferred`, and JPG `Failed`, `Deferred`, `NeedsReview` bypass throttling. It never alters the engine's journal, cancellation, output validation or summaries.
- The actual WPF UI refuses stale callbacks after cancellation or after another stage starts. Start, Stop and completed-status messages are maintained by the original handler and the engine's authoritative result. This also avoids queued item notifications overwriting the final Stop/status.
- Remember the last valid combined-output folder and official jpegtran location **only within the current application session**, as suggestions in the standard Windows file/folder picker. The user **still explicitly chooses both paths**, the exact reusable output is checked with `CombinedBatchResumeInspector`, and every run requires the same final explicit confirmation. Neither tool nor candidate generation auto-starts.
- The WPF title/hero/warning now describe **read-only duplicate detection and preview-only source-safe candidates** accurately; no longer imply that source file consolidation is currently available.

## Verification matrix

The C# safety tests verify file-weighted 2:8, 8:2, JPEG-only, PNG-only and empty inventories; underflow/overflow and preflight drift; terminal 100%; artificial PNG and JPG event floods; exact preservation of priority errors; zero-delay behavior and invalid configuration rejection.

The synthetic event-flood fixture reports **20,002 PNG notifications + 10,003 JPG notifications**. For deterministic test-only *one-day* minimum intervals, expected forwards are **three PNG** (first + Failed + Deferred) and **four JPG** (first + NeedsReview + Failed + Deferred), i.e., **7/30,005 informational/priority notifications passed on**. This proves the helper can suppress redundant work when inputs arrive faster than its timeout, **not** a measured production WPF latency, throughput/speedup percentage or a claim that only seven events occur during normal operation.

A distinct real-WPF XAML routed Click test on generated MP4-like bytes also asserts that combined, PNG and JPG candidate buttons become disabled while a Scan is active, then return to enabled after a genuine Stop/cancel and after a successful second Scan, with the Merge action disabled throughout. It does not automate the native file/folder dialogs, actual visual mouse clicks, screen rendering or combined preview wizard confirmation.

## Proven limitations and release safety

No physical HDD/SSD cold-cache control or codec speedup attribution. Artificial events do not simulate the entire filesystem workload. The projection uses preflight file counts, so unusually changed inventories may still make the indicator an approximation; authoritative counts always come from the batch summaries.

All test data is generated. The source-backed journal, input SHA-256, resume safety inspector and candidate-only operation remain unchanged. No user photos or videos touched. Production original replacement, hardlink merge and independent Undo **remain disabled**. Physical user disk storage recovered **0 B**.

## Verified Windows CI

Pending the final branch and merged-main Windows runs.
