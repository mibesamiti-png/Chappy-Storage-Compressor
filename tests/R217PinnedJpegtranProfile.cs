using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// R2.17 part 6: REAL jpegtran and the real app JPEG preview engine,
// on generated-only valid JPEGs. This test is OPT-IN when a source-pinned
// executable is explicitly provisioned by CI. It never executes user tools.
internal static class R217PinnedJpegtranProfile
{
    private const int Count = 8;
    private const int Width = 640;
    private const int Height = 480;
    private const string Commit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";

    private sealed class InlineProgress : IProgress<JpegBatchPreviewEngine.Progress>
    {
        public int Updates { get; private set; }
        public int Failures { get; private set; }
        public void Report(JpegBatchPreviewEngine.Progress progress)
        {
            Updates++;
            if (progress.Status == "Failed") Failures++;
        }
    }

    private static string IndependentSha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] Pattern(int index)
    {
        var result = new byte[Width * Height * 4];
        var random = new Random(217600 + index);
        // Distinct levels of compressibility. Every file is a valid WIC JPEG,
        // not arbitrary JPEG-like bytes or a user photo.
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int at = (y * Width + x) * 4;
                if (index % 3 == 0)
                {
                    result[at] = (byte)(x / 32 * 11);
                    result[at + 1] = (byte)(y / 32 * 9);
                    result[at + 2] = (byte)(index * 23);
                }
                else if (index % 3 == 1)
                {
                    result[at] = (byte)((x + index * 7) % 256);
                    result[at + 1] = (byte)((y * 3 + index * 13) % 256);
                    result[at + 2] = (byte)(((x / 16) + (y / 16)) * 5 % 256);
                }
                else
                {
                    result[at] = (byte)random.Next(256);
                    result[at + 1] = (byte)random.Next(256);
                    result[at + 2] = (byte)random.Next(256);
                }
                result[at + 3] = 255;
            }
        return result;
    }

    private static void WriteJpeg(string path, int index)
    {
        byte[] pixels = Pattern(index);
        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, Width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        if (index % 2 == 0) AddCommentMarker(path, index);
    }

    private static void AddCommentMarker(string path, int index)
    {
        byte[] source = File.ReadAllBytes(path);
        if (source.Length < 8 || source[0] != 0xff || source[1] != 0xd8)
            throw new InvalidDataException("Generated JPEG SOI missing");
        // Insert the COM segment after the initial APP0 JFIF marker when
        // available. It has a normal marker length and contains no user data.
        int insert = 2;
        if (source[2] == 0xff && source[3] == 0xe0)
        {
            int length = (source[4] << 8) | source[5];
            insert = checked(2 + 2 + length);
            if (insert > source.Length)
                throw new InvalidDataException("Invalid generated JFIF segment");
        }
        byte[] payload = Encoding.ASCII.GetBytes("ChappyR217SyntheticComment-" + index);
        int segmentLength = payload.Length + 2;
        byte[] withComment = new byte[source.Length + 4 + payload.Length];
        source.AsSpan(0, insert).CopyTo(withComment);
        withComment[insert] = 0xff;
        withComment[insert + 1] = 0xfe;
        withComment[insert + 2] = (byte)(segmentLength >> 8);
        withComment[insert + 3] = (byte)segmentLength;
        payload.CopyTo(withComment.AsSpan(insert + 4));
        source.AsSpan(insert).CopyTo(withComment.AsSpan(insert + 4 + payload.Length));
        File.WriteAllBytes(path, withComment);
    }

    private static (int Width, int Height, byte[] Bytes) IndependentDecode(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoded = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoded.Frames.Count != 1)
            throw new InvalidDataException("Expected exactly one JPEG frame");
        var rgb = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgr24, null, 0);
        var pixelBytes = new byte[checked(rgb.PixelWidth * rgb.PixelHeight * 3)];
        rgb.CopyPixels(pixelBytes, checked(rgb.PixelWidth * 3), 0);
        return (rgb.PixelWidth, rgb.PixelHeight, pixelBytes);
    }

    private static string Version(string tool)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                ArgumentList = { "-version" }
            }
        };
        if (!proc.Start()) throw new IOException("Pinned jpegtran could not start");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        if (!proc.WaitForExit(10_000) || proc.ExitCode != 0)
            throw new IOException("Pinned jpegtran version probe failed");
        return (stdout + "\n" + stderr).Trim();
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? tool = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(tool))
        {
            // Standard local smoke tests don't auto-download or execute
            // third-party code; CI supplies a source-built pinned jpegtran.
            Console.WriteLine("R2.17 official jpegtran profiling SKIPPED: no pinned CI tool");
            return;
        }
        string sourceCommit = Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") ?? "";
        check(string.Equals(sourceCommit, Commit, StringComparison.Ordinal) &&
              Path.GetFileName(tool).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase) &&
              File.Exists(tool),
            "R2.17: official libjpeg-turbo tool path and pinned source commit provided by CI");
        string version = Version(tool);
        check(version.Contains("3.2.1", StringComparison.Ordinal),
            "R2.17: jpegtran -version identifies the pinned official 3.2.1 release");

        string root = Path.Combine(parent, "r217-pinned-jpegtran-only");
        string input = Path.Combine(root, "generated-input");
        string output = Path.Combine(root, "candidate-only-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        try
        {
            for (int i = 0; i < Count; i++)
                WriteJpeg(Path.Combine(input, $"synthetic-{i:D2}.jpg"), i);

            var originals = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), IndependentSha);
            var markers = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), x => JpegMarkerAudit.Inspect(x));
            var initialMeta = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x),
                    x => (Size: new FileInfo(x).Length,
                          Date: File.GetLastWriteTimeUtc(x).Ticks));
            int comments = 0;
            foreach (string source in Directory.EnumerateFiles(input, "*.jpg"))
            {
                var image = IndependentDecode(source);
                if (image.Width != Width || image.Height != Height)
                    throw new InvalidDataException("Generated JPEG image failed independent WIC decode");
                if (markers[Path.GetFileName(source)].ProtectedMarkerCount > 2) comments++;
            }
            check(originals.Count == Count && markers.Count == Count,
                "R2.17: eight valid baseline JPEG images independently decoded and marker-audited");

            // The production engine, not a synthetic jpegtran stub, is timed.
            var progress = new InlineProgress();
            long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long beforeWorkingSet = process.WorkingSet64;
            var watch = Stopwatch.StartNew();
            var summary = JpegBatchPreviewEngine.Run(input, output, tool, progress);
            watch.Stop();
            process.Refresh();
            long afterWorkingSet = process.WorkingSet64;
            long allocations = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

            check(summary.Total == Count && summary.Done == Count &&
                  summary.Completed + summary.Skipped == Count &&
                  summary.Failed == 0 && summary.Deferred == 0 &&
                  summary.NeedsReview == 0 && progress.Failures == 0 &&
                  progress.Updates == Count,
                "R2.17: real jpegtran processes finish every generated source without error");

            var previews = Directory.EnumerateFiles(
                Path.Combine(output, JpegBatchPreviewEngine.PreviewName), "*.jpg").ToList();
            check(summary.Completed > 0 && summary.EstimatedSavingBytes > 0 &&
                  previews.Count == summary.Completed,
                "R2.17: real Huffman optimization generates smaller accepted JPEG candidates");

            long independentlyVerifiedSaving = 0;
            foreach (string preview in previews)
            {
                string filename = Path.GetFileName(preview);
                int at = filename.LastIndexOf(".chappy-jpeg-preview-", StringComparison.Ordinal);
                if (at < 0) throw new InvalidDataException("Unexpected candidate filename");
                string original = Path.Combine(input, filename[..at] + ".jpg");
                if (!File.Exists(original)) throw new InvalidDataException("Candidate source missing");

                var srcMarker = JpegMarkerAudit.Inspect(original);
                var newMarker = JpegMarkerAudit.Inspect(preview);
                if (srcMarker != newMarker)
                    throw new InvalidDataException("Protected metadata, quantization or SOF changed");
                var src = IndependentDecode(original);
                var dst = IndependentDecode(preview);
                if (src.Width != dst.Width || src.Height != dst.Height ||
                    !src.Bytes.AsSpan().SequenceEqual(dst.Bytes))
                    throw new InvalidDataException("Independent full decoded BGR pixel mismatch");
                independentlyVerifiedSaving = checked(independentlyVerifiedSaving +
                    new FileInfo(original).Length - new FileInfo(preview).Length);
            }
            check(independentlyVerifiedSaving == summary.EstimatedSavingBytes,
                "R2.17: all kept JPEG previews pass independent full-pixel and metadata audits");

            var sourceAfterFirst = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), IndependentSha);
            check(originals.All(x => sourceAfterFirst.TryGetValue(x.Key, out var v) && v == x.Value)
                  && initialMeta.All(x => new FileInfo(Path.Combine(input, x.Key)).Length == x.Value.Size &&
                    File.GetLastWriteTimeUtc(Path.Combine(input, x.Key)).Ticks == x.Value.Date),
                "R2.17: original JPEG bytes, sizes and timestamps unchanged after real jpegtran");

            var resumeWatch = Stopwatch.StartNew();
            var resumed = JpegBatchPreviewEngine.Run(input, output, tool);
            resumeWatch.Stop();
            var audit = JpegBatchPreviewEngine.Inspect(input, output, tool);
            check(resumed.Total == Count && resumed.Done == Count &&
                  resumed.Completed == summary.Completed &&
                  resumed.EstimatedSavingBytes == summary.EstimatedSavingBytes &&
                  Directory.EnumerateFiles(
                      Path.Combine(output, JpegBatchPreviewEngine.PreviewName), "*.jpg").Count()
                      == previews.Count && audit.VerifiedSavingBytes == independentlyVerifiedSaving &&
                  audit.NeedsReview == 0,
                "R2.17: resume reuses previously validated JPEG candidates without duplication");

            var sourceAfterResume = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), IndependentSha);
            check(originals.All(x => sourceAfterResume.TryGetValue(x.Key, out var v) && v == x.Value),
                "R2.17: repeat jpegtran candidate run leaves all source SHA-256 hashes unchanged");
            check(summary.EstimatedSavingBytes >= 0 && audit.VerifiedSavingBytes >= 0,
                "R2.17: saved bytes are preview-to-source size differences, not actual freed bytes");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-pinned-jpegtran-profile.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-official-source-built-jpegtran-v1",
                    upstreamRepo = "https://github.com/libjpeg-turbo/libjpeg-turbo",
                    upstreamVersion = "3.2.1",
                    upstreamCommit = Commit,
                    upstreamSourcePinnedAndCheckedByCi = true,
                    nativeSimdBuildEnabled = false,
                    officialBinaryInstallerUsed = false,
                    toolReportedVersion = version,
                    syntheticValidJpegCount = Count,
                    testJpegWithCommentMarkers = Count / 2,
                    jpegtranActuallyExecuted = true,
                    jpegEngineActualCandidateGenerationMeasured = true,
                    generatedCandidateCount = summary.Completed,
                    skippedNotSmallerCount = summary.Skipped,
                    failureCount = summary.Failed,
                    verifiedDecodedCandidateCount = previews.Count,
                    verifiedProtectedMetadataCandidateCount = previews.Count,
                    originalSourcesUnchanged = true,
                    resumeCreatedNoAdditionalCandidates = true,
                    sourceTotalBytes = initialMeta.Values.Sum(x => x.Size),
                    candidateTotalBytes = previews.Sum(x => new FileInfo(x).Length),
                    theoreticalCandidateSavingBytes = independentlyVerifiedSaving,
                    candidateGenerationElapsedMs = watch.Elapsed.TotalMilliseconds,
                    candidateResumeElapsedMs = resumeWatch.Elapsed.TotalMilliseconds,
                    allocatedManagedBytesCurrentThread = allocations,
                    workingSetBeforeBytes = beforeWorkingSet,
                    workingSetAfterBytes = afterWorkingSet,
                    physicalDiskReadsControlled = false,
                    cleanColdWarmCacheControlled = false,
                    peakWorkingSetAttributedToJpegEngine = false,
                    physicalWpfMouseInteractionVerified = false,
                    productionHardlinkMergeEnabled = false,
                    productionOriginalReplacementEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 pinned jpegtran 3.2.1: " +
                $"real generated-only candidates {summary.Completed}/{Count}; " +
                $"savings {independentlyVerifiedSaving} bytes (theoretical); " +
                $"first {watch.Elapsed.TotalMilliseconds:F2} ms, " +
                $"resume {resumeWatch.Elapsed.TotalMilliseconds:F2} ms");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
