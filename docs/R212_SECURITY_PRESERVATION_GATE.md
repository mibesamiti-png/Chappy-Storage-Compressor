# R2.12 — Synthetic NTFS owner/DACL preservation and exact rollback

## Scope

An isolated **Windows GitHub Actions** .NET 10 test in `bootstrap/NtfsSecurityPreservationProbe.cs`. The experiment targets **generated disposable NTFS files only** under a UUID-named, sentinel-checked `RUNNER_TEMP` folder. It accepts no user paths and cannot run outside Windows GitHub Actions. It is **not integrated into the WPF product**.

The previous R2.11 work detected permission changes and rejected unsafe operations. R2.12 adds a **positive, single-fixture experiment** for preservation through Windows `File.Replace` and subsequent byte-exact rollback.

## Procedure

1. Create a synthetic 512 KiB original and a different 384 KiB staged replacement.
2. Add a read grant to the generated original's DACL for the built-in Authenticated Users SID. Confirm its owner+DACL descriptor differs from the separate stage.
3. Copy the source owner and DACL to the independent stage with `GetNamedSecurityInfoW` / `SetNamedSecurityInfoW`. Refuse if the copied owner+DACL SDDL does not match the original. Apply the original creation/write timestamps.
4. Capture SHA-256, file identity, hardlink count, owner+DACL SDDL, timestamps, attributes, stream inventory, and file size for each source.
5. `File.Replace(stage, original, backup)` only after verifying both synthetic paths a second time. Check the new file holds staged bytes, owner+DACL matches the original and an independent backup holds original bytes.
6. Prepare another independent stage from that backup, copy the security descriptor and timestamps, perform a second `File.Replace`, and check original SHA-256 and owner+DACL are restored.
7. Run five separate negative preflight fixtures: staged DACL changed, target DACL changed, unexpected ADS, same-SHA different stage file ID, and stage gaining another hardlink.

The machine-readable report `diagnostics/r212-security-preservation.json` records positive post-commit/rollback results and negative refusal counts **without publishing SDDL or user SIDs**.

## Critical limitations

- This is **not production owner/ACL preservation**. It only tests synthetic ordinary files under the same GitHub Windows user token; it cannot guarantee owner replacement across arbitrary user accounts or privileges.
- Windows `File.Replace` may merge file metadata and has documented failure states. If any mismatch occurs, CI **must fail**, and the code should be fixed after examining the results rather than silently accepting the mismatch.
- Named streams, SACL, group SID, EFS, reparse points, NTFS compression, inherited ACLs across arbitrary directories, access-denied paths and power-loss consistency remain unproven.
- Separate file identity, no extra hardlinks, SHA-256 exactness and metadata checks do not eliminate TOCTOU races for real-world paths.
- This test exercises Windows file replacement only on expendable test data. **Production deduplication, original-media replacement and independent undo remain disabled.**
- R2.12 does not set up a VHDX. Dedicated-volume measurement remains an open separate gate.

## References

- [Microsoft ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew): original DACL and attributes may be merged; ignored ACL merge errors can lose metadata.
- [Microsoft SetNamedSecurityInfoW](https://learn.microsoft.com/en-us/windows/win32/api/aclapi/nf-aclapi-setnamedsecurityinfow): explicit security information mutation requires appropriate access rights.
- [R3 production undo issue #6](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/6) and [filesystem safety issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2).
