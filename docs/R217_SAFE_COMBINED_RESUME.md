# R2.17 part 4 — safe combined-preview resume selection

## Purpose

The existing one-click PNG+JPG candidate workflow already keeps PNG and JPEG plan/journal files in the chosen output directory. Previously, the UI did not clearly tell users whether the chosen output folder would **start a new plan** or **resume an older plan**. This change adds a **read-only, fail-closed resume preflight** before user confirmation and before the engines create or modify any output.

This is **candidate-batch** resume, not automatic resume of an interrupted whole-library SHA-256 scan.

## User experience

After choosing the input folder, explicitly chosen jpegtran.exe and a separate output folder, the combined-preview UI:

1. Inspects the output directory **without creating a lock, plan, or journal**.
2. Classifies the directory as **new empty**, **PNG-only resume**, **JPEG-only resume**, or **both plans resumed**. Existing plan file counts are displayed.
3. Rejects mismatched previous source/selected jpegtran identity, incomplete or corrupt progress logs, unexpected files, foreign preview filenames, symlinks/junctions, unusual attributes, and hardlinked work records.
4. Clearly labels the planned operation in the existing confirmation dialog. The user can cancel without running compression.
5. After user confirmation, the existing PNG and JPEG engines perform their own full validation and journal replay again; the preflight **never authorizes source mutation** or bypasses engine safety checks.

The existing Stop button now immediately displays **中断要求中…** and disables itself after issuing a cancellation request. This is a visual feedback improvement; the existing token-propagation and mid-hash cancellation path remain authoritative.

## Security / limitations

- The preflight is an **additional read-only gate**, not a proof that all files are forever safe. It checks the current snapshot; a race after the dialog is handled by each engine's independent checks.
- Output-folder contents are allowed only when they match a known PNG/JPEG plan, journal, lock, or preview folder. Unknown or foreign files are **preserved but block reuse**. Users can choose a fresh empty destination rather than deleting anything.
- Each known PNG/JPEG preview directory must contain only files with the app's generated GUID filename scheme. All existing candidates remain untouched.
- Up to 128 MiB is read per plan/journal; larger, ambiguous or incomplete histories fail closed and require manual review.
- The preflight checks paths, journal consistency and basic metadata. It does **not** execute jpegtran.exe, recompress JPG files, replace originals, or turn on deduplication. It is also not a substitute for the existing detailed candidate SHA and WIC validation.
- Some existing output folders with exported CSV reports, legacy file names, manually saved files, or repair artifacts may now be rejected by the combined workflow. That is intentional: review them manually and choose a separate new output directory. Nothing is automatically cleaned.
- Windows CI verifies controller/logic paths and compiles the WPF UI; actual desktop mouse clicking of the Stop button has **not** been automated in CI. Full manual GUI response testing remains open.
- Since only previews are created, actual disk free-space recovery is still **0 B**.

## Generated-only regression cases

The new Windows smoke suite uses a disposable synthetic root and verifies:

- Empty destination is classified as new without creating any files.
- A previously created PNG plan is recognized for resume while JPEG remains new.
- A PNG + JPEG pair of plans is classified as an explicit combined resume.
- The same output with a different input or jpegtran.exe is rejected.
- Foreign files in root and preview directory remain unchanged and cause rejection.
- Torn JSONL journals (PNG or JPEG) cause rejection without truncating the evidence.
- NTFS hardlinked control/plan files are rejected.
- A log without a plan causes rejection.
- Independent source SHA-256 and saved plan/journal bytes remain unchanged.

Machine-readable evidence: diagnostics/r217-safe-preview-resume.json. Existing 456+ C# assertions, earlier NTFS R2.8–R2.12 probes, WPF compilation, and win-x64 publish remain mandatory.

## Next

A real WPF desktop UI exercise should select an existing valid output, verify the explicit resume text, click Stop during a long scan, and test the UI remains responsive. Any future attempt to add automatic SHA scan checkpoint resume requires separate safety review (source identity, stale snapshots, and no cross-run trust of old hashes).

## Verified Windows CI — 2026-11-14

[Successful PR workflow #43602714495](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43602714495): **23/23 workflow steps succeeded**; **476/476 combined C# safety assertions** passed (previous baseline 456 plus 20 R2.17 part-4 checks). Existing R2.8–R2.12 independent NTFS fault/recovery/security probes, tracked WPF build and win-x64 publish also completed successfully.

The structured result `diagnostics/r217-safe-preview-resume.json` reports PASS for empty/new output, PNG-only and combined resume, input/tool mismatch refusals, foreign files at root and inside preview directory, both PNG/JPG torn journals, hardlinked plan refusals, and journal-without-plan refusals. It explicitly records no external executable invocation, no WPF desktop click automation, no user media touched, and zero production source mutation.

This verifies **preflight and regression correctness on generated NTFS inputs**, not graphical click responsiveness on a physical user's desktop. Full GUI tests and JPEG candidate-generation verification remain pending.
