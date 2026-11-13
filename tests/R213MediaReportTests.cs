using System.Security.Cryptography;
using ChappyStorageCompressor.Core;

internal static class R213MediaReportTests
{
    public static void Run(string parent, Action<bool, string> check,
        Action<Action, string> expectThrows)
    {
        string root = Path.Combine(parent, "r213-only-personal-media");
        Directory.CreateDirectory(root);
        try
        {
            static void Pair(string dir, string aName, string bName, int length,
                bool mismatchTime = false)
            {
                var bytes = RandomNumberGenerator.GetBytes(length);
                string a = Path.Combine(dir, aName), b = Path.Combine(dir, bName);
                File.WriteAllBytes(a, bytes);
                File.WriteAllBytes(b, bytes);
                File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
                if (mismatchTime) File.SetLastWriteTimeUtc(b,
                    File.GetLastWriteTimeUtc(a).AddDays(-2));
            }

            Pair(root, "film1.mp4", "film2.mp4", 4096);
            Pair(root, "picture1.png", "picture2.png", 3072);
            Pair(root, "=HYPERLINK.jpg", "photo.jpeg", 2048);
            Pair(root, "older1.mp4", "older2.mp4", 1536, mismatchTime: true);
            var scan = Scanner.Run(root, true, null, CancellationToken.None);
            check(scan.Groups.Count == 4, "R2.13: MP4/JPG/PNGと日時差候補を読み取り専用検出");
            var report = MediaDuplicateReport.Analyze(scan, false);
            check(report.InspectedGroups == 4 && report.EligibleGroups == 3 &&
                  report.BlockedGroups == 1 && report.ChangedGroups == 0,
                "R2.13: 日時差のある候補を除外して3グループ算定");
            check(report.PotentialBytes == 4096 + 3072 + 2048 &&
                  report.ActualFreedBytes == 0,
                "R2.13: 論理見込と実回復0バイトを区別");
            check(report.Formats.Single(x => x.Format == "MP4").PotentialBytes == 4096 &&
                  report.Formats.Single(x => x.Format == "PNG").PotentialBytes == 3072 &&
                  report.Formats.Single(x => x.Format == "JPG").PotentialBytes == 2048,
                "R2.13: MP4/JPG/JPEG/PNGの拡張子別容量を重複計上しない");
            check(report.Rows.Where(x => x.GroupPotentialBytes > 0)
                      .Sum(x => x.GroupPotentialBytes) == report.PotentialBytes,
                "R2.13: CSV各行の理論削減量を足しても二重計上しない");

            string csv = Path.Combine(parent, "r213-duplicates.csv");
            MediaDuplicateReport.ExportCsv(report, root, csv);
            string output = File.ReadAllText(csv);
            check(output.Contains("'=HYPERLINK.jpg", StringComparison.Ordinal) &&
                  output.Contains("GroupEstimatedSavingBytes", StringComparison.Ordinal),
                "R2.13: CSVの先頭記号による表計算式注入を無効化");
            expectThrows(() => MediaDuplicateReport.ExportCsv(report, root,
                Path.Combine(root, "disallowed.csv")),
                "R2.13: 原本スキャンフォルダ内へのCSV保存を拒否");
            expectThrows(() => MediaDuplicateReport.ExportCsv(report, root, csv),
                "R2.13: 既存CSVを上書きしない");

            File.WriteAllBytes(Path.Combine(root, "film2.mp4"),
                RandomNumberGenerator.GetBytes(4096));
            var stale = MediaDuplicateReport.Analyze(scan, false);
            check(stale.ChangedGroups == 1 && stale.EligibleGroups == 2 &&
                  stale.PotentialBytes == 3072 + 2048,
                "R2.13: 再照合時にスキャン後のMP4変更を検出して見込から除外");
            var permissive = MediaDuplicateReport.Analyze(scan, true);
            check(permissive.EligibleGroups == 3 && permissive.ChangedGroups == 1 &&
                  permissive.PotentialBytes == 3072 + 2048 + 1536,
                "R2.13: 日時差許容は既存の明示設定時のみ反映");

            string newLink = Path.Combine(root, "unexpected-hardlink.jpg");
            NativeFile.NewHardLink(newLink, Path.Combine(root, "=HYPERLINK.jpg"));
            var linked = MediaDuplicateReport.Analyze(scan, false);
            check(linked.ChangedGroups == 2 && linked.EligibleGroups == 1 &&
                  linked.PotentialBytes == 3072,
                "R2.13: スキャン後に追加された外部ハードリンクを見込から除外");
            File.Delete(newLink);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
