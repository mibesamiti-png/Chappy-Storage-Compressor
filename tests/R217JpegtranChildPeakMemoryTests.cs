using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// R2.17 part 10: actual OS-tracked per-child peak resident working set
// of official-source jpegtran.exe, never the parent .NET test process.
// All input JPEG files are synthetic and isolated; never user media.
internal static class R217JpegtranChildPeakMemoryTests
{
    private const string PinnedCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";
    private const int Width = 1280, Height = 720, Count = 3;
    private sealed record ChildSample(
        string Mode, int Fixture, long SourceBytes, long PreviewBytes,
        long EstimatedCandidateSavingBytes, double NativeJpegtranElapsedMs,
        long ChildPeakWorkingSetBytes, long ChildPeakVirtualMemoryBytes,
        long ChildPeakPagedMemoryBytes, int ChildExitCode,
        string JpegtranExecutableSha256, bool IndependentPreviewByteIdentical,
        bool OriginalHashAndTimestampPreserved);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] GeneratePixels(int variant)
    {
        var pixels = new byte[checked(Width * Height * 4)];
        var random = new Random(2171000 + variant);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            // A mix of stripes, gradients and seeded noise. All user-free.
            pixels[p] = (byte)(variant == 0 ? x / 7 :
                variant == 1 ? random.Next(256) : (x + y) / 13);
            pixels[p + 1] = (byte)(variant == 0 ? y / 5 :
                variant == 1 ? random.Next(256) : (x / 9 + y / 11));
            pixels[p + 2] = (byte)(variant == 0 ? (x / 8 + y / 9) :
                variant == 1 ? random.Next(256) : (x / 10 + 30));
            pixels[p + 3] = 255;
        }
        return pixels;
    }

    private static void GenerateSource(string path, int variant)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, GeneratePixels(variant), Width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        var snapshot = JpegMarkerAudit.Inspect(path);
        if (snapshot.Width != Width || snapshot.Height != Height ||
            !snapshot.Baseline || snapshot.ScanCount != 1)
            throw new InvalidDataException("Synthetic source JPEG is not supported baseline");
    }

    private static ChildSample Measure(string root, string source, int fixture,
        string mode, string tool, string sourceHash, long ticks)
    {
        // Distinct directories, never source folder or a production path.
        string rawOutput = Path.Combine(root, $"direct-{mode}-{fixture:D2}");
        string appOutput = Path.Combine(root, $"engine-{mode}-{fixture:D2}");
        Directory.CreateDirectory(rawOutput);
        Directory.CreateDirectory(appOutput);
        string directCandidate = Path.Combine(rawOutput, "native-jpegtran-output.jpg");
        var start = Stopwatch.StartNew();
        int exitCode;
        long peakWorkingSet, peakVirtual, peakPaged;
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = rawOutput,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Exact production jpegtran options; only isolated preview output.
        psi.ArgumentList.Add("-copy");
        psi.ArgumentList.Add("all");
        psi.ArgumentList.Add("-optimize");
        psi.ArgumentList.Add("-outfile");
        psi.ArgumentList.Add(directCandidate);
        psi.ArgumentList.Add(source);
        using (var child = Process.Start(psi) ??
               throw new IOException("Pinned jpegtran child cannot be launched"))
        {
            try
            {
                // Kernel tracks process-lifetime peaks even if it finishes
                // before a high-frequency user-space sampler could observe it.
                if (!child.WaitForExit(60_000))
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                    throw new TimeoutException("CI-only jpegtran timed out");
                }
                start.Stop();
                child.Refresh();
                exitCode = child.ExitCode;
                peakWorkingSet = child.PeakWorkingSet64;
                peakVirtual = child.PeakVirtualMemorySize64;
                peakPaged = child.PeakPagedMemorySize64;
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit();
                }
            }
        }
        if (exitCode != 0 || peakWorkingSet <= 0 || peakVirtual <= 0 ||
            peakPaged < 0 || !File.Exists(directCandidate))
            throw new InvalidDataException("Native child memory or exit evidence missing");
        var directSize = new FileInfo(directCandidate).Length;
        var originalSize = new FileInfo(source).Length;
        var originalMarkers = JpegMarkerAudit.Inspect(source);
        if (directSize >= originalSize ||
            JpegMarkerAudit.Inspect(directCandidate) != originalMarkers ||
            !JpegLosslessPreviewEngine.EquivalentDecodedPixels(source, directCandidate))
            throw new InvalidDataException("Native child output does not preserve source JPEG");

        // Run the application production preview engine independently and
        // compare actual output bytes against the directly measured tool run.
        var result = JpegLosslessPreviewEngine.CreatePreview(source, appOutput, tool);
        if (result.Candidate is null || result.CandidateBytes != directSize ||
            JpegMarkerAudit.Inspect(result.Candidate) != originalMarkers ||
            !JpegLosslessPreviewEngine.EquivalentDecodedPixels(source, result.Candidate))
            throw new InvalidDataException("Real app preview does not match native child result");
        string directHash = Hash(directCandidate);
        bool same = directHash == Hash(result.Candidate);
        if (!same)
            throw new InvalidDataException("Directly measured child did not match app preview byte-for-byte");
        bool unchanged = Hash(source) == sourceHash &&
            File.GetLastWriteTimeUtc(source).Ticks == ticks;
        if (!unchanged)
            throw new InvalidDataException("Generated source JPEG mutated during native child test");
        return new ChildSample(mode, fixture, originalSize, directSize,
            originalSize - directSize, start.Elapsed.TotalMilliseconds,
            peakWorkingSet, peakVirtual, peakPaged, exitCode, Hash(tool),
            same, unchanged);
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? baseline = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(baseline))
        {
            Console.WriteLine("R2.17 true JPEG child peak memory SKIPPED: no pinned CI tool");
            return;
        }
        string? simd = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        check(File.Exists(baseline) && File.Exists(simd) &&
              Path.GetFileName(baseline).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase) &&
              Path.GetFileName(simd).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == PinnedCommit &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17: official source-verified baseline and SIMD tools required for child memory");

        string root = Path.Combine(parent, "r217-actual-child-memory-only");
        string input = Path.Combine(root, "synthetic-jpegs");
        Directory.CreateDirectory(input);
        try
        {
            for (int i = 0; i < Count; i++)
                GenerateSource(Path.Combine(input, $"fixture-{i:D2}.jpg"), i);
            var sources = Directory.EnumerateFiles(input, "*.jpg").OrderBy(x => x)
                .ToArray();
            var hashes = sources.ToDictionary(x => x, Hash);
            var ticks = sources.ToDictionary(x => x, x => File.GetLastWriteTimeUtc(x).Ticks);
            check(sources.Length == Count &&
                  hashes.Values.Distinct(StringComparer.Ordinal).Count() == Count,
                "R2.17: three distinct generated baseline JPEGs for native child memory");

            // Interleave across variants, but this is NOT a controlled
            // comparative SIMD speed/memory benchmark.
            var samples = new List<ChildSample>();
            for (int i = 0; i < Count; i++)
            {
                samples.Add(Measure(root, sources[i], i, "baseline", baseline,
                    hashes[sources[i]], ticks[sources[i]]));
                samples.Add(Measure(root, sources[i], i, "simd", simd!,
                    hashes[sources[i]], ticks[sources[i]]));
            }
            check(samples.Count == Count * 2 &&
                  samples.All(x => x.ChildExitCode == 0 &&
                      x.ChildPeakWorkingSetBytes > 0 &&
                      x.ChildPeakVirtualMemoryBytes > 0 &&
                      x.NativeJpegtranElapsedMs > 0 &&
                      x.EstimatedCandidateSavingBytes > 0),
                "R2.17: 6 official jpegtran child exits with OS process-lifetime peak memory");
            check(samples.All(x => x.IndependentPreviewByteIdentical &&
                      x.OriginalHashAndTimestampPreserved) &&
                  samples.GroupBy(x => x.Fixture).All(g =>
                      g.Count() == 2 &&
                      g.Select(x => x.PreviewBytes).Distinct().Count() == 1),
                "R2.17: independent direct child and app outputs match exact JPEG bytes");
            check(samples.Where(x => x.Mode == "baseline")
                      .Select(x => x.JpegtranExecutableSha256).Distinct().Count() == 1 &&
                  samples.Where(x => x.Mode == "simd")
                      .Select(x => x.JpegtranExecutableSha256).Distinct().Count() == 1 &&
                  samples[0].JpegtranExecutableSha256 != samples[1].JpegtranExecutableSha256,
                "R2.17: OS peak memory measured against separate pinned baseline/SIMD executables");
            check(hashes.All(x => Hash(x.Key) == x.Value &&
                  File.GetLastWriteTimeUtc(x.Key).Ticks == ticks[x.Key]),
                "R2.17: original generated JPEG SHA and last-write timestamps unchanged");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(
                Path.Combine("diagnostics", "r217-jpegtran-child-memory.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-actual-jpegtran-child-process-peak-v1",
                    officialSourceCommit = PinnedCommit,
                    nativeExecutableMeasuredDirectly = true,
                    osProvidedPerChildPeakWorkingSet64 = true,
                    osProvidedPerChildPeakVirtualMemorySize64 = true,
                    separateDirectToolAndRealApplicationPreviewBothExecuted = true,
                    byteIdenticalOutputsAndWicPixelChecks = true,
                    originalSha256AndTimestampsPreserved = true,
                    generatedSyntheticJpegCount = Count,
                    measuredNativeChildProcessCount = samples.Count,
                    samplePerToolAndFixture = samples,
                    fileCountPerTool = Count,
                    actualFreedDiskBytes = 0,
                    memoryMetricMeaning = "Windows peak resident working set per jpegtran child process lifetime; not private bytes/total application peak",
                    crossMachineSpeedOrMemoryAdvantageGuaranteed = false,
                    coldCacheOrCpuSchedulingControlled = false,
                    realUserMediaTested = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"R2.17 native child peak memory PASS: {samples.Count} measured jpegtran processes, " +
                $"baseline max {samples.Where(x => x.Mode == "baseline").Max(x => x.ChildPeakWorkingSetBytes)} B, " +
                $"SIMD max {samples.Where(x => x.Mode == "simd").Max(x => x.ChildPeakWorkingSetBytes)} B; synthetic only");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
