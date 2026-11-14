# R2.15 — Reconciled JPG/PNG/MP4 capacity report without double counting

## Personal-media workflow

The existing Windows UI **「PNG/JPG統合容量レポート／CSV」** now displays a **format-by-format** conservative scenario when the user has already scanned the same source folder for exact duplicates.

**Required sequence:** First run the read-only exact duplicate scan. Then create or resume JPG and/or PNG *independent preview candidates* in a separate output folder. Finally open the combined report and select that candidate folder. The report re-audits the candidate journals and then revalidates duplicate file bytes before displaying combined figures.

Users see:
- MP4 — exact-byte duplicate savings only; **no video re-encoding**.
- JPG/JPEG — duplicate savings, independent image-candidate savings, overlap deducted, nonoverlapping estimate.
- PNG — same four figures.
- Mixed-extension groups and other types — marked separately.
- Total theoretical logical capacity reduction and **actual recovered space = 0 B**.
- Number of verified and excluded duplicate groups; progress bar and cancellation during the revalidation scan.

When exporting CSV after a matching duplicate scan, the save dialog offers:
1. **形式別の非重複見込CSV** — five rows of per-format verified groups, refused groups, preview savings, duplicate savings, overlap adjustment, combined hypothetical bytes, actual recovered bytes (zero).
2. **候補詳細CSV** — the existing JPG/PNG preview journal detail.

The CSV cannot overwrite existing files or be saved into the source/candidate folder trees or reparse-point directories.

## Accounting

For every fully reverified exact-duplicate group, let the file size be `S` and the file count be `N`.

- Potential duplicate saving = `S * (N - 1)`.
- When a group has JPG/PNG candidate savings on several names, *only one* of those independent candidate effects should be counted alongside the duplicate saving.
- For this hypothetical scenario, the excluded overlap is `sum(candidate savings per member) - max(candidate saving per member)`.
- Combined hypothetical = unique candidate savings + verified duplicate savings - excluded overlap.

The group is excluded if any member moved, changed its metadata/identity/hash/bytes, acquired an additional hardlink name, falls outside the scan root, or fails the existing metadata guard. A group is processed only once; CSV preview rows are deduplicated by path and rejected when the media extension, format, status or byte reduction is inconsistent.

In mixed-extension groups, the overlap is attributed to **mixed-extension** type, while previews are attributed to their individual JPG/PNG type. Therefore, individual format rows are accounting buckets, not an independent realizable optimization strategy; **the aggregate remains the meaningful scenario**. Large-file hash and byte verification costs time and are cancellable; the new progress display gives the user feedback.

## Safety / limitations

The combined number is a **hypothetical maximum scenario**, not a guaranteed or measured saving. Physical file allocation, NTFS cluster rounding, codec metadata, output artifacts and retained backups may alter actual recovered space. Candidate compression of an exact duplicate group need not be achievable by applying the currently recorded candidate to one name after deduplication; this is **not** a plan to execute that operation.

This feature never modifies JPG, PNG or MP4 originals. Production deduplication, replacement, or independent-file undo remain **disabled**. Additional files created by preview operations consume rather than free local disk space.

Only a Windows synthetic test suite is used: JPG triple duplicates, PNG pair, MP4 pair, singleton previews, stale or rewritten MP4, added hardlinks, per-type CSV, cancellation, illegal CSV destinations, and existing R2.8–R2.12 NTFS safety checks. No real personal media were tested.

## Next priority

Profile large scans and candidate throughput on generated datasets before making any performance claims. Investigate progress and reuse of trusted scan output without relaxing identity, byte-exact or named-stream safety checks. Avoid advanced cross-user ACL research unless it becomes relevant to the user's single-account media library.
