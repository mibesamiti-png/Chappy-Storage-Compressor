using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

internal static class R216MediaScannerBenchmarks
{
    public static void Run(string parent, Action<bool, string> check)
    {
        // All inputs are disposable synthetic data created by the Windows smoke
        // suite, never a user-selected path or original photo/video.
        string dir = Path.Combine(parent, "r216-two-stage-only");
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 360; i++)
            {
                string extension = (i % 3) switch { 0 => ".jpg", 1 => ".png", _ => ".mp4" };
                File.WriteAllBytes(Path.Combine(dir, $"unique-{i:D4}{extension}"),
                    RandomNumberGenerator.GetBytes(512 + i));
            }

            static void Pair(string root, string first, string second, int len,
                bool identical = true)
            {
                string a = Path.Combine(root, first), b = Path.Combine(root, second);
                byte[] bytes = RandomNumberGenerator.GetBytes(len);
                File.WriteAllBytes(a, bytes);
                File.WriteAllBytes(b, identical ? bytes :
                    RandomNumberGenerator.GetBytes(len));
                File.SetCreationTimeUtc(b, File.GetCreationTimeUtc(a));
                File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            }

            Pair(dir, "duplicate-a.jpg", "duplicate-b.jpeg", 4096);
            Pair(dir, "duplicate-a.png", "duplicate-b.png", 8192);
            Pair(dir, "duplicate-a.mp4", "duplicate-b.mp4", 16384);
            Pair(dir, "collision-a.jpg", "collision-b.jpg", 12288, identical: false);

            // Repeated-size candidate with an invisible NTFS ADS. The shallow
            // inventory may count it, but the deep gate must reject it.
            Pair(dir, "ads-a.png", "ads-b.png", 2048);
            string ads = Path.Combine(dir, "ads-b.png") + ":R216SecurityMarker";
            File.WriteAllText(ads, "disposable-ads");

            // Existing hardlinks must never be treated as independently
            // reclaimable duplicate copies.
            string linked = Path.Combine(dir, "linked-a.jpg");
            File.WriteAllBytes(linked, RandomNumberGenerator.GetBytes(3200));
            NativeFile.NewHardLink(Path.Combine(dir, "linked-b.jpg"), linked);

            check(Directory.EnumerateFiles(dir).Count() == 372,
                "R2.16: synthetic corpus has 360 distinct-size and 12 same-size inputs");

            // Legacy-style control: deep metadata capture of *every* file.
            // This is NOT a controlled apples-to-apples speed benchmark:
            // runner caches and execution order influence elapsed time.
            var baselineWatch = Stopwatch.StartNew();
            long baseAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            int baselineDeepCapture = 0, baselineRejected = 0;
            foreach (string path in Directory.EnumerateFiles(dir))
            {
                try { Safety.Capture(path); baselineDeepCapture++; }
                catch (InvalidOperationException) { baselineRejected++; }
            }
            baselineWatch.Stop();
            long baselineAllocated = GC.GetAllocatedBytesForCurrentThread() -
                baseAllocatedBefore;

            var progress = new List<string>();
            var optimizedWatch = Stopwatch.StartNew();
            long optimizedAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            ScanResult scan = Scanner.Run(dir, true,
                new DirectProgress(progress), CancellationToken.None);
            optimizedWatch.Stop();
            long optimizedAllocated = GC.GetAllocatedBytesForCurrentThread() -
                optimizedAllocatedBefore;

            check(scan.FileCount == 372 && scan.DeepInspectedFiles == 12,
                "R2.16: only repeated-size files trigger full NTFS security metadata checks");
            check(scan.HashAttemptedFiles <= 9 && scan.HashAttemptedFiles >= 8,
                "R2.16: no hashing for 360 unique-size media files");
            check(scan.Groups.Count == 3 &&
                  scan.Groups.All(g => g.Files.Count == 2 && g.BlockReason(false) == null),
                "R2.16: same-size, different-byte and ADS/hardlink cases are not false duplicates");
            check(scan.Groups.Select(x => Path.GetExtension(x.Representative.Path).ToLowerInvariant())
                    .OrderBy(x => x).SequenceEqual(new[] { ".jpg", ".mp4", ".png" }),
                "R2.16: JPG, PNG, MP4 byte-identical groups survive fast inventory");
            check(baselineDeepCapture >= 370 && baselineRejected >= 1 &&
                  scan.DeepInspectedFiles * 10 < baselineDeepCapture,
                "R2.16: expensive security captures reduced by at least 90% on unique-size fixture");
            check(progress.Any(p => p.Contains("厳密検査", StringComparison.Ordinal)) &&
                  progress.Any(p => p.Contains("完了", StringComparison.Ordinal)),
                "R2.16: transparent scan-stage progress is available");

            // Warm the shared buffer pool once, then verify many hash and
            // byte-equality operations don't allocate a fresh 1-2 MiB buffer
            // per input. This is a deterministic allocation-bound regression
            // on generated files, not a peak process-memory claim.
            string sameA = Path.Combine(dir, "duplicate-a.jpg");
            string sameB = Path.Combine(dir, "duplicate-b.jpeg");
            string mismatch = Path.Combine(dir, "collision-a.jpg");
            string mismatchOther = Path.Combine(dir, "collision-b.jpg");
            string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sameA)));
            check(Safety.ComputeSha256(sameA, CancellationToken.None) == expectedHash &&
                  Safety.ByteEqual(sameA, sameB, CancellationToken.None) &&
                  !Safety.ByteEqual(mismatch, mismatchOther, CancellationToken.None),
                "R2.16: pooled hash and byte-compare preserve exact-byte behavior");
            long pooledStart = GC.GetAllocatedBytesForCurrentThread();
            for (int n = 0; n < 24; n++)
            {
                check(Safety.ComputeSha256(sameA, CancellationToken.None) == expectedHash &&
                      Safety.ByteEqual(sameA, sameB, CancellationToken.None),
                    "R2.16: reused buffers preserve SHA-256 and exact bytes");
            }
            long pooledAllocation = GC.GetAllocatedBytesForCurrentThread() - pooledStart;
            check(pooledAllocation < 4_000_000,
                "R2.16: 24 hash/byte-compare cycles avoid per-call multi-MiB allocations");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool canceledCorrectly = false;
            try { Scanner.Run(dir, true, null, canceled.Token); }
            catch (OperationCanceledException) { canceledCorrectly = true; }
            check(canceledCorrectly, "R2.16: canceled batch does not continue deep metadata scanning");

            // Write only aggregate, anonymous synthetic performance counters.
            // No usernames, filenames, SIDs or security descriptors in report.
            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r216-scanner-benchmark.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r216-synthetic-scanner-v1",
                    scope = "synthetic Windows NTFS files in isolated smoke directory",
                    fileCount = scan.FileCount,
                    repeatedSizeFiles = scan.DeepInspectedFiles,
                    hashAttempts = scan.HashAttemptedFiles,
                    verifiedDuplicateGroups = scan.Groups.Count,
                    baselineDeepCaptures = baselineDeepCapture,
                    baselineInvalidOrSpecial = baselineRejected,
                    baselineElapsedMs = baselineWatch.Elapsed.TotalMilliseconds,
                    optimizedElapsedMs = optimizedWatch.Elapsed.TotalMilliseconds,
                    baselineAllocatedBytesThread = baselineAllocated,
                    optimizedAllocatedBytesThread = optimizedAllocated,
                    repeatedPooledOperationAllocatedBytes = pooledAllocation,
                    pooledOperationCount = 24,
                    buffersClearedOnReturn = true,
                    timingImprovementAsserted = false,
                    productionFileMutationEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"R216 SYNTHETIC SCAN PASS: {scan.FileCount} inventory, " +
                $"{scan.DeepInspectedFiles} full NTFS captures, " +
                $"{scan.HashAttemptedFiles} hash attempts, 3 verified JPG/PNG/MP4 groups; " +
                $"naive {baselineWatch.Elapsed.TotalMilliseconds:F1} ms vs " +
                $"two-stage {optimizedWatch.Elapsed.TotalMilliseconds:F1} ms (NON-RIGOROUS timing)");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class DirectProgress(List<string> records) : IProgress<string>
    {
        public void Report(string value) => records.Add(value);
    }
}
