# R2.17 Part 38 — read-only collision recovery safety checklist

## Purpose

The previous Part34–37 synthetic workflow can group same-name files under `同名ファイル/...`, create `同名ファイル一覧.txt` at the outermost output root, log exact per-file NTFS receipts before/after crash checkpoints, and show concrete ManualReview reasons in a production read-only WPF tab. Those receipts **only demonstrate destination evidence**, not the current condition of the source. We must **never equate “output SHA and NTFS file ID match” with permission to overwrite, delete, merge, or Undo**.

Part38 makes this boundary explicit with an **8-point source-versus-destination checklist**, produced entirely from the existing `CollisionEvidenceReview.Summary` in memory. The checklist performs no file IO at all, does not interpret `SourcePath` from a journal, never requires or generates a mutation lease, and has no write action. In the WPF `同名ファイル調査（読み取り専用）` tab it appears in the overall summary and on selecting a specific incident/file.

## Eight concrete checks

1. **Target scope:** only the selected root's immediate collision journal; descendant folders and other drives are uninspected.
2. **Persisted operation record:** absent, inconsistent, incomplete, or locally destination-verified.
3. **Output NTFS identity, SHA and owner/DACL:** missing, mismatched, or receipt-matched. Receipt-matched is *not* current-source proof.
4. **Outermost 同名ファイル一覧.txt and receipt:** pending, mismatched to the canonical saved collision plan, matching or not applicable.
5. **Unknown foreign evidence / orphan .tmp:** a foreign evidence record blocks the *entire* operation, including when a selected destination's own receipt still matches.
6. **Current originals, their original ACL and paths:** always *未検証*; the read-only viewer never opens or trusts untrusted journal SourcePath.
7. **Concurrency and actual power-loss durability:** *未検証*; a real process kill is not a hardware power cut and no atomic multi-file transaction is proven.
8. **Permission to mutate real data:** always *禁止*; all `CanAutomaticallyResume`, `CanDeleteOrOverwrite`, and `AuthorizesOriginalRestore` are hard-coded false.

The decision is deliberately one of:

- `要確認（証拠に不整合）` — unknown files, receipt mismatch, contradictory operation or missing authoritative record;
- `未完了（自動再開不可）` — valid intended operation but outputs or the outermost memo remain unwritten;
- `保存先証拠のみ一致（本番復元不可）` — stored outputs/receipts and memo are locally consistent, but original files/power-loss behavior are unproven;
- `証拠なし（復旧可否は未検証）` or `検査不能（復旧可否は未検証）` — a missing/failed inspection does not prove safety.

A stale selected UI row not included in the latest evidence result is also classified as **要確認**. The new checklist does not reduce any of Part37's security checks. An exact output SHA match is only one piece of evidence.

## Tests and CI gate

Nine generated-only NTFS cases:

1. Empty root means no authoritative history, not “all clear”.
2. Missing inspection result cannot grant recovery.
3. Durable intent exists but no output; incomplete vs inconsistent diagnosis is distinct.
4. Fully receipted output + root memo; eight checks, source/power-loss not claimed, no authorization.
5. After successful output receipts, add a foreign `.tmp`; even selecting a verified file cannot hide whole-operation ManualReview.
6. Simulated interruption after output flush but before receipt; unreceipted output is never adopted/deleted.
7. Alter the outermost collision memo; show an explicit report blocker.
8. Modify the source *after* a verified output receipt; the destination remains locally verifiable, but current source stays unverified and Undo unauthorized.
9. Select a stale/forged diagnostic UI row not in the latest result; fail closed.

A real routed WPF test also verifies the selected incident's checklist and disabled Apply button, while the synthetic original SHA/NTFS IDs remain unchanged.

Strict machine evidence: `diagnostics/r217-part38-collision-checklist.json`; independent Part1–37, WPF and win-x64 publish gates remain required.

## Release status and known limitations

Part38 is **read-only UI/decision support**, not actual recovery execution or a general-purpose archive decompressor. The production app cannot invoke Part35 synthetic writer; no original replacement, hardlink merge, independent Undo or destructive same-name collision resolution is enabled. Physical power loss, malicious simultaneous journal+intent rewriting, arbitrary SACL/ADS/EFS or directory rebind races remain unsolved. No user files touched and **0 B** end-user capacity recovered. The historical source coverage estimate (69.4%) is not release readiness.

Require successful PR-head Windows CI, then merge and independently validate the postmerge main CI.
