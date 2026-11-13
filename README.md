# Chappy Storage Compressor

Windows向けの **JPG/PNG完全無劣化最適化 + 完全一致ファイルの重複調査** を行う開発中のツールです。

> ⚠ **試作段階。大切な写真・動画の原本で使用しないでください。** 現時点でソースのWindows/.NETビルドとNTFSの実行検証は未完了で、通常版のハードリンク統合と原本置換は無効です。

## 現在のベース

- ローカルで作成した最新スナップショット: **R2.6**
- .NET 10 / Windows WPF / C#
- 原本を変更しない読み取り専用分析、PNG/JPGの別ファイル候補生成
- R2.6は隔離合成ファイルによる復旧実験を含みますが、**通常版では無効**
- GitHub ActionsのWindows/.NET 10環境と、使い捨てNTFSテスト（ハードリンク・バックアップ付き置換・復元）は検証済み。**R2.6アプリ本体のビルドとテストはソース未登録のため未実施**。

## ソースの登録

現時点のGitHubリポジトリはCI導入用のブートストラップです。**R2.6のソース本体はまだこのリポジトリに登録されていません。** 実際のC#ビルドに必要なのは、SourceKit内の `ChappyStorageCompressor.csproj`、`Core/`、`tests/`、`tools/`、GUIのXAML群です。

公開リポジトリに写真・動画の実データ、ユーザー固有のファイルパス、診断ログ、認証情報は追加しないでください。

## 開発のゲート

1. Windowsで `dotnet build` / `dotnet run --project tests` が成功すること
2. 合成ファイルでNTFSのメタデータと復旧処理が合格すること
3. 原本は一切変更せず、候補に対する可逆性を確認すること
4. 実際の容量回復は、独立バックアップ・クラッシュ復旧の実証後に別ゲートとして解放すること

**Windowsの独立NTFSテスト結果：** [成功したCI](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions/runs/43470917936)。このテストは`bootstrap/NtfsSafetyProbe.cs`にあり、原本・個人データを読み込まず、Windows TEMPに作成した合成ファイルのみを操作します。R2.6の本体テストに代わるものではありません。

詳細な実装進捗と、実機検証済み項目の区別は `docs/ROADMAP_STATUS.md` に記録しています。
