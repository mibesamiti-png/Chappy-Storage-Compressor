using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// Priority batch: real PNG+JPEG preview engines / resume; observed concurrent
// app process and the ACTUAL child launched by JpegLosslessPreviewEngine.
// Uses generated, isolated PNG/JPEG only; never downloads personal media.
internal static class R217PriorityImageAndMemoryTests
{
    private const int Count = 6;
    private const string Pinned = "7b4993786d856cbccabfc0ed2f923ba000e326cc";
    private sealed record Phase(string Name, double ElapsedMs,
        long ManagedCallingThreadAllocatedBytes);
    private sealed record ConcurrentSample(int Variant, double EngineMs, long SourceBytes,
        long CandidateBytes, long ParentStartWorkingSetBytes,
        long ParentEndWorkingSetBytes, long MaxObservedParentWorkingSetBytes,
        long MaxObservedNativeChildPeakWorkingSetBytes,
        long MaxObservedParentPlusNativeWorkingSetBytes,
        int LiveJointObservations, bool SourceUnchanged, bool CandidateDecodedPixelsMatch);

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }
    private static Phase Time(string name, Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        action();
        clock.Stop();
        return new Phase(name, clock.Elapsed.TotalMilliseconds,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }
    private static byte[] CreatePixels(int width, int height, int variant)
    {
        var pixels = new byte[checked(width * height * 4)];
        var random = new Random(21717000 + variant);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = (y * width + x) * 4;
            pixels[p] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)((x / 11 + variant * 7) & 255);
            pixels[p + 1] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)((y / 9 + variant * 13) & 255);
            pixels[p + 2] = variant % 3 == 1 ? (byte)random.Next(256) :
                (byte)(((x + y) / 15 + variant * 19) & 255);
            pixels[p + 3] = 255;
        }
        return pixels;
    }
    private static void WriteJpeg(string path, int width, int height, int variant)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
            null, CreatePixels(width, height, variant), width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 89 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> bytes)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type, header[4..]);
        output.Write(header);
        output.Write(bytes);
        uint crc = 0xffffffff;
        foreach (byte b in header[4..8])
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        foreach (byte b in bytes)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        Span<byte> digest = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(digest, ~crc);
        output.Write(digest);
    }
    private static void WriteUncompressedPng(string path, int width, int height, int variant)
    {
        byte[] bgra = CreatePixels(width, height, variant);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)height);
        header[8] = 8; header[9] = 6; // 8bit RGBA
        using var inflated = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            inflated.WriteByte(0); // PNG filter=none
            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                inflated.WriteByte(bgra[p + 2]);
                inflated.WriteByte(bgra[p + 1]);
                inflated.WriteByte(bgra[p]);
                inflated.WriteByte(bgra[p + 3]);
            }
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.NoCompression, true))
            inflated.WriteTo(z);
        using var output = File.Create(path);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "tEXt", "Comment\0Generated R2.17 bulk priority"u8);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }
    private static (int W, int H, byte[] PixelBytes) Decode(string file)
    {
        using var stream = File.OpenRead(file);
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames;
        if (frames.Count != 1) throw new InvalidDataException("JPEG/PNG frame count incorrect");
        var bmp = new FormatConvertedBitmap(frames[0], PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[checked(bmp.PixelWidth * bmp.PixelHeight * 4)];
        bmp.CopyPixels(pixels, checked(bmp.PixelWidth * 4), 0);
        return (bmp.PixelWidth, bmp.PixelHeight, pixels);
    }
    private static bool EqualPixels(string left, string right)
    {
        var a = Decode(left);
        var b = Decode(right);
        return a.W == b.W && a.H == b.H &&
            a.PixelBytes.AsSpan().SequenceEqual(b.PixelBytes);
    }

    private static ConcurrentSample MeasureConcurrent(string root, string source,
        string tool, int variant)
    {
        string output = Path.Combine(root, $"concurrent-output-{variant}");
        Directory.CreateDirectory(output);
        string originalHash = Hash(source);
        long originalTicks = File.GetLastWriteTimeUtc(source).Ticks;
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long before = self.WorkingSet64;
        int childPid = 0, stop = 0, observations = 0;
        long maxParent = 0, maxNative = 0, maxSum = 0;

        // Test-only PID hook observes EXACT child started by actual application
        // engine; never look up arbitrary system jpegtran processes by name.
        var sampler = Task.Run(() =>
        {
            using var parent = Process.GetCurrentProcess();
            while (Volatile.Read(ref stop) == 0)
            {
                int pid = Volatile.Read(ref childPid);
                if (pid <= 0)
                {
                    Thread.Sleep(1);
                    continue;
                }
                try
                {
                    using var native = Process.GetProcessById(pid);
                    if (!native.HasExited)
                    {
                        native.Refresh();
                        parent.Refresh();
                        if (!native.HasExited)
                        {
                            long childPeak = native.PeakWorkingSet64;
                            long currentParent = parent.WorkingSet64;
                            if (childPeak > 0 && currentParent > 0)
                            {
                                maxParent = Math.Max(maxParent, currentParent);
                                maxNative = Math.Max(maxNative, childPeak);
                                maxSum = Math.Max(maxSum, checked(currentParent + childPeak));
                                observations++;
                            }
                        }
                    }
                }
                catch (ArgumentException) { /* process finished between PID observation and open */ }
                catch (InvalidOperationException) { /* process finished while sampling */ }
                Thread.Sleep(1);
            }
        });
        JpegLosslessPreviewEngine.Result? candidate = null;
        var timer = Stopwatch.StartNew();
        try
        {
            candidate = JpegLosslessPreviewEngine.CreatePreview(source, output, tool,
                CancellationToken.None,
                testOnlyNativeProcessId: pid => Volatile.Write(ref childPid, pid));
        }
        finally
        {
            timer.Stop();
            Volatile.Write(ref stop, 1);
            sampler.GetAwaiter().GetResult();
        }
        self.Refresh();
        if (candidate.Candidate is null ||
            !JpegLosslessPreviewEngine.EquivalentDecodedPixels(source, candidate.Candidate))
            throw new InvalidDataException("Test-only genuine engine JPEG candidate invalid");
        bool unchanged = Hash(source) == originalHash &&
            File.GetLastWriteTimeUtc(source).Ticks == originalTicks;
        if (!unchanged) throw new InvalidDataException("Concurrent probe modified original");
        if (observations <= 0 || maxParent <= 0 || maxNative <= 0 || maxSum <= 0)
            throw new InvalidDataException("No concurrent parent+exact-child OS memory evidence");
        return new ConcurrentSample(variant, timer.Elapsed.TotalMilliseconds,
            candidate.SourceBytes, candidate.CandidateBytes, before,
            self.WorkingSet64, maxParent, maxNative, maxSum, observations,
            unchanged, true);
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? tool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        if (string.IsNullOrEmpty(tool))
        {
            Console.WriteLine("R2.17 combined image+memory matrix SKIPPED outside source-pinned Windows CI");
            return;
        }
        check(File.Exists(tool) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == Pinned &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17 image priority: official-pinned native SIMD jpegtran required");
        string root = Path.Combine(parent, "r217-bulk-images-and-memory-only");
        string input = Path.Combine(root, "input");
        string output = Path.Combine(root, "png-jpeg-output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        try
        {
            var phases = new List<Phase>();
            phases.Add(Time("generate12-decoded-jpeg-png", () =>
            {
                for (int i = 0; i < Count; i++)
                {
                    WriteJpeg(Path.Combine(input, $"generated-{i:D2}.jpg"),
                        960, 640, i);
                    WriteUncompressedPng(Path.Combine(input, $"generated-{i:D2}.png"),
                        512, 320, i + 10);
                }
            }));
            var sources = Directory.GetFiles(input).OrderBy(x => x).ToArray();
            var sourceHashes = sources.ToDictionary(p => p, Hash);
            var sourceTicks = sources.ToDictionary(p => p,
                p => File.GetLastWriteTimeUtc(p).Ticks);
            check(sources.Length == 12 &&
                  sources.Count(x => x.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) == Count &&
                  sources.Count(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) == Count,
                "R2.17 image priority: real WIC-decodable JPEG and intentionally uncompressed valid PNG set");

            PngBatchPreviewEngine.Summary? pngFirst = null;
            PngBatchPreviewEngine.Summary? pngResume = null;
            JpegBatchPreviewEngine.Summary? jpegFirst = null;
            JpegBatchPreviewEngine.Summary? jpegResume = null;
            phases.Add(Time("png-first-real-batch", () =>
                pngFirst = PngBatchPreviewEngine.Run(input, output)));
            check(pngFirst!.Total == Count && pngFirst.Failed == 0 &&
                  pngFirst.Optimized >= 4 && pngFirst.SavedBytes > 0,
                "R2.17 image priority: PNG real batch retains verified smaller candidates");
            phases.Add(Time("jpeg-first-real-batch", () =>
                jpegFirst = JpegBatchPreviewEngine.Run(input, output, tool)));
            check(jpegFirst!.Total == Count && jpegFirst.Failed == 0 &&
                  jpegFirst.Completed >= 3 && jpegFirst.EstimatedSavingBytes > 0,
                "R2.17 image priority: JPEG real batch retains verified smaller candidates");

            // All accepted output files must decode to exactly the same pixels
            // as their corresponding independent source. Inspect rejects stale.
            var pngPreview = Path.Combine(output, "chappy-png-previews");
            var jpegAudit = JpegBatchPreviewEngine.Inspect(input, output, tool);
            int decodedPng = 0;
            int decodedJpeg = 0;
            foreach (string path in Directory.EnumerateFiles(pngPreview, "*.png"))
            {
                string name = Path.GetFileName(path);
                int delimiter = name.IndexOf(".chappy-preview-", StringComparison.Ordinal);
                if (delimiter < 0) throw new InvalidDataException("Unexpected generated PNG preview");
                string original = Path.Combine(input, name[..delimiter] + ".png");
                if (!EqualPixels(original, path)) throw new InvalidDataException("PNG candidate pixels changed");
                decodedPng++;
            }
            foreach (var row in jpegAudit.Rows.Where(x => x.Status == "Completed"))
            {
                string original = Path.Combine(input, row.RelativePath);
                if (!EqualPixels(original, row.CandidatePath) ||
                    JpegMarkerAudit.Inspect(original) != JpegMarkerAudit.Inspect(row.CandidatePath))
                    throw new InvalidDataException("JPEG candidate pixel/metadata changed");
                decodedJpeg++;
            }
            check(decodedPng == pngFirst.Optimized &&
                  decodedJpeg == jpegFirst.Completed &&
                  jpegAudit.VerifiedSavingBytes == jpegFirst.EstimatedSavingBytes,
                "R2.17 image priority: independent WPF pixel and JPEG marker audit of accepted candidates");

            phases.Add(Time("png-resume", () =>
                pngResume = PngBatchPreviewEngine.Run(input, output)));
            phases.Add(Time("jpeg-resume", () =>
                jpegResume = JpegBatchPreviewEngine.Run(input, output, tool)));
            check(pngResume!.Optimized == pngFirst.Optimized &&
                  pngResume.SavedBytes == pngFirst.SavedBytes &&
                  jpegResume!.Completed == jpegFirst.Completed &&
                  jpegResume.EstimatedSavingBytes == jpegFirst.EstimatedSavingBytes &&
                  Directory.EnumerateFiles(pngPreview, "*.png").Count() == decodedPng &&
                  Directory.EnumerateFiles(Path.Combine(output, "chappy-jpeg-r19-previews"), "*.jpg").Count() == decodedJpeg,
                "R2.17 image priority: both real-format batch resumes avoid repeated candidates");

            var concurrent = new List<ConcurrentSample>();
            phases.Add(Time("combined-app-and-jpegtran-memory", () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    string source = Path.Combine(input, $"large-memory-{i}.jpg");
                    WriteJpeg(source, 2560, 1600, i + 101);
                    concurrent.Add(MeasureConcurrent(root, source, tool, i));
                }
            }));
            check(concurrent.Count == 2 &&
                  concurrent.All(x => x.LiveJointObservations > 0 &&
                      x.MaxObservedParentPlusNativeWorkingSetBytes >=
                      x.MaxObservedNativeChildPeakWorkingSetBytes &&
                      x.MaxObservedParentPlusNativeWorkingSetBytes >=
                      x.MaxObservedParentWorkingSetBytes &&
                      x.SourceBytes > x.CandidateBytes &&
                      x.CandidateDecodedPixelsMatch && x.SourceUnchanged),
                "R2.17 image priority: real engine parent+exact-native-child concurrent OS working-set observed");

            // A canceled operation never touches source or produces a valid
            // candidate, and does not affect previous journal snapshots.
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            bool cancelled = false;
            try
            {
                JpegLosslessPreviewEngine.CreatePreview(sources.First(x => x.EndsWith(".jpg")),
                    Path.Combine(root, "concurrent-output-0"), tool, canceled.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled, "R2.17 image priority: pre-canceled JPEG preview aborts safely");

            check(sourceHashes.All(x => Hash(x.Key) == x.Value &&
                  File.GetLastWriteTimeUtc(x.Key).Ticks == sourceTicks[x.Key]),
                "R2.17 image priority: originals SHA-256 and update timestamps unchanged");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText("diagnostics/r217-priority-images-memory.json",
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-priority-matrix-images-memory-v1",
                    officialSourceCommit = Pinned,
                    validWicJpegSources = Count,
                    validUncompressedPngSources = Count,
                    actualPngOptimizedCandidates = pngFirst.Optimized,
                    actualJpegOptimizedCandidates = jpegFirst.Completed,
                    independentlyCheckedPngCandidates = decodedPng,
                    independentlyCheckedJpegCandidates = decodedJpeg,
                    pngPreviewEstimatedSavingBytes = pngFirst.SavedBytes,
                    jpegPreviewEstimatedSavingBytes = jpegFirst.EstimatedSavingBytes,
                    bothBatchesResumedWithoutNewCandidates = true,
                    realJpegEngineUsedForSimultaneousMemory = true,
                    observedExactlySpawnedNativePid = true,
                    sampleParentPlusNativeObservedNotExactProcessLifetimePeak = true,
                    combinedSamples = concurrent,
                    phases,
                    allOriginalHashesAndTimestampsUnchanged = true,
                    canceledPreviewAborted = cancelled,
                    rendererOrPhysicalGuiMouseTested = false,
                    cacheAndCpuControlled = false,
                    independentSpeedupClaimed = false,
                    parentPlusChildPrivateCommitMeasured = false,
                    realUserMediaTouched = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    actualRecoveredBytes = 0,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"R2.17 priority image matrix: 6 real JPEG + 6 real PNG, " +
                $"{jpegFirst.Completed} JPG and {pngFirst.Optimized} PNG candidates; " +
                $"2 parent+child joint-memory samples PASS");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
