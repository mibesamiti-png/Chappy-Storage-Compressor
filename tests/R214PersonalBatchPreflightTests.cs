using ChappyStorageCompressor.Core;

internal static class R214PersonalBatchPreflightTests
{
    public static void Run(string testRoot, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        // Only disposable files created beneath the isolated smoke-test root.
        string parent = Path.Combine(testRoot, "r214-combined-only");
        string input = Path.Combine(parent, "input");
        string output = Path.Combine(parent, "candidate-output");
        string nested = Path.Combine(input, "nested");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllBytes(Path.Combine(input, "a.png"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(input, "b.jpg"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(nested, "c.JPEG"), new byte[3072]);
            File.WriteAllBytes(Path.Combine(input, "movie.mp4"), new byte[8192]);
            string special = Path.Combine(input, "read-only.png");
            File.WriteAllBytes(special, new byte[4096]);
            File.SetAttributes(special, FileAttributes.ReadOnly);
            string zero = Path.Combine(input, "empty.png");
            File.WriteAllBytes(zero, Array.Empty<byte>());

            var report = PersonalMediaBatchPreflight.Inspect(input, output);
            check(report.PngFiles == 1 && report.JpegFiles == 2 &&
                  report.TotalFiles == 3,
                "R2.14: PNG + JPG/JPEGを一括事前診断しMP4は変換しない");
            check(report.PngSourceBytes == 1024 &&
                  report.JpegSourceBytes == 2048 + 3072 &&
                  report.TotalSourceBytes == 6144,
                "R2.14: 入力データ量の実測合計とファイル件数が一致");
            check(report.SkippedSpecialFiles == 2 && report.ScanErrors == 0 &&
                  report.ActualFreedBytes == 0,
                "R2.14: 読み取り専用・ゼロ長を候補外にして実空き回復は0B");
            check(report.OutputAvailableBytes >= 0,
                "R2.14: 候補保存先の空き容量を数値として表示できる");
            check(File.ReadAllBytes(Path.Combine(input, "movie.mp4")).Length == 8192 &&
                  File.GetAttributes(special).HasFlag(FileAttributes.ReadOnly) &&
                  !Directory.EnumerateFileSystemEntries(output).Any(),
                "R2.14: 事前調査は原本・MP4・候補フォルダを変更しない");
            expectThrows(() => PersonalMediaBatchPreflight.Inspect(input, input),
                "R2.14: 入力と出力が同一フォルダなら拒否");
            expectThrows(() => PersonalMediaBatchPreflight.Inspect(input, nested),
                "R2.14: 候補保存先が入力の内側なら拒否");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool cancelled = false;
            try { PersonalMediaBatchPreflight.Inspect(input, output, canceled.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "R2.14: 大量フォルダ調査のキャンセルに対応");
        }
        finally
        {
            string special = Path.Combine(input, "read-only.png");
            if (File.Exists(special)) File.SetAttributes(special, FileAttributes.Normal);
            Directory.Delete(parent, recursive: true);
        }
    }
}
