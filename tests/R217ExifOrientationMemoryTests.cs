using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// R2.17 part 9: EXIF orientation and a genuine non-sRGB OS profile.
// Disposable generated files only, original replacement always disabled.
internal static class R217ExifOrientationMemoryTests
{
    private const int Width = 512, Height = 320;
    private static readonly int[] Orientations = [1, 3, 6, 8];
    private const string Pinned = "7b4993786d856cbccabfc0ed2f923ba000e326cc";
    private sealed record Image(int Width, int Height, byte[] Pixels);
    private sealed record Sample(string Mode, int Accepted, double EngineMs,
        double AuditMs, long EstimatedCandidateSavingBytes,
        long ManagedBytesCallingThread, long WorkingSetStartBytes,
        long WorkingSetEndBytes, long WorkingSetSampledMaxBytes,
        int WorkingSetSampleCount, long ProcessLifetimePeakWorkingSetBytes);
    private sealed record Exif(int Orientation, byte[] Payload, int ValueOffset);

    private static string Hash(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    private static byte[] ExifPayload(int orientation)
    {
        // Little-endian TIFF header + IFD0 with one SHORT Orientation tag.
        byte[] tiff = [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x12, 0x01, 0x03, 0x00,
            0x01, 0x00, 0x00, 0x00, (byte)orientation, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        return [.. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    private static byte[] Pixels(int index)
    {
        byte[] data = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int at = (y * Width + x) * 4;
            data[at] = (byte)((x / 8 + index * 19) & 255);
            data[at + 1] = (byte)((y / 8 + index * 27) & 255);
            data[at + 2] = (byte)(((x + 2 * y) / 11 + index * 37) & 255);
            data[at + 3] = 255;
        }
        return data;
    }

    private static void AppendSegment(Stream stream, byte marker, byte[] payload)
    {
        int length = checked(payload.Length + 2);
        if (length > ushort.MaxValue)
            throw new InvalidDataException("Generated marker exceeds JPEG segment limit");
        stream.WriteByte(0xff);
        stream.WriteByte(marker);
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(payload);
    }

    private static void GenerateJpeg(string path, int index, int orientation,
        byte[]? alternateIcc = null)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, Pixels(index), Width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 89 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var f = File.Create(path)) encoder.Save(f);
        byte[] generated = File.ReadAllBytes(path);
        if (generated.Length < 10 || generated[0] != 0xff || generated[1] != 0xd8)
            throw new InvalidDataException("Generated JPEG has no SOI");
        int at = 2;
        if (generated[at] == 0xff && generated[at + 1] == 0xe0)
        {
            int n = (generated[at + 2] << 8) | generated[at + 3];
            at = checked(at + n + 2);
            if (at >= generated.Length)
                throw new InvalidDataException("Generated JFIF was truncated");
        }
        using var output = new MemoryStream();
        output.Write(generated.AsSpan(0, at));
        AppendSegment(output, 0xe1, ExifPayload(orientation));
        if (alternateIcc is not null)
        {
            int parts = (alternateIcc.Length + 999) / 1000;
            if (parts < 1 || parts > 255) throw new InvalidDataException("ICC fragment count invalid");
            for (int n = 0; n < parts; n++)
            {
                int bytes = Math.Min(1000, alternateIcc.Length - n * 1000);
                byte[] fragment = [
                    .. "ICC_PROFILE\0"u8.ToArray(),
                    checked((byte)(n + 1)), checked((byte)parts),
                    .. alternateIcc.AsSpan(n * 1000, bytes).ToArray()
                ];
                AppendSegment(output, 0xe2, fragment);
            }
        }
        output.Write(generated.AsSpan(at));
        File.WriteAllBytes(path, output.ToArray());
    }

    // Independent EXIF parser: locate APP1 Exif before SOS, validate TIFF IFD0,
    // tag 274/SHORT/count=1, and recover its exact inline value offset.
    private static Exif ReadExif(string path)
    {
        byte[] jpeg = File.ReadAllBytes(path);
        int index = 2;
        Exif? result = null;
        while (index < jpeg.Length)
        {
            if (jpeg[index++] != 0xff)
                throw new InvalidDataException("Malformed JPEG marker before SOS");
            while (index < jpeg.Length && jpeg[index] == 0xff) index++;
            if (index >= jpeg.Length) throw new InvalidDataException("Truncated JPEG marker");
            int marker = jpeg[index++];
            if (marker is 0xda or 0xd9) break;
            if (index + 2 > jpeg.Length) throw new InvalidDataException("Short marker");
            int len = (jpeg[index] << 8) | jpeg[index + 1];
            if (len < 2 || index + len > jpeg.Length)
                throw new InvalidDataException("Bad JPEG segment length");
            int start = index + 2, size = len - 2;
            if (marker == 0xe1 && size >= 6 &&
                jpeg.AsSpan(start, 6).SequenceEqual("Exif\0\0"u8))
            {
                if (result is not null) throw new InvalidDataException("Duplicate EXIF APP1");
                ReadOnlySpan<byte> tiff = jpeg.AsSpan(start + 6, size - 6);
                if (tiff.Length < 26 || !tiff[..4].SequenceEqual([0x49, 0x49, 0x2a, 0x00]))
                    throw new InvalidDataException("Only valid little-endian test TIFF is accepted");
                int ifd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(tiff[4..8]));
                if (ifd < 8 || ifd + 2 > tiff.Length)
                    throw new InvalidDataException("Exif IFD0 offset invalid");
                int tags = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(ifd, 2));
                int orientation = -1, valueAt = -1;
                for (int t = 0; t < tags; t++)
                {
                    int p = checked(ifd + 2 + t * 12);
                    if (p + 12 > tiff.Length) throw new InvalidDataException("Exif tag out of bounds");
                    if (BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(p, 2)) != 0x0112)
                        continue;
                    if (orientation != -1 ||
                        BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(p + 2, 2)) != 3 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(p + 4, 4)) != 1 ||
                        tiff[p + 10] != 0 || tiff[p + 11] != 0)
                        throw new InvalidDataException("Malformed or duplicate EXIF orientation");
                    orientation = BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(p + 8, 2));
                    valueAt = start + 6 + p + 8;
                }
                if (!Orientations.Contains(orientation))
                    throw new InvalidDataException("Unsupported EXIF orientation in generated fixture");
                result = new Exif(orientation, jpeg.AsSpan(start, size).ToArray(), valueAt);
            }
            index += len;
        }
        return result ?? throw new InvalidDataException("Missing EXIF orientation APP1");
    }

    private static Image DecodeBgr(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames;
        if (frames.Count != 1) throw new InvalidDataException("Generated JPEG frame count invalid");
        var converted = new FormatConvertedBitmap(frames[0], PixelFormats.Bgr24, null, 0);
        int width = converted.PixelWidth, height = converted.PixelHeight;
        byte[] pixels = new byte[checked(width * height * 3)];
        converted.CopyPixels(pixels, checked(width * 3), 0);
        return new Image(width, height, pixels);
    }

    // Explicit orientation normalization, not a viewer-dependent JPEG decoder
    // setting. This verifies the actual on-screen pixel order of Exif values.
    private static Image Orient(Image input, int orientation)
    {
        int w = input.Width, h = input.Height;
        int destW = orientation is 6 or 8 ? h : w;
        int destH = orientation is 6 or 8 ? w : h;
        byte[] rotated = new byte[checked(destW * destH * 3)];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            (int dx, int dy) = orientation switch
            {
                1 => (x, y),
                3 => (w - 1 - x, h - 1 - y),
                6 => (h - 1 - y, x),
                8 => (y, w - 1 - x),
                _ => throw new InvalidDataException("Unexpected orientation")
            };
            input.Pixels.AsSpan((y * w + x) * 3, 3)
                .CopyTo(rotated.AsSpan((dy * destW + dx) * 3, 3));
        }
        return new Image(destW, destH, rotated);
    }

    private static void VerifyCandidate(string source, string candidate, int expected)
    {
        Exif leftExif = ReadExif(source), rightExif = ReadExif(candidate);
        if (leftExif.Orientation != expected || rightExif.Orientation != expected ||
            !leftExif.Payload.AsSpan().SequenceEqual(rightExif.Payload))
            throw new InvalidDataException("Exif orientation tag changed across JPEG candidate");
        if (JpegMarkerAudit.Inspect(source) != JpegMarkerAudit.Inspect(candidate))
            throw new InvalidDataException("JPEG header and APPn protected markers changed");
        var left = DecodeBgr(source);
        var right = DecodeBgr(candidate);
        if (left.Width != Width || left.Height != Height ||
            !left.Pixels.AsSpan().SequenceEqual(right.Pixels))
            throw new InvalidDataException("JPEG raw decoded pixels differ");
        var displayA = Orient(left, expected);
        var displayB = Orient(right, expected);
        if (displayA.Width != (expected is 6 or 8 ? Height : Width) ||
            displayA.Height != (expected is 6 or 8 ? Width : Height) ||
            !displayA.Pixels.AsSpan().SequenceEqual(displayB.Pixels))
            throw new InvalidDataException("Orientation-aware rendered pixels differ");
        if (expected != 1 && displayA.Pixels.AsSpan().SequenceEqual(left.Pixels))
            throw new InvalidDataException("Nontrivial orientation was not applied");
    }

    private static bool IsInvalid(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private sealed class ProcessMemorySampler : IDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;
        private long maximum;
        private int count;
        public ProcessMemorySampler()
        {
            loop = Task.Run(() =>
            {
                using var p = Process.GetCurrentProcess();
                while (!stop.IsCancellationRequested)
                {
                    p.Refresh();
                    long bytes = p.WorkingSet64;
                    long previous;
                    do
                    {
                        previous = Interlocked.Read(ref maximum);
                        if (bytes <= previous) break;
                    }
                    while (Interlocked.CompareExchange(ref maximum, bytes, previous) != previous);
                    Interlocked.Increment(ref count);
                    Thread.Sleep(10);
                }
            });
        }
        public long SampledMax => Interlocked.Read(ref maximum);
        public int Samples => Volatile.Read(ref count);
        public void Dispose()
        {
            stop.Cancel();
            loop.GetAwaiter().GetResult();
            stop.Dispose();
        }
    }

    private static Sample Measure(string root, string input, string tool,
        string mode, IReadOnlyDictionary<string, string> hashes,
        IReadOnlyDictionary<string, long> timestamps)
    {
        string output = Path.Combine(root, "output-" + mode);
        Directory.CreateDirectory(output);
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long starting = self.WorkingSet64;
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        double engineMs = 0, auditMs = 0;
        int accepted = 0;
        long savings = 0;
        using var sampler = new ProcessMemorySampler();
        foreach (int o in Orientations)
        {
            string source = Path.Combine(input, $"orientation-{o}.jpg");
            var clock = Stopwatch.StartNew();
            var result = JpegLosslessPreviewEngine.CreatePreview(source, output, tool);
            clock.Stop(); engineMs += clock.Elapsed.TotalMilliseconds;
            if (result.Candidate is null)
                throw new InvalidDataException("Real JPEG orientation candidate not smaller: " + o);
            accepted++;
            savings += result.EstimatedSavingBytes;
            clock.Restart();
            VerifyCandidate(source, result.Candidate, o);
            clock.Stop(); auditMs += clock.Elapsed.TotalMilliseconds;
        }
        sampler.Dispose();
        self.Refresh();
        long ending = self.WorkingSet64;
        long lifetimePeak = self.PeakWorkingSet64;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        foreach (var file in hashes)
        {
            string original = Path.Combine(input, file.Key);
            if (Hash(original) != file.Value ||
                File.GetLastWriteTimeUtc(original).Ticks != timestamps[file.Key])
                throw new InvalidDataException("Original JPEG was modified");
        }
        return new Sample(mode, accepted, engineMs, auditMs, savings, allocated,
            starting, ending, sampler.SampledMax, sampler.Samples, lifetimePeak);
    }

    private static string? FindAlternateColorProfile(string srgbPath, out byte[] profile)
    {
        string root = Path.GetDirectoryName(srgbPath)!;
        byte[] srgb = File.ReadAllBytes(srgbPath);
        string[] names = [
            "scRGB Color Space Profile.icm", "wscRGB.icc",
            "AdobeRGB1998.icc", "Adobe RGB (1998).icm"
        ];
        foreach (string name in names)
        {
            string file = Path.Combine(root, name);
            if (!File.Exists(file) ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;
            byte[] bytes = File.ReadAllBytes(file);
            if (bytes.Length < 132 || bytes.Length > 128 * 1024 ||
                bytes.AsSpan().SequenceEqual(srgb) ||
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)) != bytes.Length ||
                !bytes.AsSpan(36, 4).SequenceEqual("acsp"u8) ||
                !bytes.AsSpan(16, 4).SequenceEqual("RGB "u8))
                continue;
            try
            {
                var color = new ColorContext(new Uri(file));
                using Stream f = color.OpenProfileStream();
                if (SHA256.HashData(f).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                {
                    profile = bytes;
                    return file;
                }
            }
            catch (Exception ex) when (ex is FileFormatException or IOException or
                NotSupportedException or ArgumentException) { }
        }
        profile = [];
        return null;
    }

    private static byte[] ExtractIcc(string file)
    {
        byte[] data = File.ReadAllBytes(file), tag = "ICC_PROFILE\0"u8.ToArray();
        int offset = 2, count = 0;
        var fragments = new SortedDictionary<int, byte[]>();
        while (offset < data.Length)
        {
            if (data[offset++] != 0xff) throw new InvalidDataException("Unexpected pre-scan JPEG marker");
            while (offset < data.Length && data[offset] == 0xff) offset++;
            if (offset >= data.Length) throw new InvalidDataException("Truncated JPEG marker");
            int marker = data[offset++];
            if (marker is 0xda or 0xd9) break;
            if (offset + 2 > data.Length) throw new InvalidDataException("Truncated segment");
            int len = data[offset] * 256 + data[offset + 1];
            if (len < 2 || offset + len > data.Length)
                throw new InvalidDataException("Malformed APP marker");
            int p = offset + 2;
            if (marker == 0xe2 && len >= tag.Length + 4 &&
                data.AsSpan(p, tag.Length).SequenceEqual(tag))
            {
                int number = data[p + tag.Length], total = data[p + tag.Length + 1];
                if (number < 1 || count != 0 && count != total ||
                    !fragments.TryAdd(number, data.AsSpan(p + tag.Length + 2,
                        len - tag.Length - 4).ToArray()))
                    throw new InvalidDataException("ICC numbering duplicate or mismatch");
                count = total;
            }
            offset += len;
        }
        if (count < 1 || fragments.Count != count)
            throw new InvalidDataException("Incomplete embedded ICC");
        using var all = new MemoryStream();
        for (int i = 1; i <= count; i++)
        {
            if (!fragments.TryGetValue(i, out var payload))
                throw new InvalidDataException("Missing ICC APP2 fragment");
            all.Write(payload);
        }
        return all.ToArray();
    }

    private static (bool Found, string Name, long Bytes, bool Passed) AlternateIcc(
        string root, string input, string tool)
    {
        string colorDir = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.Windows), "System32", "spool", "drivers", "color");
        string srgb = Path.Combine(colorDir, "sRGB Color Space Profile.icm");
        if (!File.Exists(srgb)) return (false, "", 0, false);
        string? chosen = FindAlternateColorProfile(srgb, out var bytes);
        if (chosen is null) return (false, "", 0, false);
        string profileCopy = Path.Combine(root, "alternate-profile.icc");
        File.WriteAllBytes(profileCopy, bytes);
        var inputColor = new ColorContext(new Uri(profileCopy));
        var outputColor = new ColorContext(PixelFormats.Bgra32);
        string alternate = Path.Combine(input, "alternate-profile.jpg");
        GenerateJpeg(alternate, 5, 1, bytes);
        string originalHash = Hash(alternate);
        string outputDir = Path.Combine(root, "alternate-output");
        Directory.CreateDirectory(outputDir);
        var result = JpegLosslessPreviewEngine.CreatePreview(alternate, outputDir, tool);
        if (result.Candidate is null) throw new InvalidDataException("Alternate ICC candidate not smaller");
        if (!ExtractIcc(alternate).AsSpan().SequenceEqual(bytes) ||
            !ExtractIcc(result.Candidate).AsSpan().SequenceEqual(bytes) ||
            ReadExif(alternate).Orientation != 1 ||
            ReadExif(result.Candidate).Orientation != 1)
            throw new InvalidDataException("Alternate real ICC/EXIF marker did not survive");
        var left = ConvertColor(alternate, inputColor, outputColor);
        var right = ConvertColor(result.Candidate, inputColor, outputColor);
        if (!left.Pixels.AsSpan().SequenceEqual(right.Pixels) ||
            left.Width != Width || left.Height != Height ||
            Hash(alternate) != originalHash)
            throw new InvalidDataException("Alternate real ICC WPF conversion is not identical");
        return (true, Path.GetFileName(chosen), bytes.Length, true);
    }

    private static Image ConvertColor(string file, ColorContext source, ColorContext target)
    {
        using var stream = File.OpenRead(file);
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames;
        var converted = new ColorConvertedBitmap(frames[0], source, target, PixelFormats.Bgra32);
        byte[] pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        converted.CopyPixels(pixels, checked(converted.PixelWidth * 4), 0);
        return new Image(converted.PixelWidth, converted.PixelHeight, pixels);
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? baseline = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(baseline))
        {
            Console.WriteLine("R2.17 Exif orientation and memory probe SKIPPED: no verified CI jpegtran");
            return;
        }
        string? simd = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        check(File.Exists(baseline) && File.Exists(simd) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == Pinned &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17: pinned-source and SIMD jpegtran available for Exif orientation test");
        string root = Path.Combine(parent, "r217-exif-orientation-memory-only");
        string input = Path.Combine(root, "generated-sources");
        Directory.CreateDirectory(input);
        try
        {
            for (int i = 0; i < Orientations.Length; i++)
            {
                int orientation = Orientations[i];
                GenerateJpeg(Path.Combine(input, $"orientation-{orientation}.jpg"),
                    i, orientation);
            }
            var originals = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), Hash);
            var timestamps = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), x => File.GetLastWriteTimeUtc(x).Ticks);
            check(Orientations.All(o =>
                ReadExif(Path.Combine(input, $"orientation-{o}.jpg")).Orientation == o),
                "R2.17: actual Exif IFD0 orientation 1/3/6/8 round-tripped in generated JPEG");
            string corrupt = Path.Combine(root, "corrupted-test-only.jpg");
            File.Copy(Path.Combine(input, "orientation-6.jpg"), corrupt);
            Exif exif = ReadExif(corrupt);
            byte[] bytes = File.ReadAllBytes(corrupt);
            bytes[exif.ValueOffset] = 9;  // reserved unsupported EXIF value
            File.WriteAllBytes(corrupt, bytes);
            check(IsInvalid(() => ReadExif(corrupt)),
                "R2.17: EXIF orientation outside supported values rejected");
            File.Copy(Path.Combine(input, "orientation-6.jpg"), corrupt, overwrite: true);
            bytes = File.ReadAllBytes(corrupt);
            bytes[ReadExif(corrupt).ValueOffset] = 1;
            File.WriteAllBytes(corrupt, bytes);
            check(IsInvalid(() => VerifyCandidate(
                Path.Combine(input, "orientation-6.jpg"), corrupt, 6)),
                "R2.17: altered valid EXIF orientation must not pass unchanged-preview oracle");
            Sample b = Measure(root, input, baseline, "baseline", originals, timestamps);
            Sample s = Measure(root, input, simd!, "simd", originals, timestamps);
            check(b.Accepted == 4 && s.Accepted == 4 &&
                  b.EstimatedCandidateSavingBytes > 0 &&
                  s.EstimatedCandidateSavingBytes > 0 &&
                  b.EstimatedCandidateSavingBytes == s.EstimatedCandidateSavingBytes,
                "R2.17: real jpegtran preview retains all four Exif rotations in both builds");
            check(b.WorkingSetSampleCount > 0 && s.WorkingSetSampleCount > 0 &&
                  b.WorkingSetSampledMaxBytes > 0 &&
                  s.WorkingSetSampledMaxBytes > 0 &&
                  b.ProcessLifetimePeakWorkingSetBytes > 0 &&
                  s.ProcessLifetimePeakWorkingSetBytes > 0 &&
                  b.EngineMs > 0 && s.EngineMs > 0 &&
                  b.AuditMs > 0 && s.AuditMs > 0,
                "R2.17: bounded whole-process working-set samples and separate phase timings");
            var alternative = AlternateIcc(root, input, simd!);
            check(!alternative.Found || alternative.Passed,
                "R2.17: if a trusted alternate OS RGB ICC exists its actual WPF color transform matches");
            check(originals.All(x => Hash(Path.Combine(input, x.Key)) == x.Value &&
                  File.GetLastWriteTimeUtc(Path.Combine(input, x.Key)).Ticks == timestamps[x.Key]),
                "R2.17: unchanged original synthetic Exif JPEG hashes and timestamps");
            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-exif-orientation-memory.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-exif-orientation-and-process-memory-v1",
                    sourceCommit = Pinned,
                    syntheticJpegCount = Orientations.Length,
                    exifOrientations = Orientations,
                    exifLittleEndianIfd0ActuallyParsed = true,
                    invalidAndChangedOrientationRejected = true,
                    allKeptCandidatesRawAndOrientedPixelsMatch = true,
                    allProtectedJpegMarkersMatch = true,
                    allOriginalHashesAndTimestampsUnchanged = true,
                    sourceBuiltNonSimdAndSimdCandidatesBothVerified = true,
                    baseline = b,
                    simd = s,
                    nonSrgbWindowsSystemProfileFound = alternative.Found,
                    nonSrgbProfileName = alternative.Name,
                    nonSrgbProfileBytes = alternative.Bytes,
                    nonSrgbWicColorTransformPassed = alternative.Passed,
                    noAlternativeProfileImpliesNonSrgbCoverageStillPending = !alternative.Found,
                    workingSetSamplingIntervalMs = 10,
                    sampledWholeTestProcessNotJpegtranChild = true,
                    processLifetimePeakNotPerOperation = true,
                    shortMemoryPeaksMayBeMissed = true,
                    actualJpegtranChildPeakMeasured = false,
                    coldCacheControlled = false,
                    physicalDiskReadsMeasured = false,
                    realPhotosTested = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    actualFreedBytes = 0,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"R2.17 Exif orientation 1/3/6/8 synthetic previews PASS: " +
                $"baseline {b.EngineMs:F1}ms, SIMD {s.EngineMs:F1}ms; " +
                $"alternate system ICC: {(alternative.Found ? "verified" : "unavailable")}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
