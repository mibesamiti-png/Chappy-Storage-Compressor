using System.Security.Cryptography;
using ChappyStorageCompressor.Core;

internal static class R215ReconciledCapacityTests
{
    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        // Only generated data under the existing synthetic Windows smoke root.
        string space = Path.Combine(parent, "r215-media");
        string input = Path.Combine(space, "input");
        string output = Path.Combine(space, "candidate-records");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        try
        {
            static void NewPair(string root, string first, string second, int size)
            {
                byte[] bytes = RandomNumberGenerator.GetBytes(size);
                string a = Path.Combine(root, first), b = Path.Combine(root, second);
                File.WriteAllBytes(a, bytes);
                File.WriteAllBytes(b, bytes);
                File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            }

            NewPair(input, "a.jpg", "b.jpeg", 100);
            byte[] jpg = File.ReadAllBytes(Path.Combine(input, "a.jpg"));
            string jpg3 = Path.Combine(input, "c.jpg");
            File.WriteAllBytes(jpg3, jpg);
            File.SetCreationTimeUtc(jpg3,
                File.GetCreationTimeUtc(Path.Combine(input, "a.jpg")));
            File.SetLastWriteTimeUtc(jpg3,
                File.GetLastWriteTimeUtc(Path.Combine(input, "a.jpg")));

            NewPair(input, "png-1.png", "png-2.png", 200);
            NewPair(input, "movie-1.mp4", "movie-2.mp4", 300);
            File.WriteAllBytes(Path.Combine(input, "single.jpg"),
                RandomNumberGenerator.GetBytes(90));
            File.WriteAllBytes(Path.Combine(input, "single.png"),
                RandomNumberGenerator.GetBytes(80));

            var scan = Scanner.Run(input, true, null, CancellationToken.None);
            check(scan.Groups.Count == 3 &&
                  scan.Groups.All(g => g.BlockReason(false) == null),
                "R2.15: JPG PNG MP4の3件の安全な完全一致グループを検出");
            var rows = new[] {
                new CombinedCapacityReport.Row("JPG","a.jpg","Completed",100,80,20,"synthetic"),
                new CombinedCapacityReport.Row("JPG","b.jpeg","Completed",100,80,20,"synthetic"),
                new CombinedCapacityReport.Row("PNG","png-1.png","CandidatePresent",200,160,40,"synthetic"),
                new CombinedCapacityReport.Row("PNG","png-2.png","CandidatePresent",200,160,40,"synthetic"),
                new CombinedCapacityReport.Row("JPG","single.jpg","Completed",90,80,10,"synthetic"),
                new CombinedCapacityReport.Row("PNG","single.png","CandidatePresent",80,60,20,"synthetic"),
                new CombinedCapacityReport.Row("JPG","movie-1.mp4","Completed",300,0,300,"invalid extension"),
                new CombinedCapacityReport.Row("PNG","single.png","Failed",80,0,80,"unsafe state"),
                new CombinedCapacityReport.Row("JPG","../escape.jpg","Completed",100,0,100,"escape")
            };
            var preview = new CombinedCapacityReport.Summary(3, 3, 6, 0,
                150, 0, rows);
            var r = CombinedSavingsPlanner.Analyze(preview, scan, false);
            check(r.PreviewOnlyBytes == 150 &&
                  r.DedupeOnlyBytes == 700 &&
                  r.DoubleCountAvoidedBytes == 60 &&
                  r.CombinedHypotheticalBytes == 790 &&
                  r.ActualFreedBytes == 0,
                "R2.15: 削減見込150+700-60=790B、実回復0B");
            check(r.VerifiedDuplicateGroups == 3 && r.SkippedDuplicateGroups == 0,
                "R2.15: 重複3グループを改めて完全照合");
            var perFormat = r.Formats.ToDictionary(x => x.Format);
            check(perFormat["MP4"].DedupeOnlyBytes == 300 &&
                  perFormat["MP4"].PreviewOnlyBytes == 0 &&
                  perFormat["MP4"].CombinedHypotheticalBytes == 300,
                "R2.15: MP4は完全一致重複のみ計上し再エンコードなし");
            check(perFormat["JPG"].PreviewOnlyBytes == 50 &&
                  perFormat["JPG"].DedupeOnlyBytes == 200 &&
                  perFormat["JPG"].DoubleCountAvoidedBytes == 20 &&
                  perFormat["JPG"].CombinedHypotheticalBytes == 230,
                "R2.15: JPGの重複×候補の重なりを20B補正");
            check(perFormat["PNG"].PreviewOnlyBytes == 100 &&
                  perFormat["PNG"].DedupeOnlyBytes == 200 &&
                  perFormat["PNG"].DoubleCountAvoidedBytes == 40 &&
                  perFormat["PNG"].CombinedHypotheticalBytes == 260,
                "R2.15: PNGの重複×候補の重なりを40B補正");
            check(r.Formats.Sum(x => x.CombinedHypotheticalBytes) == r.CombinedHypotheticalBytes,
                "R2.15: 形式別の補正合計は全体見込と一致");

            string csv = Path.Combine(space, "nonoverlap.csv");
            CombinedSavingsPlanner.ExportFormatCsv(r, input, output, csv);
            string actualCsv = File.ReadAllText(csv);
            check(actualCsv.Contains("OverlapAvoidedBytes", StringComparison.Ordinal) &&
                  actualCsv.Contains("\"JPG\"", StringComparison.Ordinal) &&
                  actualCsv.Contains("\"MP4\"", StringComparison.Ordinal) &&
                  actualCsv.Contains("\"790\"", StringComparison.Ordinal) == false,
                "R2.15: CSVの形式別補正と根拠を保存、全体値を各行で重複記載しない");
            expectThrows(() => CombinedSavingsPlanner.ExportFormatCsv(r, input, output, csv),
                "R2.15: 既存の統合見込CSVの上書きを拒否");
            expectThrows(() => CombinedSavingsPlanner.ExportFormatCsv(r, input, output,
                Path.Combine(input, "unsafe.csv")),
                "R2.15: 原本フォルダ内へのCSV出力を拒否");
            expectThrows(() => CombinedSavingsPlanner.ExportFormatCsv(r, input, output,
                Path.Combine(output, "unsafe.csv")),
                "R2.15: 候補保存先内へのCSV出力を拒否");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool interrupted = false;
            try { CombinedSavingsPlanner.Analyze(preview, scan, false, canceled.Token); }
            catch (OperationCanceledException) { interrupted = true; }
            check(interrupted, "R2.15: 大量ファイルの重複再照合はキャンセル可能");

            string alias = Path.Combine(input, "extra-link.jpg");
            NativeFile.NewHardLink(alias, Path.Combine(input, "a.jpg"));
            var linked = CombinedSavingsPlanner.Analyze(preview, scan, false);
            check(linked.SkippedDuplicateGroups == 1 &&
                  linked.VerifiedDuplicateGroups == 2 &&
                  linked.DedupeOnlyBytes == 500 &&
                  linked.DoubleCountAvoidedBytes == 40 &&
                  linked.PreviewOnlyBytes == 130,
                "R2.15: スキャン後に増えた外部ハードリンクは重複・候補の両方から除外");
            File.Delete(alias);

            File.WriteAllBytes(Path.Combine(input, "movie-2.mp4"),
                RandomNumberGenerator.GetBytes(300));
            var changed = CombinedSavingsPlanner.Analyze(preview, scan, false);
            check(changed.SkippedDuplicateGroups == 1 &&
                  changed.DedupeOnlyBytes == 400 &&
                  changed.Formats.Single(x => x.Format == "MP4").SkippedDuplicateGroups == 1,
                "R2.15: 同一サイズでも内容変更したMP4を見込から除外");
        }
        finally
        {
            Directory.Delete(space, recursive: true);
        }
    }
}
