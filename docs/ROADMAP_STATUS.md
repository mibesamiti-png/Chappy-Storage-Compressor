# Chappy Storage Compressor — R2.16 measured two-stage media scanner

Updated 2026-11-14 UTC. R2.7 source tracking was integrated into `main` through PR #1 (merge `dbfa6a34`). It builds a version 0.2.6 WPF application, with CI fixes and source organization. The version is intentionally unchanged until a functional release gate is achieved.

## Overall roadmap

| Phase | Source implementation estimate | Gate |
|---|---:|---|
| R1 Duplicate detection / hardlink prototype | 90% | Synthetic safety checks pass; production merging disabled |
| R1.x Failure recovery / safety | 98% | Windows synthetic tests pass; full fault-injection matrix incomplete |
| R2 Lossless JPG/PNG candidate creation | 68% | Candidate-only; original replacement disabled |
| R3 Restore independent files | 15% | Separate synthetic detachment tests pass; app undo and metadata guarantees not implemented |
| R4 Batch queue / capacity analysis | 76% | Synthetic CI passes; large dataset benchmarks pending |

**Overall estimated source implementation: 69.4% (unchanged).** This number is only an unweighted historical estimate of source coverage, not release readiness, measured reliability, or a data-loss probability.

## Verified R2.7 tracked-source test result

[Windows pull-request CI run #43479758919](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43479758919) for commit `706b4d789ccc099f50fd7132f92470601e8a3932`:

- Source files imported from verified R2.6 SourceKit ZIP into tracked `Core/`, `tests/`, `tools/`, WPF and docs.
- .NET 10 WPF application build **PASS**: **0 errors, 2 warnings**, down from 6 warnings with archived R2.6.
- C# isolated synthetic safety suite **359/359 PASS**.
- Self-contained win-x64 publish **PASS**, artifact `chappy-win-x64-unverified`.
- Windows independent NTFS hardlink/backup restore probe **PASS**.
- R2.6 structural checks and independent Python filesystem reference checks **PASS**.
- Two source-level test fixes: shared imports for smoke tests, explicit synthetic sentinel handle disposal.
- Four PNG `CA2014` stackalloc-in-loop warnings eliminated by reusing the small spans outside loops.

## R2.8 — measured physical savings on generated Windows NTFS files

New test in [PR #3](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/3); verified [Windows CI #43487742467](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43487742467):

- Two independent 16 MiB synthetic duplicates occupied **32 MiB** of unique-file-ID allocation; after synthetic hardlink consolidation the shared file occupied **16 MiB**. The measured volume free-space increase on that run was **16 MiB**.
- Deliberately uncompressed generated PNG reduced from **1,049,371 B to 3,596 B**, with all non-IDAT chunks and inflated filtered scanlines identical.
- Independent same-volume PNG backup, injected precommit stop, postcommit rollback: **PASS**; exact original SHA-256 restored. Backups consume space, and the PNG logical reduction is **not credited** as recovered end-user disk capacity.
- Existing **359/359** C# synthetic checks, WPF build (0 errors / 2 safety-lock warnings), and win-x64 publish: **PASS**.
- The experiment uses a disposable Windows runner `RUNNER_TEMP` sandbox only. The measurement code is not the production application. Production mutation is still disabled.

[Full measurement table and remaining gates](R28_VERIFIED_RESULTS.md).

## R2.9 — actual child-process termination and guarded journal replay

[PR #4](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/4) / [successful Windows CI #43491388797](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43491388797).

- **9/9** independently launched synthetic child processes were force-killed at durable checkpoints: before journal, after journal, after commit, during rollback stage, plus five adversarial recovery cases.
- **7/7** byte-exact SHA-256 restores and **6/6** fail-closed refusals.
- Unknown/missing journal, altered backup, added hardlink alias, active writer, ADS and read-only attributes remain blocking safety conditions.
- Recovery is **idempotent** on a second replay for completed synthetic transactions.
- The test harness uncovered and corrected a native `BY_HANDLE_FILE_INFORMATION` FILETIME struct alignment bug before any unsafe restoration.
- Existing **359/359** C# checks, R2.8 physical-allocation test, WPF build and win-x64 EXE publish remain PASS.

[Detailed R2.9 test scope, results and limitations](R29_PROCESS_CRASH_GATE.md). This proves **test-only process termination handling**, not sudden loss of power or crash consistency of storage hardware. The independent test backup stays on the same runner drive. No user media was altered, and no extra end-user disk space was recovered in this test.

## R2.10 — synthetic restoration of independent files after deduplication

[PR #5](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/5) / [verified Windows CI #43496747086](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43496747086).

- **4/4** forcibly terminated synthetic child process scenarios passed, with a journal/no-journal distinction.
- **4/4** safe independent restorations (regular operation plus three restart scenarios). Both path names retained the SHA-256 data but used different NTFS file IDs.
- **4/4** editing-independence checks passed: changing one detached name did not modify the other name.
- **7/7** fail-closed refusals passed: missing journal, altered stage bytes, added hardlink, exclusive writer, unexpected ADS, read-only alias and modified shared source data.
- Creation/write timestamps and ordinary file attributes were checked for equality on these synthetic fixtures.
- Independent restoration *increases* physical NTFS allocation; any observed volume free-space delta is noisy and not proof of production capacity changes.
- Baseline **359/359** C# safety checks, R2.8 measurements, R2.9 recovery tests, WPF build and Windows EXE publish remain successful.

[Detailed test plan, evidence and limits](R210_INDEPENDENT_UNDO_GATE.md). This is separate experimental code under `bootstrap/`; **the normal application still cannot perform production deduplication or independent-file restoration**. In particular, the experiment does not prove security ACL/ownership inheritance, nondefault streams, third-party writers, EFS, VHDX, or power-failure durability.

## R2.11 — owner/DACL, alternate-stream and identity fail-closed preflight

[R2.11 PR #7](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/7) and [verified Windows CI #43501540844](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43501540844):

- **7/7** clean metadata baselines and **7/7** unsafe conditions rejected, without changing original synthetic file bytes.
- **2/2** owner+DACL security-descriptor changes detected (target and staged file), even with matching SHA-256 data. Original owner/DACL descriptor is captured in memory and not published in logs.
- **1/1** named NTFS ADS injection detected, with the stream left intact after refusal.
- Extra hardlink alias, same-hash stage file identity swap, read-only original and active exclusive writer also rejected.
- Baseline **359/359** C# checks, R2.8 allocation measurement, R2.9 process-kill recovery, R2.10 independent-file undo, WPF build and win-x64 publish remain successful.

This is an independent `bootstrap/` synthetic **preflight refusal test only**: it does not prove actual ACL/owner persistence through cutover, user-owned metadata restore, SACL/EFS, power-loss safety, race-free file mutation or normal-app integration. Normal app hardlink merge/replacement remain disabled. [Full details](R211_NTFS_METADATA_GUARD.md).

## R2.12 — synthetic owner+DACL preservation across replace and rollback

[PR #8](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/8) / [verified Windows CI #43516165756](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43516165756).

- One generated synthetic NTFS file with a deliberately customized DACL was replaced by an independently staged candidate whose owner+DACL was explicitly copied from the original.
- **1/1** post-replacement owner+DACL SDDL match and staged SHA-256 verified.
- **1/1** independent-backup rollback restored the exact original SHA-256 bytes, owner+DACL SDDL and creation timestamp.
- **5/5** unsafe preflight changes refused, including stage/target DACL tampering, unexpected ADS, identical-hash stage file identity substitution and an extra hardlink.
- Existing **359/359** C# safety tests, R2.8 synthetic NTFS space measurement, R2.9 actual subprocess-kill recovery, R2.10 independent-file detach, R2.11 metadata refusal, WPF build and Windows x64 EXE publish all pass.

[Details and limitations](R212_SECURITY_PRESERVATION_GATE.md). The owner+DACL preservation result is **one synthetic same-account scenario**, not arbitrary ACL/owner preservation or integration into the shipped application. SACL/EFS, nondefault named streams, complex inheritance, real power loss, concurrent source modification and isolated VHDX remain open. **Production deduplication, original-file replacement and undo remain disabled.**

## R2.13 — personal-media development priority / format-specific read-only report

The user-facing objective is **safe lossless JPG/PNG preview candidates, byte-identical JPG/PNG/MP4 duplicate discovery, batch throughput and measured capacity effects** on one person's Windows HDD/SSD. Complex cross-account ownership/inheritance, SACL, EFS, and arbitrary ADS preservation are deferred; special files remain excluded, not silently stripped. Existing safety tests and mutation locks are retained.

[PR #9](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/9) adds a **real WPF GUI button** to summarize reverified exact-duplicate scan results by MP4, JPG/JPEG, PNG and other/mixed extensions, with per-group CSV export. Stale file identities/hashes/bytes are rejected; optional differing timestamps follow the explicit UI checkbox. Outputs are **logical savings estimates only**, not recovered bytes. Full functional design: [R2.13 personal media focus](R213_PERSONAL_MEDIA_FOCUS.md).

**Gates and next priorities**
- Windows WPF build, previous 359 synthetic tests and new per-format report assertions must pass.
- The normal application hardlink merge, original media replacement and independent undo are still **disabled**.
- Next build actual user-facing benefit: improve JPG/PNG batch candidate throughput and measurement, MP4 exact-duplicate inspection on large libraries, and nonoverlapping capacity estimates. Do **not** spend upcoming primary rounds on rare multi-account permission edge cases.
- Dedicated VHDX and power-loss validation are tracked as separate safety gates, not a prerequisite for this read-only format summary.

## R2.14 — single-entry JPG/PNG lossless candidate batch

[Issue #10](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/10) tracks the personal-media workflow priority. A new normal-app WPF button starts **PNG then JPG** candidate generation using one output folder and the *existing separate resumable journals*, without introducing new source mutation code. The integrated preflight reports approximate image counts and source bytes, an output free-space sample, special/empty file exclusions from **inventory estimates**, and scan errors. If PNG defers for insufficient space, JPG is not started; both queues can be resumed with the same folder and tool.

Synthetic Windows smoke tests are extended to verify preflight PNG/JPG/JPEG counts, MP4 exclusion, safe root separation, cancellation, capacity sample, no output changes and handling of common special file attributes. Neither the preflight nor the combined GUI button replaces original photos or videos. Candidate size savings are logical estimates; **actual recovered capacity remains 0 B**.

[Detailed R2.14 workflow and limitations](R214_UNIFIED_IMAGE_BATCH.md). Advanced cross-account ACL preservation is deferred; existing unusual-file safety gates remain in force. **Retain the historical 69.4% source estimate until real release-readiness evaluation.**

## R2.15 — format-specific, nonoverlapping hypothetical capacity report

Existing `CombinedSavingsPlanner` and its WPF report were extended, rather than creating another analysis engine. The normal app now shows **MP4 / JPG / PNG / mixed / other** breakdowns for byte-identical duplicate saving, independently validated PNG/JPG candidate saving, overlap deducted, and the corrected combined hypothetical. It also exports separate per-format reconciliation CSV or existing candidate detail CSV. Users see progress and can cancel. The duplicate scan is **reverified** (snapshot, identity, link count, SHA-256 and exact bytes) and no expired group is counted.

Windows synthetic smoke coverage includes JPG triples, PNG pairs, MP4 pairs, singleton candidate previews, formula/path-safe report output, stale same-size MP4 content, extra hardlink alias, CSV destination restrictions and cancellation. [Detailed method and limitations](R215_RECONCILED_CAPACITY_BY_FORMAT.md).

**Actual freed disk space remains zero**, because production source replacement/deduplication stay locked. The combined figure is hypothetical and subject to NTFS allocation and independent candidate feasibility, never an observed capacity recovery. Keep source progress estimate **69.4% historical and unchanged** until a grounded reassessment.

## R2.16 — two-stage personal-media scanner and measured synthetic work

The actual `Core/Scanner.cs` read-only duplicate finder has been optimized: the first pass collects lightweight length/time/attribute snapshots, while only paths with repeated file sizes trigger `Safety.Capture` (NTFS file identity, owner/ACL, ADS), SHA-256 and byte equality. Candidate metadata is compared against the first pass to reject changed files. `ScanResult` and the WPF scan log now report the number of deep-validated and hashed candidates. **Production original-file mutation remains disabled.**

The generated Windows benchmark checks **372** shallow-eligible files (360 unique sizes + 12 ambiguous), expected **12 deep checks**, **3 byte-identical JPG/PNG/MP4 groups**, ADS and hardlink rejection, cancellation, rough elapsed time and managed thread allocations. No runtime speedup threshold is asserted; the counts are a reproducible indicator of reduced security-descriptor lookups only for this test distribution. [Design, evidence and limitations](R216_TWO_STAGE_SCANNER_BENCHMARK.md).

**Source implementation percentage: historical 69.4% unchanged** pending an evidence-based release functionality assessment. Free disk space physically reclaimed remains **0B**, as original replacement, deduplication and independent undo remain disabled.

## R2.17 (part 1) — same-size-heavy generated benchmark and hash-stage feedback

R2.16の測定は重複サイズが少ない入力に偏っていたため、R2.17の第1段階として、約110MiBの生成データを使う異なる2種類のNTFSスキャン比較を追加。異なるサイズ48件では深い検査0件、同一サイズ76件では深い検査76件・SHA-256試行73件・完全一致3グループを必須とし、既存ハードリンク・ADSの誤検出を拒否する。

各データセットの初回・再実行の時間、プロセスI/O転送量、管理メモリ割当、プロセスメモリ値を機械可読なJSONに保存。ただし初回はコールドキャッシュではなく、プロセス転送量は物理HDD/SSD読み取り量でもない。GUIの長時間スキャン表示は、候補単位のSHA-256進捗を約20回に制限して途中経過を示す。単一巨大ファイル途中のバイト進捗とUI操作試験は未実施。

安全テストは生成バイト列のみを使う。PNG/JPG候補生成のための有効な画像形式での性能評価、ディスクキャッシュを制御した比較、実機WPFの停止確認は今後の課題。[仕様と結果](R217_SAME_SIZE_SCANNER_BENCHMARK.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)。**本番の重複統合・原本置換・統合解除は無効**、実際に回復した空き容量は0B。歴史的なソース実装見積もりは根拠のない再算定を避け**69.4%を据え置く**。

## R2.17 (part 2) — generated valid PNG/JPEG profiling

画像として解釈不能なランダムバイト列から一歩進み、12 PNG / 12 JPEGの合成・有効画像を生成して本番系のPNGバッチ候補生成・再開とJPEGの処理計画・マーカー/全画素読み込みを分離して計測。PNG候補は本体の独立検証に加え、WICデコードした画素の一致と入力ハッシュ不変を検証する。JPEG本番候補生成は外部jpegtran.exeが必要なため未計測、CIで外部実行ファイルを勝手にダウンロード・実行しない。

計測結果は[専用仕様と制限](R217_VALID_IMAGE_BATCH_PROFILE.md)に記録。旧プログラムの安全ゲートを維持したまま、R2.17 Issue #14のJPEG実候補生成、長時間GUI進捗、繰り返し操作簡略化等は引き続き未完了。**source 69.4%は歴史的見積りのまま、実際の容量回復0B**。

## R2.17 (part 3) — intra-file hash progress and cancel safety

読み取り専用の重複スキャンに、SHA-256計算時4MiB単位の細かな読み取りバイト進捗通知とWPF画面の進捗表示連携を実装。メタデータ検査までのバーは不定とし、サイズ重複候補のハッシュ段階のみバイトベースの達成率を表示。中断時に不完全なScanResultを返さず、元のファイルとNTFS IDを保全することを合成12MiB×2ファイルでチェックする。WPFの停止ボタン実クリック/画面描画を操作したUIテストは別途必要。また、キャンセル後のスキャン自動再開は未対応（バッチPNG/JPG候補の再開とは別機能）。

[仕様と制限](R217_HASH_BYTE_PROGRESS.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)。本番の原本置換・重複統合・解除は無効。実容量回復0B。歴史的ソース実装率69.4%は証拠なしの改訂をしない。

## R2.17 (part 4) — explicit and guarded combined-preview resume

PNG＋JPG一括候補の出力先選択後に、保存済みPNG/JPG計画の有無と整合性を読み取り専用で調べて新規・一部再開・全再開を区別し、明示確認を挟む。想定外の出力内容・ジャーナル欠損・入力とツールの不一致・NTFSハードリンク等を検出したら開始前に拒否する。作業記録や候補の自動削除・修復は行わない。中断ボタン押下時の状態表示も改善したが、Windows画面上の実クリック自動テストは未実施。

[仕様・検証限界](R217_SAFE_COMBINED_RESUME.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)。本番原本置換・ハードリンク統合・独立解除は無効。実容量回復0B。ソース実装率69.4%は根拠なしに更新しない。

## R2.17 (part 5) — WPF routed Click smoke (synthetic-only)

別プロジェクトのSTA/WPF UIスモークテストを追加し、実際のXAMLボタンのClickルートを通してスキャン開始→中断要求→終了→再スキャン完了を確認する。ログとJSONは人工NTFSの2×32MiBテストのみを対象とする。実際の物理マウス操作や表示画面・手動ダイアログは未テストのまま区別する。外部jpegtranの実候補生成速度は未計測。原本置換・統合・独立Undoは無効。実容量回復0B。歴史的進捗率69.4%は変更しない。

[手順と制限](R217_WPF_ROUTED_CLICK_SMOKE.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 (part 6) — source-pinned official jpegtran candidate profile

公式libjpeg-turbo 3.2.1 の**フルGitコミット**検証→Windows CIでjpegtranをソースビルド（SIMD無効）→人工JPEG 8件の実際のプレビューエンジン候補生成までの安全な性能計測を追加。WIC全画素・APP/COM/量子化/SOFマーカー一致、SHA256原本不変、再実行時の作業記録/候補再利用を確認する。単一CIホストの経過時間は利用者のHDD/SSDやSIMD最適化済み公式バイナリの実力を保証しない。温冷キャッシュ制御と実機UIは引き続き別課題。生産環境の原本置換・重複統合・独立Undoは無効。実容量回復0B、歴史的ソース実装率69.4%を変更しない。

[試験詳細](R217_OFFICIAL_JPEGTRAN_PROFILE.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 7 — synthetic Exif/XMP/ICC metadata integrity and native SIMD

公式libjpeg-turboの同一固定コミットから、SIMD有効・無効版を別ビルドし、Exeの独立SHA-256とNASM固定版を確認する。人工JPEG8件にExif/XMP/ICC/Photoshop APP13/COMの合成マーカーを入れ、実プレビューエンジンをABBA順で各2回測定。採用候補のWIC全画素比較、独立APP/COMバイト比較、保護マーカー比較、元のSHA-256・更新日時不変をCI必須とする。単一CIホストの経過時間は利用者のPCや写真にそのまま適用できず、性能改善保証をしない。安全ロックは維持し、実空き容量回復0B。

[テストの詳細](R217_JPEG_METADATA_SIMD.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 8 — real Windows sRGB ICC / WIC color conversions

Windows CIに標準搭載された実際のsRGB ICCプロファイルを読み込んでWPF ColorContextで検証し、合成JPEGのAPP2に複数分割して埋め込む。実プレビューエンジンでSIMDあり/なしをABBA順で検査し、ICC生バイト・WIC色変換BGRA32画素・原本SHA/日時の完全一致をCI必須とする。ICC断片の欠損・重複は拒否する。Windows ICC本体はGitHubに同梱しない。独立監査と処理エンジンの時間を区別し、スレッド割当・プロセスワーキングセットはピーク値やハードウェア性能の主張に使わない。実画像・物理GUI・厳密な温冷キャッシュ測定は未実施。原本操作禁止、実空き容量回復0B、歴史的実装率69.4%は再評価まで維持。

[試験条件](R217_REAL_ICC_COLOR_MANAGEMENT.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 9 — Exif 1/3/6/8 orientation and memory observation

ExifのOrientation（1・3・6・8）を人工JPEGに入れ、正式候補エンジンでSIMDあり/なしの生成後にTIFF値・APP1バイト・WIC復号画素・Exif適用後の全画素一致を独立検査する。Exif改ざんや不正値は人工マイナス試験で拒否。WindowsにsRGB以外の適切なICCがある場合はそれを利用し、非存在時は固定コミット・SHA-256照合済のCC0 Display P3互換ICC（456B）を人工JPEGに埋めて、WPF色変換後の一致を必須試験とする。10msのプロセスWSサンプリングはJPEG子プロセスの独立ピーク計測ではない。実データ・原本置換・ハードリンク統合は無効。実回復0B、暫定実装率69.4%は据え置き。

[仕様と限界](R217_EXIF_ORIENTATION_MEMORY.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 10 — OS-tracked native jpegtran child peak resident memory

Windows CIの人工JPEG3件×公式ソース固定ビルドの通常版/ネイティブSIMD版で、6回の `jpegtran.exe` 子プロセスの生存中に `PeakWorkingSet64` を繰り返し観測する検証。各子プロセスの `-copy all -optimize` 出力を、本番の読み取り専用プレビューエンジンから得た出力と**バイト完全一致**で照合し、元のSHA-256・更新日時が変更されないことを必須とする。結果の意味は子プロセス生存中に観測したOS管理ピーク常駐メモリに限定され（終了直前を完全には捕捉できない可能性あり）、アプリ全体のメモリピークや物理ディスク速度の保証ではない。本番の原本置換・リンク統合を引き続き無効とし、実空き容量回復0B、歴史的実装率69.4%は再評価まで維持。

[テスト範囲](R217_NATIVE_CHILD_PEAK_MEMORY.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 11 — prioritized batch matrix, one release PR

優先順位が高い3テーマを一括テスト基盤で検証。①人工ファイル1,024件のサイズ一意スキャン＋48件の同サイズ衝突（独立SHA/バイト正解比較）、②WPFで復号可能なJPEG 6枚と非圧縮PNG 6枚の実プレビュー生成・再開・全画素検証、③2560×1600の人工JPEG 2枚で本番エンジンから起動されたjpegtranのPIDをテストビルド限定で取得し、親アプリとネイティブ子プロセスのOSワーキングセットを同時観測。時間・ピークは比較用参考値であり、コールドキャッシュ、物理ディスクI/O、完全同時ピークや性能向上を主張しない。**原本置換・統合は本番無効、実容量回復0B、ソース概算69.4%を据え置く。**

[設計・制限・CI結果](R217_PRIORITY_GROUPED_MATRIX.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 12 — weighted batch preview UX and rate-limited WPF updates

PNG/JPG一括候補処理で、ファイル件数比に合わない50/50固定の進捗バーを改善。前回の出力先・jpegtran選択場所はアプリセッション中の**ダイアログ候補のみ**とし、元の明示確認と `CombinedBatchResumeInspector` の再開安全診断を省略しない。エンジンが行う100,000件分の進捗通知でWPFへ100,000件をそのまま送らないよう、参考値125msのレート制限を追加し、失敗・Deferred・NeedsReviewは優先配送する。テストでは人工イベント30,005件と数え上げ・0件・不整合値、WPF本体のスキャン開始・停止・再試行中のPNG/JPGボタン制御を確認する。これは**描画速度・コーデック性能の実測改善率ではない**。原本置換・リンク統合・Undoは本番無効、容量の実回復0B、ソース実装率は暫定69.4%のまま。[検証記録](R217_BATCH_UX_THROTTLE.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14)

## R2.17 part 13 — confirm-time output reinspection (candidate-only)

The PNG/JPG combined preview UI now re-runs the **read-only** output-folder/journal inspection after the user accepts its explicit confirmation dialog and before either preview engine starts. If an unfamiliar file appears, a valid PNG/JPEG plan arrives/disappears, the folder becomes unsafe, or cancellation is requested while the dialog is open, the preview does not start. A synthetic NTFS regression exercises the no-change passes and changed-state refusals without mutating source media; the existing CI JSON gate now requires these results. This **does not create an atomic snapshot**: both engines must still perform their own complete validation, and real physical UI interactions are still unverified. Release requires successful final PR-head and postmerge main CI. [Safety scope](R217_CONFIRMATION_RECHECK.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14). **Actual reclaimed user capacity 0 B**; production replacement, merge and Undo remain disabled. Historical source estimate 69.4% unchanged.

## R2.17 part 14 — plan/journal byte-bound approval recheck

Part 13's post-Yes inspection compared only plan presence and entry counts. Part 14 extends `CombinedBatchResumeInspector.Summary` with **SHA-256 fingerprints of the exact validated PNG/JPEG plan and progress-journal bytes** for the two checks surrounding user consent. Valid-looking same-count changes or extra blank journal rows now invalidate that confirmation. The existing synthetic-NTFS suite also checks pre-canceled confirmation rechecks and byte-for-byte restoration of the original test state. Six new machine-readable diagnostics are required in the Windows CI preflight gate, in addition to all prior security/fault and routed WPF checks.

This is *not* physical mouse/dialog automation or a filesystem transaction; it does not hash every preview candidate's content, nor prevent modifications immediately after the recheck. Each format engine must continue to revalidate candidates. [Scope and test details](R217_APPROVAL_CONTENT_SNAPSHOT.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14). **Real capacity recovered 0 B; production original replacement/hardlink merge/Undo remain disabled; historical 69.4% estimate unchanged.** Formal completion needs final PR-head and postmerge main CI.

## R2.17 part 15 — routed WPF combined-preview dialogs and refusal lifecycle

WPF's existing PNG/JPG combined preview handler now obtains every jpegtran choice, output-folder choice and Yes/No through **private per-window native-default dialog adapters**, enabling the real `UnifiedBatchButton` Click path to be tested by deterministic responses in noninteractive Windows CI. The actual inspector and preflight run unchanged; the UI indicates "確認でキャンセル（未実行）" after No. The new STA/WPF suite verifies canceled tool picker, canceled output picker, explicit No after real preflight, canceled token during consent, a foreign output file created after Yes, and a fresh No on retry. No preview engine starts in these refusal scenarios, and source SHA-256s and any foreign file remain intact. The test does **not** physically click native dialogs or display a desktop, and production still uses the real Windows pickers. [Scope and limitations](R217_WPF_COMBINED_CONSENT_SMOKE.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14). No production replacement/hardlink merge/Undo, actual user disk recovery 0 B, historical source estimate 69.4% unchanged. Verify final PR and postmerge main CI before declaring the stage complete.

## R2.17 part 16 — real combined preview success and resume through routed WPF Click

Part 15's WPF runner proved refusal paths with a dummy non-executed JPEG tool. Part 16 additionally clicks **the same real `UnifiedBatchButton`**, supplies CI's independently source-pinned official `jpegtran.exe` and twelve disposable valid WIC-generated JPEG/PNG files, explicitly approves the preview, waits for a successful result dialog (private native-default in-process test adapter), and checks the actual format engines generated candidates with 100% GUI progress. It then clicks the real button a second time, explicitly picks/approves again, and verifies the saved candidate hashes, candidate counts and original journal bytes are unchanged. The original source SHA-256s remain equal. If the official pinned tool is absent, standalone WPF tests mark the live batch **skipped**; the Windows CI gate requires successful real execution. This is still routed WPF event testing, **not** actual native dialog mouse/screen automation. No original replacement/hardlink merge/production Undo; real user disk capacity recovered **0 B**. Historical source estimate 69.4% unchanged. [Details](R217_WPF_COMBINED_SUCCESS_RESUME.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14).

## R2.17 part 17 — deterministic routed WPF Stop between PNG/JPG candidates and safe resume

Part 16 verified a complete `UnifiedBatchButton` PNG/JPG run and immediate no-duplicate restart. Part 17 adds **two separate real WPF routed Stop events** at predictable journal-safe file boundaries, not time-based races. A private null-by-default per-window progress tap forwards the actual on-worker `PngBatchPreviewEngine.Progress` or `JpegBatchPreviewEngine.Progress` update only after the normal report; the isolated test uses `Dispatcher.Invoke` to route the existing `StopButton` Click when the first item has completed, so the next engine iteration observes the canceled token. One valid PNG candidate/journal and no JPEG plan must remain after the first stop. A second explicitly approved WPF click resumes PNG, then stops after one official-jpegtran JPEG candidate; a third click fully resumes both formats while preserving the exact SHA-256 of the earlier PNG/JPEG candidates and all 12 synthetic source originals. CI requires six new Boolean evidence fields, four exact candidate counts, and explicit no-physical-mouse-automation evidence, in addition to all previous CI gates.

This verifies cancellation **between completed candidate items**, not arbitrary in-file interruption or a native Windows physical click. Test hooks are null by default in production. Original-file replacement, hardlink merge and independent Undo remain disabled; actual recovered disk **0 B**; historical 69.4% estimate unchanged. [Details](R217_WPF_MIDBATCH_STOP_RESUME.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14).

## R2.17 part 18 — cancel during PNG compression and after jpegtran process spawn

Part 17 demonstrated deterministic real WPF Stop between fully journaled PNG/JPG candidates. Part 18 adds two **Core-only CHAPPY_TESTING probes**, without production-visible testing APIs: cancel after a real PNG inflated block has been handed to the candidate compression stream and cancel synchronously immediately after an actual official-source `jpegtran.exe` child process is created. The native child is killed/waited before propagating cancellation, and both engines must remove their unfinished candidate, preserve generated source hash/timestamp, and accept a fresh successful candidate-only retry. The JPEG retry is independently pixel/metadata marker validated. Existing Windows CI strict evidence checks both tests, the exact native PID termination and all previous C#/WPF/NTFS gates.

**Scope:** cooperative in-file PNG cancellation and post-native-Start JPEG cancellation, **not** forcibly killing the entire app, in-progress native encoder timing guarantees, physical mouse Stop, or transactional power-loss recovery. No original replacement, hardlink merge or production Undo. Actual user capacity recovered **0 B**; historical 69.4% source estimate unchanged. [Design and observed limits](R217_INNER_CANCEL_NATIVE_CLEANUP.md) / [Issue #14](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/14).

## R2.17 part 19 — actual subprocess death during PNG candidate construction and orphan refusal

A disposable **Core smoke-test child process** runs the real PNG batch engine on synthetic NTFS data, persists its plan, begins streaming an uncompressed valid PNG into a candidate, records a test-only inflated-block checkpoint, then terminates itself abruptly using `Process.Kill` without normal `finally` cleanup. The parent verifies the exit, durable plan, one unjournaled incomplete candidate, original SHA-256/mtime, then verifies that **both** the combined preflight and the direct PNG resume refuse the orphan and preserve its bytes and plan. Only the test explicitly removes its own known orphan, then resumes the original saved plan to a verified candidate. A shared `PreviewReferenceGate` also causes direct PNG and JPG engines to refuse **syntactically valid but unreferenced candidate names**, preventing silent reuse or duplicate generation. Tests for both extensions preserve all unknown bytes. No production auto-delete or original media write is permitted. [Part 19 test design](R217_PREVIEW_PROCESS_CRASH_ORPHANS.md).

**Limitations:** This proves an abruptly killed **standalone Core test worker**, not the production WPF process, and not termination of an active native jpegtran child. It does not prove disk power-loss ordering or atomic snapshots. All files are generated in a unique disposable NTFS root. Original replacement/hardlink merging/Undo stay disabled; real user recovered capacity **0 B**, historical 69.4% estimate unchanged. Final PR-head Windows CI and independent postmerge main CI remain separate gates.

## R2.17 part 20 — actual native jpegtran forced exit + post-encode JPEG worker crash before journal

Building on Part 19's real PNG process-kill and unreferenced-candidate refusal, Part 20 adds two **separate generated-only** JPEG failure modes: (1) the Core test genuinely kills the exact official-source-pinned native `jpegtran.exe` PID immediately after it starts (without cooperative cancellation), and requires the production JPEG engine to reject its nonzero exit, clean incomplete output, preserve original SHA-256/mtime, and retry with matching JPEG markers and decoded WIC pixels; (2) a guarded disposable Core child runs the real JPEG batch and official pinned SIMD tool to a **verified smaller JPEG candidate**, then abruptly kills *the Core worker* before appending the first durable journal entry. Both combined resume and direct JPEG queue must reject the resulting **fully formed but unjournaled** orphan without deleting it. Only test-only explicit orphan removal permits reuse of the original plan, verified recovery and an idempotent second resume. Source and candidate integrity are checked; no production original mutation. [Design and limits](R217_JPEG_CRASH_RECOVERY.md).

**Scope:** the native process is killed at a deterministic post-`Process.Start` checkpoint, which does not prove it encoded any bytes before termination. The separate worker crash occurs *after* a completed native encoding, not while native encoding remains active. This does not simulate power failure, a complete production WPF app crash, or physical user interface automation. Original replacement, production hardlink merge and independent Undo remain disabled; actual user disk recovered **0 B**; historical source estimate **69.4%** unchanged. Require final PR-head and independent postmerge main CI checks.

## R2.17 part 21 — bind displayed source consent + managed parent death while native jpegtran lives

**Source consent:** Combined WPF preview confirms the source inventory *twice*: at the original user-facing preflight and after explicit Yes, after its existing plan/journal SHA-256 reinspection. A deterministic, sorted **metadata-only** inventory SHA-256 binds eligible PNG/JPG relative filenames, lengths, UTC mtime and attributes, plus per-format counts/bytes and scan/exclusion counts. A new file, rename with identical count/bytes, or retimestamped image invalidates consent before the first preview plan/codec; volatile output free-space samples are deliberately excluded. Engine-level original-content SHA-256 and NTFS safety remain authoritative, since the consent snapshot is **not** a byte-perfect source snapshot and is not an atomic filesystem transaction. Both Core synthetic and real WPF routed Yes callbacks test refusal without source mutation. [Part 21 details](R217_SOURCE_CONSENT_AND_NATIVE_PARENT_DEATH.md).

**Actual native-child lifecycle:** A guarded standalone Core child launches the official source-pinned native `jpegtran.exe` on a **generated large JPEG**, verifies the child PID is alive just after `Process.Start`, then abruptly kills its **managed parent**. The independent test process waits for that exact native child to exit (bounded; emergency termination permitted only after PID/start time/executable path verification), checks the durable JPEG plan and absent journal, and refuses any leftover unjournaled candidate with both combined and direct resume before explicit synthetic-only cleanup. It then generates a pixel/marker-verified JPEG and confirms idempotent resume. Native encoding progress at the instant of parent death is **not** proven; native child may already have exited by the time the independent parent checks. This is not a production WPF application forced-exit test, physical mouse simulation or power-loss durability proof.

**All production original replacement, hardlink merging and independent Undo remain disabled. Real user capacity recovered 0 B; historic source-implementation estimate 69.4% unchanged.** Final PR-head CI and independent postmerge main CI are distinct gates.

## R2.17 part 22 — refuse foreign output and changed approved input at the actual preview engine boundary

Part 21 validated source inventory after the WPF confirmation. Part 22 closes two further **deterministically tested** gaps in the existing preview-only flows: (1) the read-only combined inspector and both format engines **when invoked with an explicit WPF-approved input snapshot** now use one shared `PreviewOutputStartGate` whitelist/attribute/NTFS hardlink check for the output root; in explicitly approved runs both engines apply it **before opening the exclusive lock**, again after opening the lock, after an input-plan scan but before saving a new plan, and after plan replay/save but before the candidate directory/file is opened; (2) the WPF combined-click handler passes its originally displayed `PersonalMediaBatchPreflight.Summary` into **each** format engine, which independently rechecks source path/size/UTC mtime/attributes before lock and immediately before generating candidates. This is separate from the preview engine's own source SHA-256 and byte/pixel checks. Legacy standalone direct-format runs without a combined approval snapshot keep the R1.7 journal backup/CSV workflow and existing candidate-orphan safety checks; they do **not** promise the new strict root allowlist by default.

Generated-only deterministic tests add a foreign root file just after the final WPF rechecks and just before the engine call; no lock or plan may be created. They also inject a foreign output and a newly added/retimestamped source **after a real PNG/JPEG plan has been saved but before the first candidate**, requiring read-only refusal, preserving the saved plan and foreign evidence, and a successful resume only after the test explicitly clears/restores its own fixtures. The JPEG retry uses official source-pinned SIMD jpegtran with protected marker and WIC pixel comparison. [Part 22 design, limits and recovery](R217_START_BOUNDARY_RACES.md).

**Not atomic:** An external process can still mutate a file immediately after a check. Root checks require repeated O(number of root files) validation; source inventory checks require repeated source enumeration, and do not hash all source bytes. No production WPF forced kill, power-loss simulation, original replacement, production hardlink merge or Undo. Actual recovered user bytes **0 B**; historical source estimate **69.4%** unchanged.

## R2.17 Part 23 — integrated duplicate-first proof, one representative preview and independent Undo

This is the first **actual Core-level combined synthetic workflow** connecting the existing real `Scanner.Run`, the existing `DedupeEngine.Run` (still compiled **test-only for destructive changes**), real `PngLosslessOptimizer.CreatePreview` and official pinned-SIMD `JpegLosslessPreviewEngine.CreatePreview`, and the new `IndependentUndoEngine.Restore` (compiled **test-only** for any mutation). Three disposable JPEG/PNG/MP4 duplicate groups (7 independent files, 4 aliases) must be revalidated, then linked by the actual Core dedupe engine, scanned again to exclude already-linked data, previewed once per **image representative**, and finally detached back into seven independent byte-identical files with checked file IDs, timestamps, attributes, owner+DACL bytes and no extra store links. Deliberate independent write to an Undo-restored MP4 alias must not modify the former master. An unexpected extra hardlink and changed staged bytes cause fail-closed refusals, with test-only evidence retention and explicit cleanup. This integration is **not** a production/real-user disk mutation or crash-consistent Undo proof.

The **normal WPF application** now offers a new, enabled-after-scan **read-only duplicate-first/Undo preparation button** that separately rechecks every scanned eligible group, estimates potential duplicate savings, identifies JPEG/PNG representatives versus aliases, and reports Undo's approximate reallocation cost. The actual Merge button stays disabled; no file operation is requested from that preview. A routed WPF Click smoke test independently verifies source hashes, NTFS identities, no store and no production consolidation. [Implementation and limits](R217_DUPLICATE_FIRST_INTEGRATED_UNDO.md).

**Crucial remaining blockers:** `DedupeEngine.Run` historically removes its journal at success and does **not** retain an authoritative durable **completed-consolidation ledger**, so an app reboot cannot reconstruct precisely which originals/metadata to restore. This new Undo engine deliberately requires the original in-memory `FileSnapshot` from the same synthetic scan, a unique sentinel test root and controlled security metadata. It does **not** prove crash replay, arbitrary owner/DACL/SACL/ADS/EFS, aliases outside the scan, real-world concurrent writers, isolated VHDX capacity or GUI opt-in. Production deduplication, original replacement and Undo remain **disabled**, actual user bytes freed **0 B**. Historical source estimate **69.4%** unchanged. PR-head and postmerge main CI remain separate verification gates.

## R2.17 Part 24 — durable completed-merge ledger and genuine restart recovery

The existing R1 transaction journal is **only for incomplete in-flight consolidation** and is removed after every success. Part 24 adds a separate `Core/CompletedMergeLedger.cs` **read-only in production**: persisted per-alias original NTFS file identity, SHA-256, size, creation/write timestamps, attributes and owner/group/DACL bytes, plus actual merged master identity and expected link count. The normal WPF application adds a button to read and revalidate that historical evidence without creating files or enabling Merge/Undo. Suspicious, malformed, hardlinked or changed entries are explicitly **NeedsReview**, not auto-promoted or repaired.

For `CHAPPY_TESTING` on **uniquely sentinel-verified disposable TEMP NTFS roots only**, `DedupeEngine.Run(... testOnlyRecordCompletedMerges:true)` now writes the original metadata and a flushed `.prepared.json` **before** deleting its independent backup and atomically renames the prepared record to `.completed.json` **before** completing the existing pending transaction journal. A separate child process is actually killed immediately after the real `File.Delete(backup)` and before ledger finalization; upon restarting, the read-only inspector must show **PreparedPendingRecovery**, not a completed Undo authorization. The existing `RecoveryService.Inspect/Apply` must explicitly validate/clear the pending journal, then a separate explicitly requested test-only confirmation rechecks the exact prepared alias/master/SHA/metadata/link count before promoting it. A fresh original `FileSnapshot` for independent Undo is then reconstructed **entirely from on-disk completed history**, not the vanished original scan; the existing test-only independent Undo must verify bytes, independent NTFS ID, timestamps and owner+DACL. External extra hardlinks and malformed ledger evidence must refuse without auto cleanup. [Part 24 exact contract and limitations](R217_COMPLETED_MERGE_LEDGER.md).

**No product mutation enabled:** `CompletedMergeLedger` writers, prepared-record promotions, and metadata-to-Undo accessors compile only with `CHAPPY_TESTING` and refuse all non-disposable paths. Production `DedupeEngine`, real-original replacement and `IndependentUndoEngine` remain disabled. This test demonstrates a **single-pair** ledger workflow, not safe production arbitrary multi-alias undo, power-loss/storage-cache barriers, arbitrary ACL/SACL/ADS/EFS, or recovery from every possible crash checkpoint. Actual end-user recovered capacity **0B**, historical source coverage estimate **69.4%** unchanged.

## R2.17 Part 25 — interrupted independent Undo: persisted stages, actual forced child death and explicit synthetic replay

Part 24 saved completed consolidation records across an app restart, but the **Undo itself** could still die between independently copying an alias and committing/restoring file metadata. Part 25 adds `Core/IndependentUndoCrashJournal.cs` with a read-only production `Inspect` and a strictly **`CHAPPY_TESTING`-only** persistent phase record. It writes a disk-flushed `intent` **before** staging, atomically transitions phase names after validated stage, after actual `File.Replace` cutover, and after original owner/group/DACL/timestamps have been fully restored. It never trusts the phase alone: it reopens the exact original completed-merge history and revalidates alias/master SHA-256, NTFS volume+ID and expected link counts. A crash **before cutover** retains original shared links: explicit synthetic-only recovery verifies known stage, archives its record as aborted and safely retries the original test-only Core Undo. A crash **after cutover** detects that the alias already has its own NTFS identity; it **never** re-runs `File.Replace`, and instead re-applies original security/timestamps if necessary and verifies the persisted history before marking it restored. [Part 25 contract](R217_INTERRUPTED_UNDO_RECOVERY.md).

A real separate test-only Core process will be forcibly killed in five distinct windows: before staging, after stage validation, immediately after `File.Replace` before recording cutover, after metadata restoration before completed-history update, and after completed history was updated but before the Undo journal finalizes. No automatic cleanup of an unknown stage or record; a deliberately modified candidate stage yields ManualReview and all suspicious bytes remain intact. The normal WPF `CompletedLedgerButton` only **displays** number of unfinished / manual-review Undo records along with completed merge history, and cannot enable destructive operations.

**Not yet a shippable Undo:** All added journal writers, explicit retry and cutover metadata repair are `CHAPPY_TESTING`-gated on uniquely verified disposable local NTFS test fixtures. Production can only inspect. These are controlled process crashes, not sudden device power loss, cache/rename durability guarantees or arbitrary multi-group ACL/EFS/ADS replay. Original replacement, production hardlink merge and production Undo remain disabled; actual user capacity recovered **0 B**; historical 69.4% implementation estimate is unchanged. PR-head full CI and independent postmerge main CI remain separate gates.

## R2.17 Part 26 — disjoint multi-group completed history and fail-closed multi-alias preflight

Part26 introduces a stricter **test-only** completed-ledger safety boundary: a group with three or more independent filenames is skipped **before** a new hardlink, pending journal or backup rename when `DedupeEngine.Run(...testOnlyRecordCompletedMerges:true)` is used. The existing one-alias durable record stores the master's exact link count; applying a second saved alias to the same master otherwise invalidates that earlier record. It would be misleading to claim that unsupported N-alias groups already have crash-consistent persisted Undo.

A new generated-only Windows Core integration uses three **different two-file** PNG/JPEG/MP4 groups plus an unsupported three-file MP4-like group. It tests one real Core merge pass, three independently re-openable completed ledgers, a changed external hardlink affecting only its own group's read-only approval, and three actual independent Undo operations using original metadata from persisted history. Source bytes, file identities, timestamps, owner+DACL and journal cleanup are asserted. CI additionally requires `r217-multigroup-ledger.json` evidence. [Part26 scope and next multi-alias gate](R217_MULTIGROUP_LEDGER_GATE.md).

**Still outstanding:** a group-level manifest and restartable N-alias commit/Undo protocol, true power-loss durability/isolated VHDX capacity, external writers and arbitrary security metadata, and final production GUI user approval. The normal WPF app's original replacement/merge/Undo are still disabled, actual user capacity regained **0 B** and the historical **69.4%** source estimate remains unchanged. Require final PR-head Windows CI and a distinct postmerge main CI run.

## R2.17 Part 27 — generated 3+ alias group ledger and verified restart between independent Undo checkpoints

A separate `CompletedGroupLedger` records **all original alias identities and owner+DACL/timestamps before changing the first generated file**. The `CHAPPY_TESTING`-only group workflow invokes the real Core `DedupeEngine.Run` for a synthetic 4-filename MP4 group, validates the exact group master identity/link count, and persists one group-wide completed record. This addresses the Part26 v1 per-alias ledger count limitation **without weakening existing two-file evidence**. A new production-compatible inspector is read-only.

Each synthetic independent Undo success is separately checkpointed with its exact resulting NTFS identity. An external unexpected alias makes the entire group ManualReview, while actual worker death **after the first durable per-alias checkpoint** permits explicit revalidation and continuation of the remaining two aliases after restart. The test uses a genuine killed child process and verifies all 4 original contents and metadata. CI requires a strict `r217-group-ledger-resume.json` report. [Design, safety boundaries and remaining work](R217_GROUP_LEDGER_RESUME.md).

**Important nonclaim:** a crash *inside* one group alias's own `File.Replace`/metadata/callback window, a partial group merge, an actual power interruption, arbitrary file permissions or external writers still require separate safety gates. Interrupted uncheckpointed alias changes are ManualReview and are not automatically undone. Production hardlink merge/Undo/original replacement **remain disabled**, 0 B end-user capacity reclaimed; 69.4% remains historical source coverage, not product readiness. Require final PR-head and independent postmerge main CI.

## R2.17 Part 28 — four actual group Undo intra-alias process deaths and two-group isolation

Part27 resumed only after a completed per-alias Undo checkpoint. Part28 uses the real CHAPPY_TESTING-only `IndependentUndoEngine.Restore` stage/Replace/metadata checkpoints and extends `CompletedGroupLedger` with a **durable active alias stage and phase** before file mutation. Read-only inspection verifies the complete group NTFS identity/link-count/bytes and per-file metadata, distinguishing an alias still shared (explicit retry after safe stage inspection) from one already independent (restore metadata without repeating `File.Replace`). Suspicious stages, extra links, or inconsistent provenance are manual-review refusals, not automatic cleanup.

New generated Windows tests actually kill four child processes at intent, staged, immediately post-cutover, and fully restored metadata before group checkpoint. Each scenario uses **two independent 3+ filename groups** to detect accidental cross-group interference; synthetic stage and link tampering must fail closed. Separate strict CI proof: `r217-group-intra-alias-crash.json`. [Contract and limitations](R217_GROUP_INTRA_ALIAS_CRASH.md).

**No production mutations enabled.** This is controlled managed-process death, not hardware power loss, true atomic multi-group transactions, or arbitrary permissions/ADS/EFS and real user-media cutover. Real user recovered capacity remains **0 B**, and historical 69.4% source implementation estimate is unchanged (not product readiness). PR-head and postmerge main CI must both succeed.

## R2.17 Part 29 — isolated VHDX physical NTFS accounting and Core low-space refusal

A generated-only, CI-scoped 512-MiB NTFS VHDX now measures unique-file-ID allocated bytes and free-space changes after synthetic 8-MiB dedupe and independent Undo, then fills **only that isolated virtual volume** below the 4-MiB target reserve to validate low-space refusal without touching other drives. The script accepts no user paths, selects/formats/detaches only its own new virtual disk, and must verify a distinct mounted volume GUID. If Windows CI cannot safely attach an isolated VHDX, this proof is **not established**, and the dedicated strict gate fails rather than silently falling back to previous host-drive measurements.

A separate `CHAPPY_TESTING`-only `CapacityGuard` observation override drives the actual Core `DedupeEngine.Run` through below-reserve target/journal preflight, post-master-link refusal, and a late shortage after one successful alias. The test insists on exact original SHA, NTFS identity and clean transaction journals. The override is absent from production binaries. [Part29 design and limitations](R217_PART29_VHDX_CAPACITY.md).

**These tests are not a release permission:** no user-file consolidation, original replacement, or production Undo; real recovered user capacity stays **0 B**, historical 69.4% estimate stays a source coverage snapshot rather than current completion percentage. Still missing: physical power interruption/durability, concurrent writers and arbitrary owner/ACL/SACL/ADS/EFS, capacity reservation races, and a full opt-in WPF release safety contract. PR-head and independent postmerge main Windows CI both required.

## R2.17 Part 30 — one read-only view of all durable recovery records

The WPF Recovery Wizard now displays **legacy pending transactions, pair-level completed ledgers, group-wide 3+ alias manifests, and interrupted independent Undo records** in a separate, explicitly non-actionable history tab. `RecoveryEvidenceOverview` fails closed on unreadable families, exposes per-row manual-review status, and never interprets an empty legacy journal as whole-app safety. The target root comes from the selected folder; no root means `未検証`, not "OK". The legacy explicit journal-recovery actions remain on a distinct tab and cannot be activated from the new history view. Existing main-window completed-ledger inspection now includes group-history and in-alias interruption counts.

The group inspector no longer silently ignores **orphaned checkpoint files**. Generated-only Windows tests cover clean empty inspection (no store creation), a real synthetic three-filename group, unknown hardlinks, a missing group manifest, corrupt pair and interrupted Undo records, malformed pending journal, and full explicit Undo interruption/resume; a routed WPF Loaded event checks history tab controls. Strict Core/WPF evidence gates are added. [Details and limitations](R217_PART30_RECOVERY_OVERVIEW.md).

This part is **read-only UI + evidence safety**, not production file modification, physical device power-cut proof, arbitrary ACL/SACL/ADS/EFS, or a concurrency-safe capacity reservation. Production hardlink merge, original replacement and independent group Undo **remain disabled**; real-user capacity recovered **0 B**. 69.4% is a historical source estimate, not current release readiness. Full PR-head and separate main CI remain mandatory.

## R2.17 Part 31 — real competing process writer and staged ACL/checkpoint substitution safety

Windows test-only `R217ConcurrentWriterAclTests` now uses three **separate writer processes** with actual live Windows write handles or postscan flushed byte changes to verify the real Core rejects stale source metadata, active external writers and changed exact hashes before constructing a master hardlink. Two generated 3-file groups test true owner/group/DACL mutation **after the independent Undo stage is verified**, and deliberate corruption of the saved group checkpoint just before `File.Replace`. The Core now revalidates **both shared alias/master identities, owner+DACL/times/attributes and bytes** at the final cutover boundary and reopens the durable group checkpoint immediately before final validation; unknown changes remain ManualReview. It does not repair an outsider's ACL or record automatically. Strict Part31 Windows CI machine evidence is required. [Design and exact scope](R217_PART31_EXTERNAL_WRITER_ACL_GATES.md).

**Still not production-ready:** these checks detect only modifications observed before the final guard, not an arbitrary hostile rename/delete or privilege change *after* validation; no atomic global NTFS transaction, physical power cut, security inheritance/SACL/ADS/EFS or real-user cutover is proven. Production dedupe, source replacement and independent group Undo remain locked, 0 B actual user capacity reclaimed. Historical 69.4% is not current readiness. PR-head and postmerge main CI required.

## R2.17 Part 32 — external process pathname rename/delete and same-byte backup replacement

Five genuine independent managed processes mutate **only generated, sentinel-guarded NTFS names** at Core checkpoints. The cases move a backup after rename and plant an unrelated alias, move/replace a backup with **identical bytes but a distinct NTFS identity** immediately before irreversible delete, rename+replace a shared alias immediately after independent Undo's final validation, delete the alias name, and delete the independent stage. Core's last-backup validation and exception rollback now verify the **exact original file ID + metadata + SHA-256**, refusing to delete a swapped-in backup or rollback over untrusted evidence. The test-only Undo engine reopens stage/alias/master evidence after the external postvalidation race and fails closed before File.Replace; the group inspector reports ManualReview and keeps history untouched. Strict Part32 JSON and all earlier CI gates must pass. [Exact scenario design and limitations](R217_PART32_PATH_RACE_GATE.md).

**Not proven**: arbitrary external rename/delete after the *last* validation but before the OS pathname operation (TOCTOU), physical power loss, arbitrary owner/SACL/ADS/EFS, production safe consent, or real-user cutoff. Production hardlink merge, original image replacement and independent Undo remain **disabled**. Real recovered user capacity: **0 B**. Historical source-estimate 69.4% is not release readiness.

## R2.17 Part 33 — generated-only NTFS pinned-handle mutation feasibility

Rather than adding another path-based snapshot to Part32's final-instruction TOCTOU gap, a **strictly `CHAPPY_TESTING`-only** `SyntheticPinnedNtfsFile` prototype now opens the generated original by `CreateFileW(DELETE | FILE_READ_ATTRIBUTES)`, authenticates its NTFS volume+file ID and original SHA, and uses Microsoft's `SetFileInformationByHandle` FileRenameInfo (class 3, **ReplaceIfExists=FALSE**) or FileDispositionInfo (class 4, single independent file only). Eight controlled NTFS proofs include **four real external processes** that move/replace the source pathname (including same-SHA different NTFS ID) or plant a foreign destination after precheck, plus normal rename, single-link deletion, hardlink refusal and out-of-root refusal. Crucially, the pinned handle's target ID does not silently change when its prior pathname has acquired a foreign occupant, and the native no-replace operation must reject an occupied target.

**This is a feasibility experiment, not integrated into real Core dedupe/Undo or authorized production writing.** A user's externally relocated file would generally require ManualReview rather than a demonstration-mode handle rename, even though its identity is stable. Multi-file transactions and journal durability, directory/name rebinding, external deletion after final verification, arbitrary ACL/ADS/EFS/SACL, power loss and opt-in user consent remain release blockers. Native API support, measurements and precise limits: [Part33 contract](R217_PART33_PINNED_HANDLE_GATE.md).

Production deduplication, original media replacement and independent Undo remain disabled. **0 B real-user storage recovered**; the old 69.4% source estimate remains a historical snapshot, not product release-readiness. Require full PR-head and postmerge main Windows CI.

## R2.17 Part 34 — user-approved grouped same-name restoration/extraction specification

The future same-name handling policy now has a read-only Core plan and generated-only Windows tests: **keep the existing filename intact**, send incoming collision files to an outermost `同名ファイル` collection preserving original relative subdirectories and appending ` (1)`, ` (2)`, etc. to names. If `同名ファイル` already exists, create an `同名ファイル` **inside it** (repeat until a new ordinary directory can be created, never reuse an existing prior collection). When a collision exists, write `同名ファイル一覧.txt` at the outermost extraction/restoration root, recording original paths and actual saved paths; existing memo is preserved by numbered report names. A file blocking an intended directory is diverted to the collection, and no-collision outputs produce no memo.

`Core/CollisionCollectionPlan.Inspect` is a read-only preview. Only the `CHAPPY_TESTING` synthetic exercise materializes files on a disposable `ChappyStorageSmoke_` Windows NTFS root with CreateNew, SHA-256 checks and an UTF-8 report. Strict CI verifies old and new source hashes and old NTFS file IDs, nested same-name folders, no overwriting memo, late foreign destination refusal and path traversal/ADS refusal. [Part34 end-user behavior, file tree and limitations](R217_PART34_COLLISION_COLLECTION.md).

**Not integrated into production archive extraction or independent Undo:** the app has no active general-purpose archive decompression path in the current source, and safe multi-file journal/atomic transaction behavior is still unproven. Original JPG/PNG replacement, hardlink consolidation and independent Undo remain disabled. **0 B real-user capacity recovered**; historical 69.4% source estimate remains non-release. Full PR and separate postmerge main CI gates are required.

## R2.17 Part 35 — append-only collision history, verified explicit restart and real killed workers

The Part34 same-name nested `同名ファイル` folder and root `同名ファイル一覧.txt` now have a **generated-only crash-evidence experiment**. `CollisionRecoveryLedger` records an immutable GUID intent with each source path, original and selected destination relative paths, original ID, SHA, timestamp and owner/DACL hash **before any output write**. Each generated output is written using CreateNew + Flush(true), then receives its own immutable receipt with exact NTFS file ID, bytes, attributes, timestamps and owner/DACL proof. The UTF-8 root memo is independently receipted before a separate completion marker. An unknown staged file/receipt/temporary journal, different source/target identity, missing receipt after output Flush, or malformed record **requires manual review; no automatic cleanup or repeat overwrite**.

The Windows suite kills **three actual child processes** after a durable first receipt, between output Flush and receipt, and after the memo receipt before final completion. Only the authenticated receipt prefix can explicitly resume; an orphaned unreceipted output is preserved and blocked. Further cases modify original source, plant a foreign destination, tamper with a receipt and leave an unknown .tmp journal. Strict Part35 Windows CI report required with all earlier Core/NTFS/WPF/publish gates. [Detailed contract](R217_PART35_DURABLE_COLLISION_RECOVERY.md).

**This is not yet production archive extraction or independent Undo.** Multi-file atomic power-loss behavior, hostile journal edits, arbitrary ACL/ADS/EFS, path race defense, signed off user consent and fully safe restoration remain release blockers. All Part35 write methods are compiled out of shipped builds; hardlink merge, original image replacement and Undo remain locked. **0 B real-user storage recovered**; historical 69.4% source estimate is not readiness.

## Remaining risks / safeguards

- The **two remaining CS0162 unreachable-code warnings** are from intentional user-facing and engine-level production mutation locks; these must not be removed to silence diagnostics.
- Only generated synthetic data used in CI. No GUI interactive tests or original personal photos/videos were used.
- PNG/JPG previews are output candidates. Physical disk capacity recovered from end-user media has **not been measured**.
- Original replacement and hardlink consolidation remain **DISABLED** in ordinary app builds.
- Real NTFS crash/power-loss resilience, ADS, ACL, simultaneous mutation and test-drive performance still require controlled evaluation.
- `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip` is retained for audit as a pinned historical source; the tracked tree is now the intended editable source.
- No user media, local diagnostic logs, credentials or private backups are included.

## Next gate

1. **Completed:** merge R2.7's directly tracked source without relaxing mutation locks. Validate the merged `main` CI.
2. Check historical static scripts and update CI to run only against reviewed source, reducing the old pinned ZIP path to archival use.
3. **R2.8–R2.10 [Issue #2](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2):** synthetic allocation, process-kill recovery and independent hardlink detach passed; VHDX, true OS/power interruption, ACL/ownership/ADS and production app integration remain open.
4. Establish a release gate for future opt-in mutations only after rigorous rollback and on-disk metadata verification.

Source changes: [PR #1](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/1).
