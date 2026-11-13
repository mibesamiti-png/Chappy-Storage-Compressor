# Chappy Storage Compressor

Windows向け **完全一致ファイルの重複調査** と **PNG/JPG完全無劣化の圧縮候補生成** を行う開発中のC# / WPFアプリ。

> ⚠️ **開発中です。大切な写真・動画の原本には使用しないでください。** 通常版のハードリンク統合・原本置換は安全のため無効です。GitHub Actionsの合格は製品の安全性保証ではありません。

## R2.7：ソースを直接GitHub管理

従来はZIP内のC#を展開し、CI上で一時修正を加えてテストしていました。R2.7では `Core/`、`tests/`、`tools/` とWPFソースをGitで直接管理してCIで検証しています。

- Windows .NET 10 WPFビルド **成功（エラー0件、警告2件）**
- 合成ファイルを使ったC#安全テスト **359/359成功**
- Windows x64単一EXE発行 **成功**
- PNG解析の `CA2014` ループ内スタック使用警告 **4件修正**
- テスト側の共有インポート・ファイルハンドルの閉じ忘れ **正式ソースで修正**
- **本番のディスク容量回復は未検証**、原本置換・ハードリンク統合は引き続き無効

実行結果： [R2.7 Windows CI](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43479758919)。
進捗：[docs/ROADMAP_STATUS.md](docs/ROADMAP_STATUS.md)。
変更履歴：[PR #1](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/1)。

## R2.8：合成NTFSデータの容量回復を初めて実測

- Windows GitHub Actions上で16MiBずつの完全一致ファイル2個を作成し、ハードリンクによる重複統合後の**実測空き容量増加16MiB**を確認。
- 一意なNTFSファイルIDの物理割当量は**32MiB → 16MiB**。ただし実測値はCI環境固有で、他のドライブで同じ値になる保証はありません。
- 意図的に非圧縮で生成したPNGは**1,049,371B → 3,596B**。画像のscanlineと非IDATメタデータの完全一致、独立バックアップからのバイト完全復元を確認。
- 既存C#安全テスト**359/359成功**、WPFビルドとEXE発行も成功。
- **実際の写真・動画の容量回復は未検証**。バックアップを保持している間は圧縮しただけで空き容量が増えるとは限りません。

[実測検証結果](docs/R28_VERIFIED_RESULTS.md) / [未完了の安全ゲート](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/2)

## R2.9：プロセス強制終了と復旧

合成ファイルの処理中に別のWindowsプロセスを実際に強制終了させ、復旧用ジャーナルの再実行を検証しました。

- **9/9** 強制終了シナリオ、**7/7** SHA-256完全復元、**6/6** 危険操作の拒否
- バックアップ改変、共有ハードリンク、書き込みロック、NTFSの代替ストリーム、読み取り専用などを検査
- これは**プロセス終了試験**で、実際の電源断やディスク書き込みキャッシュの耐久性は未検証

[検証記録](docs/R29_PROCESS_CRASH_GATE.md) / [R2.9の統合PR #4](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/4)

## R2.10：重複統合したハードリンクを再び独立化する試験

4MiBの生成ファイル2つをNTFSの同じファイルIDで共有させた後、片方を別ファイルIDへ戻しました。

- **4/4** 強制終了地点、**4/4** 独立ファイル復元、**4/4** 独立した編集、**7/7** 危険操作の拒否
- 解除後も内容のSHA-256と通常属性・作成/更新日時を検証
- **独立化には追加のディスク容量が必要**。削減ではなく、独立した書き込み動作を取り戻す処理
- 本番用の統合解除やACL・所有者・特殊ストリームの復元は未実装。試験コードは`bootstrap/`内に隔離

[検証記録](docs/R210_INDEPENDENT_UNDO_GATE.md) / [R3本番機能の未完了条件](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/issues/6)

## R2.11：NTFSの所有者・アクセス権・ADSを検査

Windowsの使い捨て合成ファイルについて、**SHA-256が同じでも、所有者・DACL、NTFSファイルID、代替データストリーム等が変更された場合は処理を拒否**する検証を追加。

- 正常事前検証 **7/7成功**、危険条件 **7/7拒否**
- ファイルのDACL変更 **2/2検出**、NTFS ADS追加 **1/1検出**
- 元ファイルのデータはすべての拒否試験で変更なし
- セキュリティ情報の詳細は公開CIログへ出力しない
- **本番ファイルでのACL/所有者の完全な保存・復元は未検証**。元データを変更する機能は無効

[Windows CI証拠](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43501972438) / [詳細検証記録](docs/R211_NTFS_METADATA_GUARD.md) / [統合PR #7](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/7)

## R2.12：所有者・アクセス権を維持する置換と復元の合成試験

- Windowsの合成NTFSファイルで、元ファイルと異なるアクセス権を持つステージへ所有者・DACLを引き継ぎ、**置換後のデータと権限が一致することを検証**。
- 別の独立バックアップから元のSHA-256・所有者・DACLを一致させて復元する試験も成功。
- **1/1 置換成功・1/1 完全復元・5/5 危険な変更の拒否**。
- これは同一Windows実行アカウント上の合成データの検証。**異なる所有者の復元、任意のACL、SACL、ADS、EFS、実際の電源断、利用者ファイルの安全性は未検証**。
- 通常のアプリでの重複統合・原本置換・統合解除は引き続き無効。

[Windows CI検証結果](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43516585860) / [R2.12検証記録](docs/R212_SECURITY_PRESERVATION_GATE.md) / [統合PR #8](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/pull/8)

## 開発の安全ゲート

1. Windows CIでビルド・合成ファイルの安全テストをすべて通す。
2. ファイルサイズ・バイト内容だけでなくファイルID、ACL、ADS、ハードリンク状態、同時変更の危険性を検証する。
3. 元の写真・動画を削除・上書きする機能は本番検証完了まで**無効**とする。
4. 容量回復は実測の空き容量として報告し、圧縮候補の削減見込みと区別する。

公開リポジトリに個人の写真・動画、ユーザー固有パス、実行ログ、認証情報、バックアップを追加しないでください。旧 `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip` は照合用アーカイブで、これからの正本はGit管理されたC#ソースです。
