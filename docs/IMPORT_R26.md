# R2.6のソースをWindows CIへ登録する方法

GitHubリポジトリの初期設定とWindowsコンパイラ検証は完了しています。R2.6の本体を試験するには、**R2.6のソースZIPを1つ**公開リポジトリのルートに登録してください。

## GitHub Web UI （最短）

1. ChatGPTで提供済みの `ChappyStorageCompressor_R2_6_SourceKit.zip`（233,860 bytes）をダウンロード。
2. [このリポジトリ](https://github.com/mibesamiti-png/Chappy-Storage-Compressor) で **Add file → Upload files** を開く。
3. ZIPを**展開せずに**ルートへドラッグ＆ドロップし、mainへコミット。
4. [Actions](https://github.com/mibesamiti-png/Chappy-Storage-Compressor/actions) の新しい `Windows build and safety smoke tests` を確認。

ZIPの検証用SHA-256:
```
a2b3ed077deb0a5be856a57ab32e39ebfcd0b650c572e7923dc974b4d0fff5ec
```

GitHub Actionsは正しいZIPを検証後、 `sourcekit/ChappyStorageCompressor_R2_6/` に展開し、`RUN_DIAGNOSTICS.bat` を使ってWPFビルド、合成NTFSスモークテスト、EXE publishを実行します。エラーでも `chappy-windows-diagnostics` をアップロードします。全工程合格時に限り `chappy-win-x64-unverified` を作成します。

**注意**: ZIPに含まれるコードが一般利用で安全という意味ではありません。大切な原本に適用しないでください。ソースを公開することを承認済みですが、個人の写真・動画、ログや実データは絶対にアップロードしないでください。

## 実装ソースを直接Git管理したい場合

ZIP一個の登録はビルド検証に必要な最小作業です。追って `Core/`、`tests/`、`tools/`、WPFのXAML/.csprojなどを直接Git追跡する形式に移行するのがおすすめです。CIはルートに `ChappyStorageCompressor.csproj` が存在する場合、ZIPよりソースツリーを優先します。
