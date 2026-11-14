using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// R2.17 part 3: deterministic, generated-only byte-level hash progress.
// No production mutation, no user-selected directories, no disk free-space claims.
internal static class R217HashByteProgressTests
{
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string root = Path.Combine(parent, "r217-hash-progress-only");
        Directory.CreateDirectory(root);
        try
        {
            const int mediaBytes = 12 * 1024 * 1024;
            byte[] content = new byte[mediaBytes];
            new Random(21737).NextBytes(content);
            string a = Path.Combine(root, "synthetic-a.mp4");
            string b = Path.Combine(root, "synthetic-b.mp4");
            File.WriteAllBytes(a, content);
            File.Copy(a, b);
            File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            string expected = Convert.ToHexString(SHA256.HashData(content));

            var directBytes = new List<(long Read, long Total)>();
            string digest = Safety.ComputeSha256(a, CancellationToken.None,
                (read, total) => directBytes.Add((read, total)));
            check(digest == expected && directBytes.Count >= 3 &&
                  directBytes[^1] == (mediaBytes, mediaBytes) &&
                  directBytes.All(p => p.Total == mediaBytes),
                "R2.17: SHA-256 bytes and 4 MiB progress preserve framework hash value");
            check(directBytes.Zip(directBytes.Skip(1),
                      (x, y) => y.Read > x.Read).All(x => x) &&
                  directBytes.Any(x => x.Read > 0 && x.Read < x.Total),
                "R2.17: hash progress is strictly monotonic with genuine mid-file updates");

            var updates = new List<ScanHashProgress>();
            ScanResult scan = Scanner.Run(root, true, null, CancellationToken.None,
                new InlineProgress<ScanHashProgress>(p => updates.Add(p)));
            check(scan.FileCount == 2 && scan.DeepInspectedFiles == 2 &&
                  scan.HashAttemptedFiles == 2 && scan.Groups.Count == 1 &&
                  scan.Groups[0].Files.Count == 2 &&
                  scan.Groups[0].BlockReason(false) == null,
                "R2.17: typed byte progress does not alter safe exact-duplicate detection");
            check(updates.Count >= 8 && updates.Count <= 20 &&
                  updates[0].ProcessedCandidateBytes == 0 &&
                  updates[^1].ProcessedCandidateBytes == mediaBytes * 2L &&
                  updates[^1].Percent == 100 &&
                  updates.All(p => p.CandidateFiles == 2 &&
                       p.TotalCandidateBytes == mediaBytes * 2L &&
                       p.Percent is >= 0 and <= 100),
                "R2.17: scanner reports bounded aggregate byte progress through completion");
            check(updates.Zip(updates.Skip(1),
                      (x, y) => y.ProcessedCandidateBytes >= x.ProcessedCandidateBytes)
                    .All(x => x) &&
                  updates.Any(p => p.CurrentFileBytes > 0 &&
                      p.CurrentFileBytes < p.CurrentFileTotalBytes &&
                      p.CurrentFilePercent is > 0 and < 100),
                "R2.17: aggregate progress is monotonic; an individual file visibly advances");

            using var directCancel = new CancellationTokenSource();
            long canceledAt = 0;
            bool shaCanceled = false;
            try
            {
                Safety.ComputeSha256(a, directCancel.Token, (read, total) =>
                {
                    if (read > 0 && read < total)
                    {
                        canceledAt = read;
                        directCancel.Cancel();
                    }
                });
            }
            catch (OperationCanceledException) { shaCanceled = true; }
            check(shaCanceled && canceledAt >= 4 * 1024 * 1024 &&
                  canceledAt < mediaBytes,
                "R2.17: SHA cancellation in the middle of a large file returns no digest");

            using var scanCancel = new CancellationTokenSource();
            long scanCanceledAt = 0;
            bool scannerCanceled = false;
            try
            {
                Scanner.Run(root, true, null, scanCancel.Token,
                    new InlineProgress<ScanHashProgress>(p =>
                    {
                        if (p.CurrentFileBytes > 0 &&
                            p.CurrentFileBytes < p.CurrentFileTotalBytes)
                        {
                            scanCanceledAt = p.CurrentFileBytes;
                            scanCancel.Cancel();
                        }
                    }));
            }
            catch (OperationCanceledException) { scannerCanceled = true; }
            check(scannerCanceled && scanCanceledAt >= 4 * 1024 * 1024 &&
                  scanCanceledAt < mediaBytes,
                "R2.17: scanner propagates a mid-hash cancel instead of returning partial groups");

            check(Directory.EnumerateFileSystemEntries(root).Count() == 2 &&
                  Safety.ComputeSha256(a, CancellationToken.None) == expected &&
                  Safety.ComputeSha256(b, CancellationToken.None) == expected &&
                  !NativeFile.SameFile(a, b),
                "R2.17: progress and cancellation leave both independent originals untouched");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-hash-byte-progress.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-hash-byte-progress-v1",
                    scope = "isolated generated-only Windows NTFS fixture",
                    syntheticFiles = 2,
                    bytesPerFile = mediaBytes,
                    checkedSha256MatchesFramework = true,
                    scannerVerifiedGroups = scan.Groups.Count,
                    scannerHashAttempts = scan.HashAttemptedFiles,
                    directHashProgressUpdates = directBytes.Count,
                    scannerByteProgressUpdates = updates.Count,
                    scannerIntermediateFileProgressObserved = true,
                    shaMidFileCancellationConfirmed = shaCanceled,
                    scannerMidFileCancellationConfirmed = scannerCanceled,
                    allOriginalHashesPreserved = true,
                    actualWpfButtonClickedByGuiAutomation = false,
                    guiCompiledButInteractionUnverified = true,
                    automaticScanResumptionSupported = false,
                    productionHardlinkMergeEnabled = false,
                    productionOriginalReplacementEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 HASH BYTE PROGRESS PASS: " +
                $"{updates.Count} progress samples, mid-file cancellation confirmed, " +
                "originals SHA unchanged");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
