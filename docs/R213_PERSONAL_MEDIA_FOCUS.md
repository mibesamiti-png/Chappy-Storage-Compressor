# R2.13 — Personal media first: JPG/PNG/MP4 exact-duplicate report

## Reprioritized scope

This project focuses on personal Windows HDD/SSD collections belonging to the current user. Primary tasks are lossless JPG/PNG candidate generation, byte-identical MP4/JPG/PNG duplicate discovery, reliable batch estimates, and later opt-in reversible disk recovery. Different Windows-account ownership, complex ACL inheritance, EFS/SACL and arbitrary named streams are **deferred**. Special/unknown files remain fail-closed rather than being silently modified.

## Implemented in the normal WPF application

After **「① 読み取り専用スキャン」**, click **「JPG / PNG / MP4別の重複容量レポート・CSV」**. The report is disabled until the scan finishes, and remains disabled during other operations.

- Counts safe candidate groups and size estimates separately for **MP4**, **JPG/JPEG**, **PNG**, mixed extensions and other files.
- Requires every eligible group to pass its existing metadata safety gates. The report rechecks file identity/metadata, SHA-256 and byte-exact group equality at report time.
- If a file changes after scanning, **the entire group is excluded** from the theoretical estimate and marked **再スキャン必要**.
- Preserves the user's explicit choice about whether differing timestamps are allowed. Default: differing timestamps block a group.
- Exports a group-by-file CSV **only on explicit user choice**, never inside the input scan tree or through reparse-point output directories. Existing output files cannot be overwritten.
- CSV shows the group saving **only once**, on its first file row, to avoid double counting when summing rows. Formula-leading filenames are escaped.
- Displays **logical potential savings only**. `ActualFreedBytes` is always **0**; no file deduplication, recompression, re-encoding, renaming, hardlinking or source replacement is performed.

**MP4:** this is *byte-identical duplicate detection*, not video recompression or quality-lossless re-encoding. File-type grouping is based on extension; a JPEG with a `.mp4` name would be categorized by its extension, so do not interpret the category as media-format validation.

## Windows CI synthetic acceptance

- Generate exact duplicates in four groups: MP4, PNG, JPG/JPEG and an MP4 pair with different modification times.
- Verify 3 default eligible groups, 1 blocked group, 9,216 B theoretical eligible savings, 0 B real disk recovery.
- Confirm per-format breakdown, group-level CSV accounting, no overwrite, input-folder export rejection and spreadsheet formula injection escaping.
- Modify an MP4 after scanning and confirm the stale group is excluded on recheck.
- Explicitly allow timestamp differences and confirm only the time-blocked group is added, not the altered MP4.
- Continue running the existing 359 Windows synthetic safety assertions, R2.8–R2.12 probes and WPF publish.

## What is still missing

These reports do **not yet recover storage**. The user-facing hardlink consolidation and source replacement controls remain disabled. Candidate generation for JPG/PNG still writes separate previews outside the source folder; the report never treats candidate-size savings as freed space. Planned next priorities: faster large-folder scanning, verified JPEG/PNG batch candidate throughput, realistic MP4 duplicate discovery, preview/no-overlap reporting, and only then a separately approved source-mutation release gate.

[GitHub progress](ROADMAP_STATUS.md) · [R3 production undo prerequisites](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/6).
