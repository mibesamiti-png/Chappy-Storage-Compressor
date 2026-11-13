# R2.6 Windowsテスト計画 — late-stage integrity gate

## 対象

`CHAPPY_TESTING` 限定の合成ファイル `SyntheticReplacementExperiment` のみ。通常のWPFアプリは実験コードをコンパイルせず、JPG/PNG/MP4の個人原本に変更を加えません。

## R2.5から発見した問題

以前の`Begin`は、staging candidate（`.r23-stage.bin`）とexternal backup（`.r23-external-copy.bin`）を初期コピー時に検査する一方、`File.Replace`直前に再検証していませんでした。この間に別プロセスがstaging dataを変更すると、想定外の内容への置換が生じ得ます。また、ハッシュ検査中の同じバイト列を持つ別実体への置換は、単純なハッシュ比較だけでは識別できません。

## Windows NTFS スモーク試験（コード追加、未実行）

| 試験名 | 注入タイミング | 期待結果 |
|---|---|---|
| `r26-late-stage-bytes` | 最終プリフライト前にステージを同サイズの別データに更新 | 原本のファイルID・内容は不変、ジャーナル保持、切替未実行 |
| `r26-late-backup-bytes` | 最終プリフライト前にバックアップを同サイズの別データに更新 | 切替未実行、原本は不変 |
| `r26-late-stage-hardlink` | 最終プリフライト前にステージの別名ハードリンクを作成 | 共有ファイルを危険な候補として拒否 |
| `r26-same-bytes-different-identity` | SHA-256計算直後に外部コピーを別ファイルへすり替え | 同一ハッシュでもファイルIDの変化を検出して拒否 |

**実行方法：**Windows 10/11、ローカルNTFS、.NET 10 SDK を用意して、解凍フォルダで `RUN_DIAGNOSTICS.bat`。本試験はTEMP内で生成される`.bin`ファイルのみ操作します。`diagnostics/RESULT.txt` がPASSでも本番適用は不可です。

## 独立参考検証

- `python tools/r26_contract_check.py`：27/27 静的ソース構成チェック。C#の文法/コンパイルは確認しません。
- `python tools/r26_reference_check.py`：10/10 合成POSIXファイルテスト。Windows NTFSの動作を証明しません。

## 未達成の安全ゲート

- NTFS Windows実行テストおよびC#コンパイル
- 最終チェック後のTOCTOU競合の排除（ファイルIDに結び付けたハンドル/排他機構）
- 実メディアの可逆性、メタデータ、ACL、ADSの保存検証
- 別物理装置への独立バックアップと電源断復旧
- 実際の空き容量回復（現在0B）
