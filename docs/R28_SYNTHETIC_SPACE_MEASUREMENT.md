# R2.8 — Synthetic NTFS allocated/free space measurement

## Goal

Distinguish logical size savings, NTFS physical file allocation savings, and noisy observed volume free-space deltas. The probe is **only for GitHub Actions disposable Windows TEMP files** and is not an end-user feature.

## New test

`bootstrap/NtfsSpaceMeasurementProbe.csproj` runs on Windows / .NET 10. No command-line arguments or external paths are accepted. Before running, it validates `GITHUB_ACTIONS=true`, requires `RUNNER_TEMP` to be an existing non-reparse directory on NTFS, and creates its own UUID-named sandbox.

- Generates two independent SHA-256-identical 16 MiB binary files using system randomness; verifies distinct NTFS file IDs.
- Replaces only the newly generated duplicate with a hardlink to its sibling; verifies identical contents, a shared NTFS file ID and >=2 link names.
- Measures unique-file-ID allocation using Win32 `GetCompressedFileSizeW`. This avoids incorrectly counting the same hardlinked content twice.
- Measures `DriveInfo.AvailableFreeSpace` around the operation. These deltas are noisy and **not acceptance conditions**, because shared CI runners and NTFS metadata can move.
- Creates a generated RGBA PNG with an uncompressed IDAT and independently recompresses the same filtered scanline bytes. Verifies all non-IDAT bytes (including the tEXt metadata chunk) and inflated filtered scanlines are identical.
- Makes a byte-exact, independent backup before synthetic PNG replacement. Injects a synthetic failure after backup before commit and after commit; verifies original-preservation and byte-exact rollback.
- Writes machine-readable `diagnostics/r28-ntfs-space.json`, archived by GitHub Actions.

## Interpretation

An original PNG, its candidate and independent backup may simultaneously consume **more total physical disk space than the original alone**. Recovery never counts as real while a same-volume independent backup is retained. Hardlinks preserve storage but **do not preserve independent editing behavior**; modifying one name modifies all.

The reported `uniqueFileIdAllocatedSavingsBytes` is a **synthetic NTFS file allocation estimate**, not an end-user disk-space savings result. The `observedFreeSpaceDeltaBytes` is a point-in-time measurement, not a guaranteed user-visible difference. The probe never alters existing user files.

## Unfinished gates

- Actual crash/power-off during `File.Replace`, not an injected exception.
- ACL, ADS, reparse-point and concurrent-writer fault matrix.
- Dedicated volume (VHDX) measurement to eliminate unrelated CI noise.
- App-level PNG/JPG candidate acceptance, metadata consistency and independent restore.
- Production original replacement and hardlink dedupe remain DISABLED.

The broader test plan is [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2). No increase to source-coverage completion % is claimed before test evidence.
