# R2.8 verified Windows NTFS synthetic measurement

Verification: [Windows PR CI run #43487742467](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43487742467) for [PR #3](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/3).
Diagnostics artifact `chappy-windows-diagnostics`, path `diagnostics/r28-ntfs-space.json`. Measurements were obtained on a disposable GitHub Actions Windows NTFS runner; no user media was accessed.

## Synthetic duplicate test (real measured delta)

| Metric | Bytes | Interpretation |
|---|---:|---|
| Original file size, per name | 16,777,216 | Random generated data |
| Combined NTFS allocation, two distinct IDs | 33,554,432 | Before deduplication |
| Unique NTFS allocation, one shared ID | 16,777,216 | After hardlink merge |
| Unique-ID allocation saving | **16,777,216** | **16 MiB** |
| Measured increase in volume free space | **16,777,216** | **16 MiB observed on this run only** |
| NTFS cluster size | 4,096 | Windows runner filesystem |

The original and duplicate had distinct NTFS file IDs before consolidation, then matched after the hardlink operation. Both paths still read the exact same SHA-256 bytes. **The hardlink pair is not independently editable**. The free-space observation is not a stable benchmark: unrelated filesystem activity can change it.

## Generated PNG test

| Metric | Bytes | Interpretation |
|---|---:|---|
| Deliberately uncompressed generated RGBA PNG | 1,049,371 | Input |
| Independently recompressed PNG candidate | 3,596 | Output |
| Logical candidate saving | **1,045,775** | About 99.66% for this intentionally compressible test |
| Free space with candidate and original | 157,548,937,216 | Raw volume snapshot |
| Free space after independent same-volume backup | 157,547,880,448 | Backup consumes disk |
| Free space after candidate replacement | 157,548,933,120 | Backup remains |
| Free space after exact byte-for-byte rollback | 157,547,884,544 | Original and backup remain |

Inflated scanlines matched byte-for-byte before/after recompression; all non-IDAT PNG chunks including the tEXt metadata were also identical. The synthetic backup was a distinct NTFS file ID, not a hardlink. An injected stop before commit left the original untouched. An injected stop after commit was followed by a **SHA-256 exact rollback** from the backup.

**Do not credit this PNG as recovered free space:** an independent same-volume backup was intentionally retained, so total volume space remains consumed. A physical crash/power failure, metadata protection, and concurrent writers were not simulated here.

## Overall CI results

- WPF .NET 10 build: **PASS**, 0 errors / 2 intentionally retained CS0162 safety-lock warnings.
- Existing Windows C# synthetic safety suite: **359/359 PASS**.
- New R2.8 NTFS measurement and rollback probe: **PASS**.
- Windows win-x64 publish: **PASS**.
- Original replacement and hardlink consolidation in the end-user app: **DISABLED**.
- Real-world user-data capacity recovered: **not measured**; not 16 MiB or 1,045,775 bytes.

## Remaining gates — Issue #2

1. Use a dedicated VHDX instead of a shared runner volume for quiet, repeatable volume accounting.
2. Add failure-after-crash/power-loss tests beyond exception injection and journal replay.
3. Validate ACL, ADS, reparse points, simultaneous writers and filesystem attributes.
4. Exercise actual app candidate preparation and restoration under the same safety constraints, not a separately generated PNG.
5. Only after those checks, review whether opt-in production replacement can be designed. **Do not enable production mutation automatically.**
