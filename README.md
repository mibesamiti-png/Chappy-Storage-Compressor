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

## 開発の安全ゲート

1. Windows CIでビルド・合成ファイルの安全テストをすべて通す。
2. ファイルサイズ・バイト内容だけでなくファイルID、ACL、ADS、ハードリンク状態、同時変更の危険性を検証する。
3. 元の写真・動画を削除・上書きする機能は本番検証完了まで**無効**とする。
4. 容量回復は実測の空き容量として報告し、圧縮候補の削減見込みと区別する。

公開リポジトリに個人の写真・動画、ユーザー固有パス、実行ログ、認証情報、バックアップを追加しないでください。旧 `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip` は照合用アーカイブで、これからの正本はGit管理されたC#ソースです。
