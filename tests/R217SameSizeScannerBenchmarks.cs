using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ChappyStorageCompressor.Core;

// R2.17 (part 1): controlled, generated-only benchmark for same-size-heavy scans.
// No user photos/videos, no app mutation path, and no timing performance threshold.
internal static class R217SameSizeScannerBenchmarks
{
    private const int SameSizeBytes = 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadTransferBytes;
        public ulong WriteTransferBytes;
        public ulong OtherTransferBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    private sealed class DirectProgress : IProgress<string>
    {
        public List<string> Messages { get; } = new();
        public void Report(string value) => Messages.Add(value);
    }

    private sealed record Measurement(
        string Dataset, string RunKind, ScanResult Scan,
        IReadOnlyList<string> Messages, double ElapsedMs,
        ulong ProcessReadTransferBytes, long ManagedThreadAllocationBytes,
        long WorkingSetAfterBytes, long PeakWorkingSetSinceProcessStartBytes,
        long PrivateBytesAfter);

    private static IoCounters ReadIo(Process process)
    {
        if (!GetProcessIoCounters(process.Handle, out var value))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "R2.17: Windows process I/O counters unavailable");
        return value;
    }

    private static Measurement Measure(string directory, string dataset, string runKind)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        IoCounters beforeIo = ReadIo(process);
        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
        var messages = new DirectProgress();
        var watch = Stopwatch.StartNew();
        ScanResult scan = Scanner.Run(directory, true, messages, CancellationToken.None);
        watch.Stop();
        IoCounters afterIo = ReadIo(process);
        process.Refresh();
        return new Measurement(dataset, runKind, scan, messages.Messages,
            watch.Elapsed.TotalMilliseconds,
            afterIo.ReadTransferBytes - beforeIo.ReadTransferBytes,
            GC.GetAllocatedBytesForCurrentThread() - beforeAlloc,
            process.WorkingSet64, process.PeakWorkingSet64,
            process.PrivateMemorySize64);
    }

    private static string IndependentSha256(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    private static Dictionary<string, string> Fingerprint(string folder) =>
        Directory.EnumerateFiles(folder).ToDictionary(
            p => Path.GetFileName(p), IndependentSha256,
            StringComparer.OrdinalIgnoreCase);

    public static void Run(string parent, Action<bool, string> check)
    {
        // The caller creates a disposable randomized test root.
        string root = Path.Combine(parent, "r217-generated-only");
        string unique = Path.Combine(root, "unique-heavy");
        string same = Path.Combine(root, "same-size-heavy");
        Directory.CreateDirectory(unique);
        Directory.CreateDirectory(same);

        try
        {
            // 48 independent, distinct sizes: 640-828 KiB each, about 34 MiB.
            // File extensions indicate scan categories only, not valid codecs.
            for (int i = 0; i < 48; i++)
            {
                string ext = (i % 3) switch { 0 => ".jpg", 1 => ".png", _ => ".mp4" };
                byte[] bytes = new byte[640 * 1024 + i * 4096];
                new Random(217000 + i).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(unique, $"unique-{i:D3}{ext}"), bytes);
            }

            // 72 different NTFS files with the same 1 MiB length: 3 exact
            // duplicate pairs and 66 unrelated same-size collisions.
            // The media-like workload stresses SHA-256 and full byte compares;
            // these bytes are not meant to decode as actual JPG/PNG/MP4.
            for (int i = 0; i < 72; i++)
            {
                // Keep switch separate from ?:; C# switch-expression precedence
                // must not reinterpret a conditional expression as the operand.
                string ext;
                if (i < 2) ext = ".jpg";
                else if (i < 4) ext = ".png";
                else if (i < 6) ext = ".mp4";
                else ext = (i % 3) switch { 0 => ".jpg", 1 => ".png", _ => ".mp4" };
                int seed = 217500 + (i < 6 ? i / 2 : i);
                byte[] bytes = new byte[SameSizeBytes];
                new Random(seed).NextBytes(bytes);
                string path = Path.Combine(same, $"media-{i:D3}{ext}");
                File.WriteAllBytes(path, bytes);
                if (i is 1 or 3 or 5)
                {
                    string previous = Path.Combine(same, $"media-{i - 1:D3}{ext}");
                    File.SetCreationTimeUtc(path, File.GetCreationTimeUtc(previous));
                    File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(previous));
                }
            }

            // Deliberately unsafe same-size cases: ADS and an existing hardlink.
            // Both are excluded by Stage 2 even if a shallow size scan saw them.
            byte[] adsBytes = new byte[SameSizeBytes];
            new Random(217900).NextBytes(adsBytes);
            string adsA = Path.Combine(same, "ads-a.png");
            string adsB = Path.Combine(same, "ads-b.png");
            File.WriteAllBytes(adsA, adsBytes);
            File.WriteAllBytes(adsB, adsBytes);
            string namedStream = adsB + ":R217SyntheticMarker";
            File.WriteAllText(namedStream, "synthetic-test-stream");

            string linkedA = Path.Combine(same, "linked-a.jpg");
            string linkedB = Path.Combine(same, "linked-b.jpg");
            byte[] linkedBytes = new byte[SameSizeBytes];
            new Random(217901).NextBytes(linkedBytes);
            File.WriteAllBytes(linkedA, linkedBytes);
            NativeFile.NewHardLink(linkedB, linkedA);

            var beforeUnique = Fingerprint(unique);
            var beforeSame = Fingerprint(same);
            check(beforeUnique.Count == 48 && beforeSame.Count == 76,
                "R2.17: 48 unique-size and 76 same-size synthetic files prepared");

            var samples = new List<Measurement>();
            for (int run = 0; run < 2; run++)
            {
                // "initial" is NOT a guaranteed cold disk cache, because the
                // fixture was just created and independently checksummed.
                string kind = run == 0 ? "initial-cache-unknown" : "repeat-cache-unknown";
                Measurement u = Measure(unique, "unique-heavy", kind);
                Measurement s = Measure(same, "same-size-heavy", kind);
                samples.Add(u);
                samples.Add(s);

                check(u.Scan.FileCount == 48 &&
                      u.Scan.DeepInspectedFiles == 0 &&
                      u.Scan.HashAttemptedFiles == 0 &&
                      u.Scan.Groups.Count == 0,
                    "R2.17: distinct-size corpus bypasses deep NTFS and SHA checks");

                check(s.Scan.FileCount == 76 &&
                      s.Scan.DeepInspectedFiles == 76 &&
                      s.Scan.HashAttemptedFiles == 73 &&
                      s.Scan.Groups.Count == 3 &&
                      s.Scan.Groups.All(g => g.Files.Count == 2 &&
                                           g.BlockReason(false) == null),
                    "R2.17: same-size corpus uses full authentication, rejects ADS/hardlinks");

                var expected = new HashSet<string>(StringComparer.Ordinal)
                {
                    "media-000.jpg|media-001.jpg",
                    "media-002.png|media-003.png",
                    "media-004.mp4|media-005.mp4"
                };
                static string PairKey(IEnumerable<string> names) =>
                    string.Join("|", names.OrderBy(x => x, StringComparer.Ordinal));
                var actual = s.Scan.Groups
                    .Select(g => PairKey(g.Files.Select(f => Path.GetFileName(f.Path))))
                    .ToHashSet(StringComparer.Ordinal);
                check(actual.SetEquals(expected),
                    "R2.17: exact JPG, PNG and MP4 pairs detected, no unsafe groups");

                check(s.Messages.Count(m => m.StartsWith("SHA-256計算:",
                          StringComparison.Ordinal)) >= 10 &&
                      s.Messages.Any(m => m.Contains("完了",
                          StringComparison.Ordinal)),
                    "R2.17: same-size scan exposes hash and completion progress");
            }

            // Independent reference: framework SHA256.HashData(FileStream),
            // NOT the application's Safety.ComputeSha256/ByteEqual helpers.
            // Limit the reference to the 72 ordinary media files: the ADS and
            // hardlink cases must be excluded by the app's stricter NTFS policy.
            var reference = Directory.EnumerateFiles(same)
                .Where(p => Path.GetFileName(p).StartsWith("media-",
                    StringComparison.Ordinal))
                .GroupBy(IndependentSha256)
                .Where(g => g.Count() > 1)
                .Select(g => string.Join("|", g.Select(p => Path.GetFileName(p))
                    .OrderBy(n => n, StringComparer.Ordinal)))
                .ToHashSet(StringComparer.Ordinal);
            var expectedReference = new HashSet<string>(StringComparer.Ordinal)
            {
                "media-000.jpg|media-001.jpg",
                "media-002.png|media-003.png",
                "media-004.mp4|media-005.mp4"
            };
            bool exactReferencePairs = expectedReference.All(pair =>
            {
                string[] names = pair.Split('|');
                return File.ReadAllBytes(Path.Combine(same, names[0]))
                    .AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(same, names[1])));
            });
            check(reference.SetEquals(expectedReference) && exactReferencePairs,
                "R2.17: independent SHA-256 and byte-comparison oracle confirms groups");

            var afterUnique = Fingerprint(unique);
            var afterSame = Fingerprint(same);
            bool unchanged = beforeUnique.Count == afterUnique.Count &&
                beforeSame.Count == afterSame.Count &&
                beforeUnique.All(x => afterUnique.TryGetValue(x.Key, out var y) && y == x.Value) &&
                beforeSame.All(x => afterSame.TryGetValue(x.Key, out var y) && y == x.Value);
            check(unchanged && File.ReadAllText(namedStream) == "synthetic-test-stream" &&
                  NativeFile.SameFile(linkedA, linkedB),
                "R2.17: repeated scans leave every source, ADS and link intact");

            using var stop = new CancellationTokenSource();
            stop.Cancel();
            bool canceled = false;
            try { Scanner.Run(same, true, null, stop.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "R2.17: pre-canceled scan exits without modifying files");

            // Process I/O transfer bytes include OS-visible logical reads, not
            // physical media reads. PeakWorkingSet64 is since process launch.
            // Neither number proves cold-cache disk throughput or peak RAM
            // attributable solely to the scanner. No private paths in report.
            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-same-size-scanner.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-generated-scanner-part1-v1",
                    scope = "generated-only local Windows NTFS fixture",
                    os = Environment.OSVersion.VersionString,
                    cpuCount = Environment.ProcessorCount,
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    driveFormat = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat,
                    cacheStateControlled = false,
                    timingSpeedupAsserted = false,
                    processIoReadBytesArePhysicalDiskBytes = false,
                    peakWorkingSetIsPerScan = false,
                    syntheticSourceBytes = Directory.EnumerateFiles(unique)
                        .Concat(Directory.EnumerateFiles(same))
                        .Sum(p => new FileInfo(p).Length),
                    samples = samples.Select(s => new
                    {
                        dataset = s.Dataset,
                        runKind = s.RunKind,
                        fileCount = s.Scan.FileCount,
                        deepInspections = s.Scan.DeepInspectedFiles,
                        hashAttempts = s.Scan.HashAttemptedFiles,
                        verifiedGroups = s.Scan.Groups.Count,
                        elapsedMs = s.ElapsedMs,
                        processReadTransferBytes = s.ProcessReadTransferBytes,
                        managedAllocationBytesCurrentThread = s.ManagedThreadAllocationBytes,
                        workingSetAfterBytes = s.WorkingSetAfterBytes,
                        peakWorkingSetSinceProcessStartBytes =
                            s.PeakWorkingSetSinceProcessStartBytes,
                        privateBytesAfter = s.PrivateBytesAfter
                    }),
                    guiInteractionVerified = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 generated same-size NTFS scanner: " +
                "two initial/repeat samples for each corpus; no real media modified");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
