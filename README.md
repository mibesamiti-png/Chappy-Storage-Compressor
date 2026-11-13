# Chappy Storage Compressor

Windows向けの **JPG・PNGの完全無劣化最適化候補生成** と **完全一致ファイルの重複検出** を行う開発中の C# / WPF ツールです。

> ⚠️ **試作版：大切な写真・動画の原本では使用しないでください。** 通常版のハードリンク統合・原本置換は無効です。Windowsでビルド・合成データによる安全テストに合格したことは、本番でのデータ安全性や容量回復を保証しません。

## R2.6 Windows CI — 初の本体ビルド成功

[成功したGitHub Actions実行](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842)

| 項目 | 結果 |
|---|---|
| Windows / .NET 10 WPF 本体ビルド | **PASS（0 errors / 6 warnings）** |
| C# 合成データ安全試験 | **359 / 359 PASS** |
| 自己完結型 win-x64 EXE publish | **PASS** |
| 基礎NTFSハードリンク・復旧テスト | **PASS** |
| 本番データの安全性・GUI手動試験 | **未検証** |
| 実際に回復したディスク容量 | **未検証（実測値なし）** |

- アップロードされたソースキット: `Chappy_Storage_Compressor_R2_6_REDOWNLOAD.zip`（233,860 bytes、SHA-256ピン留め済み）
- CI成果物: [chappy-win-x64-unverified](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842/artifacts/14061958635) ／ [診断ログ](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43474468842/artifacts/14063440051)
- CIはZIPを検証・展開してテストを実行します。C#のソースツリーは今後Gitへ直接取り込む予定です。
- ZIP本体にはまだ取り込まれていない**CI限定の試験修正**があります。テストプロジェクトへの共有global using追加と、テスト専用 `PrepareTestDirectory` のsentinelファイルハンドル解放です。後続のソース更新で正式反映します。

## 今後の優先事項

1. 本体C#のソースツリーをZIPからGit管理へ移行し、CI限定修正を正式ソースへ反映する。
2. 警告（特にPNGの `CA2014` stackalloc-in-loop）を調査し修正する。
3. PNG/JPG無劣化候補の互換性・メタデータ・破損時挙動を検証する。
4. 独立バックアップ・クラッシュ復旧・復元可能性をさらに検証してから容量回復機能の実装ゲートを判断する。

公開リポジトリに個人の写真・動画、固有パス、デバッグログ、認証情報を登録しないでください。進捗の正本は [docs/ROADMAP_STATUS.md](docs/ROADMAP_STATUS.md) です。
