# R2.17 Part 18 — deterministic inner PNG cancellation / native JPEG process cleanup

## Change and what the new tests actually prove

Part 17's real routed Stop events occurred after a fully written candidate and durable journal entry. Part 18 tests **two earlier cancellation boundaries** inside the actual candidate engines using existing generated-only Windows CI fixture data, official source-pinned `jpegtran.exe`, and the isolated Core smoke runner.

For PNG, the `CHAPPY_TESTING`-only optional callback is invoked after `PngLosslessOptimizer.CopyInflated` has actually copied a decompressed scanline block into the candidate's ZLib compression stream. It cancels the `CancellationTokenSource` before the next normal unit of work. The optimizer's existing `finally` must delete the unverified candidate, with no output files left, and original bytes/timestamp untouched. A second normal `CreatePreview` invocation **without any test callback** must produce one smaller validated candidate.

For JPEG, the `CHAPPY_TESTING`-only PID callback is now inside the native `Process.Start` / `WaitForExitAsync` cancellation cleanup guard (previously it ran outside). The callback cancels the token synchronously immediately after a genuinely created official jpegtran child PID is observed. Even if cancellation occurs before `WaitForExitAsync` is entered, the engine must kill a still-running child process tree, wait for exit, propagate `OperationCanceledException`, and delete the temporary candidate. The test checks the observed native PID is no longer live, output contains no files, and source SHA-256/mtime remains unchanged. Retrying with the same official jpegtran without a cancellation callback must generate one smaller verified JPEG whose decoded pixels and JPEG marker audit match the source.

The `CHAPPY_TESTING` symbol is defined only by the standalone Core smoke-test project that recompiles Core sources independently. **Neither test hook exists in the production WPF assembly.** The default engine behavior is unchanged except for a cancellation check immediately after a real PNG compressed block is copied and a stronger JPEG child cleanup try boundary.

## Strict Windows CI evidence

The `r217-priority-images-memory.json` record must report `part18PngCancelAfterInflatedWrite=true`, `part18PngBytesInflatedBeforeCancel>0`, `part18PngIncompleteCandidateDiscarded=true`, `part18PngFreshRetrySucceeded=true`, `part18JpegCancelAfterNativeStart=true`, `part18NativeChildPidObserved=true`, `part18NativeChildReaped=true`, `part18JpegIncompleteCandidateDiscarded=true`, and `part18JpegFreshRetryPixelsAndMarkersMatch=true`, plus explicit `part18AbruptAppProcessKillVerified=false`. The entire prior synthetic NTFS security/kill/recovery suite, complete and partial WPF real-engine resume, source-pinned libjpeg-turbo, JPEG metadata/color/Exif and win-x64 build/publish remain required.

## Strict limits

A cancellation callback invoked immediately after `Process.Start` **does not prove that the native encoder had already processed any image bytes**. It proves the child existed and was cleaned up, even when cancellation lands before its awaited completion. PNG proves at least one real decompressed block entered compression, not a power cut or mid-sector disk write. Both tests exercise graceful cooperative cancellation, **not SIGKILL / TerminateProcess of the application itself**. An unexpectedly crashed process can leave an orphaned, otherwise correctly named temporary candidate; such crash/restart scenarios still require dedicated disposable-process tests. No physical GUI interaction is asserted.

No actual user media, no original replacement, no production hardlink merge or independent Undo, **no actual recovered capacity (0 B)**. Historical 69.4% estimate is not product readiness. Merge only after final PR-head Windows CI succeeds; independent postmerge main push CI remains an explicit separately observable gate.
