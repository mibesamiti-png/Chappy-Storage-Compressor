using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// R2.17 part 8: use a REAL Windows-provided color profile in a generated
// JPEG fixture; do not copy copyrighted ICC bytes into Git or app artifacts.
// Test only disposable synthetic JPEG images, no user media.
internal static class R217RealIccColorTests
{
    private const int Count = 4;
    private const int Width = 640;
    private const int Height = 480;
    private const string PinnedCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";
    private const string IccId = "ICC_PROFILE\0";
    private const int MaxProfileSize = 128 * 1024;
    private const int ChunkSize = 1024;

    private sealed record IccPart(int Number, int Total, byte[] Data);
    private sealed record RunEvidence(
        string Mode, int RunNumber, int Accepted, long SourceBytes,
        long PreviewBytes, double ProductionEngineMs, double IndependentAuditMs,
        long ManagedAllocatedBytesOnTestThread,
        long ProcessWorkingSetBeforeBytes, long ProcessWorkingSetAfterBytes,
        string ExecutableSha256);

    private static string Sha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FindWindowsSrgbProfile()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Actual Windows sRGB ICC test requires Windows");
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "spool", "drivers", "color");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(
            "Windows standard color-profile folder is missing");
        // Restrict provenance to Windows' installed sRGB profile, never pick
        // arbitrary *.icm or a profile from a user-controlled directory.
        string[] preferred = [
            Path.Combine(dir, "sRGB Color Space Profile.icm"),
            Path.Combine(dir, "sRGB IEC61966-2.1.icm")
        ];
        string? found = preferred.FirstOrDefault(File.Exists);
        if (found is null)
            throw new FileNotFoundException("Known Windows sRGB system profile is not installed");
        if ((File.GetAttributes(found) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("ICC profile reparse points are not accepted");
        return found;
    }

    private static void ValidateIccHeader(byte[] data)
    {
        if (data.Length < 132 || data.Length > MaxProfileSize)
            throw new InvalidDataException("Unexpected size for Windows installed ICC profile");
        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)) != data.Length)
            throw new InvalidDataException("ICC header size differs from full profile");
        if (!data.AsSpan(36, 4).SequenceEqual("acsp"u8))
            throw new InvalidDataException("ICC signature missing");
        if (!data.AsSpan(16, 4).SequenceEqual("RGB "u8))
            throw new InvalidDataException("Expected Windows sRGB ICC profile");
        if (!data.AsSpan(20, 4).SequenceEqual("XYZ "u8) &&
            !data.AsSpan(20, 4).SequenceEqual("Lab "u8))
            throw new InvalidDataException("ICC PCS is not recognizable");
        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128, 4)) == 0)
            throw new InvalidDataException("ICC profile has no tag table");
        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(128, 4)) > 1024)
            throw new InvalidDataException("ICC tag count unreasonable");
    }

    private static byte[] Pixels(int index)
    {
        var pixels = new byte[checked(Width * Height * 4)];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int at = (y * Width + x) * 4;
            pixels[at] = (byte)((x / 17 + index * 13) & 255);
            pixels[at + 1] = (byte)((y / 19 + index * 19) & 255);
            pixels[at + 2] = (byte)(((x + y) / 23 + index * 17) & 255);
            pixels[at + 3] = 255;
        }
        return pixels;
    }

    private static IReadOnlyList<byte[]> IccMarkers(byte[] profile)
    {
        int count = (profile.Length + ChunkSize - 1) / ChunkSize;
        if (count < 2 || count > 255)
            throw new InvalidDataException("Profile must be split into 2..255 ICC APP2 chunks");
        var markers = new List<byte[]>(count);
        byte[] prefix = Encoding.ASCII.GetBytes(IccId);
        for (int n = 0; n < count; n++)
        {
            int size = Math.Min(ChunkSize, profile.Length - n * ChunkSize);
            var payload = new byte[prefix.Length + 2 + size];
            prefix.CopyTo(payload.AsSpan());
            payload[prefix.Length] = checked((byte)(n + 1));
            payload[prefix.Length + 1] = checked((byte)count);
            profile.AsSpan(n * ChunkSize, size).CopyTo(payload.AsSpan(prefix.Length + 2));
            markers.Add(payload);
        }
        return markers;
    }

    private static void WriteJpeg(string file, int index, byte[] profile)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96,
            PixelFormats.Bgra32, null, Pixels(index), checked(Width * 4));
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var target = File.Create(file)) encoder.Save(target);
        byte[] original = File.ReadAllBytes(file);
        if (original.Length < 10 || original[0] != 0xff || original[1] != 0xd8)
            throw new InvalidDataException("Generated JPEG has no SOI");
        int offset = 2;
        if (original[offset] == 0xff && original[offset + 1] == 0xe0)
        {
            int segmentLength = original[offset + 2] * 256 + original[offset + 3];
            offset = checked(offset + segmentLength + 2);
            if (offset >= original.Length) throw new InvalidDataException("Bad JFIF");
        }
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, offset));
        foreach (byte[] payload in IccMarkers(profile))
        {
            int segmentLength = checked(payload.Length + 2);
            if (segmentLength > ushort.MaxValue)
                throw new InvalidDataException("ICC APP2 segment too large");
            output.WriteByte(0xff);
            output.WriteByte(0xe2);
            output.WriteByte((byte)(segmentLength >> 8));
            output.WriteByte((byte)segmentLength);
            output.Write(payload);
        }
        // Extra synthetic Exif COM ensures coexisting marker preservation.
        if (index % 2 == 0)
        {
            byte[] comment = Encoding.ASCII.GetBytes("Chappy R217 synthetic ICC profile test");
            output.WriteByte(0xff);
            output.WriteByte(0xfe);
            output.WriteByte(0);
            output.WriteByte(checked((byte)(comment.Length + 2)));
            output.Write(comment);
        }
        output.Write(original.AsSpan(offset));
        File.WriteAllBytes(file, output.ToArray());
    }

    // Independent ICC APP2 parsing, no call into the app's JpegMarkerAudit.
    private static List<IccPart> ExtractParts(string jpeg)
    {
        byte[] file = File.ReadAllBytes(jpeg);
        if (file.Length < 10 || file[0] != 0xff || file[1] != 0xd8)
            throw new InvalidDataException("No JPEG SOI");
        byte[] magic = Encoding.ASCII.GetBytes(IccId);
        var result = new List<IccPart>();
        int offset = 2;
        while (offset < file.Length)
        {
            if (file[offset++] != 0xff)
                throw new InvalidDataException("Malformed marker before JPEG scan");
            while (offset < file.Length && file[offset] == 0xff) offset++;
            if (offset >= file.Length) throw new InvalidDataException("Truncated marker");
            int marker = file[offset++];
            if (marker == 0xda || marker == 0xd9) break;
            if (offset + 2 > file.Length) throw new InvalidDataException("Truncated segment");
            int size = (file[offset] << 8) | file[offset + 1];
            if (size < 2 || offset + size > file.Length)
                throw new InvalidDataException("Malformed segment size");
            if (marker == 0xe2 &&
                size >= magic.Length + 4 &&
                file.AsSpan(offset + 2, magic.Length).SequenceEqual(magic))
            {
                int cursor = offset + 2 + magic.Length;
                int number = file[cursor], total = file[cursor + 1];
                var chunk = file.AsSpan(cursor + 2,
                    size - 2 - magic.Length - 2).ToArray();
                result.Add(new IccPart(number, total, chunk));
            }
            offset += size;
        }
        return result;
    }

    private static byte[] Reassemble(IReadOnlyList<IccPart> chunks)
    {
        if (chunks.Count < 2 || chunks.Count > 255)
            throw new InvalidDataException("ICC APP2 chunk count invalid");
        int total = chunks[0].Total;
        if (total != chunks.Count)
            throw new InvalidDataException("ICC APP2 chunk missing or excess");
        // Require the exact sequence in marker order. Merely having the same
        // set of chunks is not sufficient for our byte-retention oracle.
        using var stream = new MemoryStream();
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].Total != total || chunks[i].Number != i + 1 ||
                chunks[i].Data.Length == 0)
                throw new InvalidDataException("Out-of-order or duplicate ICC APP2");
            stream.Write(chunks[i].Data);
        }
        return stream.ToArray();
    }

    private static bool ThrowsInvalid(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
    }

    private static (int Width, int Height, byte[] Bytes) ConvertColor(
        string file, ColorContext inputProfile, ColorContext outputProfile)
    {
        using var stream = new FileStream(file, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
            throw new InvalidDataException("Expected one WIC JPEG frame");
        // The color profile has been independently reassembled and validated.
        // Explicitly run the real Windows WIC color conversion, rather than
        // equating raw ICC marker retention with color fidelity.
        var converted = new ColorConvertedBitmap(decoder.Frames[0],
            inputProfile, outputProfile, PixelFormats.Bgra32);
        int stride = checked(converted.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static RunEvidence Measure(string root, string input, string tool,
        string mode, int pass, byte[] profile, ColorContext srcColor,
        ColorContext dstColor, IReadOnlyDictionary<string, string> hashes,
        IReadOnlyDictionary<string, long> timestamps)
    {
        string output = Path.Combine(root, "output-" + mode + "-" + pass);
        Directory.CreateDirectory(output);
        double engineMs = 0, auditMs = 0;
        int accepted = 0;
        long sourceBytes = 0, candidateBytes = 0;
        long allocationsBefore = GC.GetAllocatedBytesForCurrentThread();
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        long workingBefore = self.WorkingSet64;

        foreach (string source in Directory.EnumerateFiles(input, "*.jpg")
            .OrderBy(x => x, StringComparer.Ordinal))
        {
            sourceBytes = checked(sourceBytes + new FileInfo(source).Length);
            var engineClock = Stopwatch.StartNew();
            var candidate = JpegLosslessPreviewEngine.CreatePreview(source, output, tool);
            engineClock.Stop();
            engineMs += engineClock.Elapsed.TotalMilliseconds;
            if (candidate.Candidate is null) continue;
            accepted++;
            candidateBytes = checked(candidateBytes + candidate.CandidateBytes);

            var auditClock = Stopwatch.StartNew();
            byte[] origIcc = Reassemble(ExtractParts(source));
            byte[] optimizedIcc = Reassemble(ExtractParts(candidate.Candidate));
            if (!origIcc.AsSpan().SequenceEqual(profile) ||
                !optimizedIcc.AsSpan().SequenceEqual(profile))
                throw new InvalidDataException("Real embedded ICC profile bytes changed");
            if (JpegMarkerAudit.Inspect(source) != JpegMarkerAudit.Inspect(candidate.Candidate))
                throw new InvalidDataException("JPEG protected metadata snapshot changed");

            var origPixels = ConvertColor(source, srcColor, dstColor);
            var optimizedPixels = ConvertColor(candidate.Candidate, srcColor, dstColor);
            if (origPixels.Width != Width || origPixels.Height != Height ||
                optimizedPixels.Width != origPixels.Width ||
                optimizedPixels.Height != origPixels.Height ||
                !origPixels.Bytes.AsSpan().SequenceEqual(optimizedPixels.Bytes))
                throw new InvalidDataException("WIC color-managed BGRA image changed");
            auditClock.Stop();
            auditMs += auditClock.Elapsed.TotalMilliseconds;
        }
        self.Refresh();
        long workingAfter = self.WorkingSet64;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationsBefore;
        foreach (var hash in hashes)
        {
            string source = Path.Combine(input, hash.Key);
            if (Sha(source) != hash.Value ||
                File.GetLastWriteTimeUtc(source).Ticks != timestamps[hash.Key])
                throw new InvalidDataException("Source bytes or timestamp changed");
        }
        return new RunEvidence(mode, pass, accepted, sourceBytes, candidateBytes,
            engineMs, auditMs, allocated, workingBefore, workingAfter, Sha(tool));
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? baseline = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(baseline))
        {
            Console.WriteLine("R2.17 real ICC color profile test SKIPPED: no pinned CI tool");
            return;
        }
        string? simd = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        check(File.Exists(baseline) && File.Exists(simd) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == PinnedCommit &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17: source-verified official jpegtran non-SIMD and SIMD provided");

        string root = Path.Combine(parent, "r217-real-icc-profile-only");
        string input = Path.Combine(root, "generated-jpeg");
        Directory.CreateDirectory(input);
        try
        {
            string systemProfilePath = FindWindowsSrgbProfile();
            byte[] profile = File.ReadAllBytes(systemProfilePath);
            ValidateIccHeader(profile);
            // The OS-installed ICC is used only in the runner; do not include
            // raw profile bytes or the profile itself in diagnostics/artifacts.
            string profileCopy = Path.Combine(root, "test-only-srgb.icm");
            File.WriteAllBytes(profileCopy, profile);
            var sourceColor = new ColorContext(new Uri(profileCopy));
            using (Stream colorContextStream = sourceColor.OpenProfileStream())
            {
                if (!SHA256.HashData(colorContextStream).AsSpan()
                    .SequenceEqual(SHA256.HashData(profile)))
                    throw new InvalidDataException("WPF did not open the same genuine ICC profile");
            }
            var outputColor = new ColorContext(PixelFormats.Bgra32);
            check(profile.Length >= 132 && profile.Length <= MaxProfileSize &&
                  BinaryPrimitives.ReadUInt32BigEndian(profile.AsSpan(128, 4)) > 0,
                "R2.17: Windows-installed real sRGB ICC profile validates and WPF opens it");

            byte[] probe = Reassemble(IccMarkers(profile).Select((x, i) =>
                new IccPart(i + 1, IccMarkers(profile).Count,
                    x.AsSpan(Encoding.ASCII.GetByteCount(IccId) + 2).ToArray())).ToArray());
            check(probe.AsSpan().SequenceEqual(profile) &&
                  IccMarkers(profile).Count >= 2,
                "R2.17: real ICC APP2 profile is round-tripped over ordered segments");

            var chunks = ExtractPrototypeParts(profile);
            check(ThrowsInvalid(() => Reassemble(
                      [chunks[0], chunks[0], .. chunks.Skip(2)])) &&
                  ThrowsInvalid(() => Reassemble(chunks.Take(chunks.Count - 1).ToArray())),
                "R2.17: independent ICC oracle rejects duplicate and missing fragments");

            for (int i = 0; i < Count; i++)
                WriteJpeg(Path.Combine(input, $"synthetic-{i:D2}.jpg"), i, profile);
            var originals = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), Sha, StringComparer.Ordinal);
            var timestamps = Directory.EnumerateFiles(input, "*.jpg")
                .ToDictionary(x => Path.GetFileName(x), x => File.GetLastWriteTimeUtc(x).Ticks,
                    StringComparer.Ordinal);
            var firstSource = Directory.EnumerateFiles(input, "*.jpg").First();
            var firstDecoded = ConvertColor(firstSource, sourceColor, outputColor);
            check(originals.Count == Count && firstDecoded.Width == Width &&
                  firstDecoded.Height == Height && firstDecoded.Bytes.Length ==
                  checked(Width * Height * 4) &&
                  Directory.EnumerateFiles(input, "*.jpg").All(x =>
                      Reassemble(ExtractParts(x)).AsSpan().SequenceEqual(profile)),
                "R2.17: four real ICC-tagged synthetic JPEGs decode with WIC color transform");

            // The same profile and sources are used in all four runs. This
            // samples process-level resource changes and phase wall-times,
            // NOT isolated peak native memory nor cold-cache measurements.
            RunEvidence[] tests = [
                Measure(root, input, baseline, "baseline", 1, profile, sourceColor,
                    outputColor, originals, timestamps),
                Measure(root, input, simd!, "simd", 1, profile, sourceColor,
                    outputColor, originals, timestamps),
                Measure(root, input, simd!, "simd", 2, profile, sourceColor,
                    outputColor, originals, timestamps),
                Measure(root, input, baseline, "baseline", 2, profile, sourceColor,
                    outputColor, originals, timestamps)
            ];
            check(tests.All(x => x.Accepted == Count && x.CandidateBytes > 0 &&
                  x.CandidateBytes < x.SourceBytes),
                "R2.17: both pinned jpegtran variants keep all real ICC color-managed previews");
            check(tests.All(x => x.ProductionEngineMs > 0 &&
                  x.IndependentAuditMs > 0 &&
                  x.ManagedAllocatedBytesOnTestThread >= 0 &&
                  x.ProcessWorkingSetBeforeBytes > 0 &&
                  x.ProcessWorkingSetAfterBytes > 0),
                "R2.17: engine/audit phases, thread allocations and process working set sampled");
            check(tests[0].ExecutableSha256 == tests[3].ExecutableSha256 &&
                  tests[1].ExecutableSha256 == tests[2].ExecutableSha256 &&
                  tests[0].ExecutableSha256 != tests[1].ExecutableSha256 &&
                  tests.Select(x => x.CandidateBytes).Distinct().Count() == 1,
                "R2.17: source-built SIMD and baseline fingerprints differ but preview totals agree");
            check(originals.All(x => Sha(Path.Combine(input, x.Key)) == x.Value &&
                  File.GetLastWriteTimeUtc(Path.Combine(input, x.Key)).Ticks ==
                  timestamps[x.Key]),
                "R2.17: all synthetic JPEG original bytes and timestamps remain unchanged");

            Directory.CreateDirectory("diagnostics");
            File.WriteAllText(Path.Combine("diagnostics", "r217-real-icc-color.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-real-windows-icc-v1",
                    upstreamVersion = "libjpeg-turbo 3.2.1",
                    officialSourceCommit = PinnedCommit,
                    generatedOnly = true,
                    windowsInstalledColorProfileUsed = true,
                    systemProfileName = Path.GetFileName(systemProfilePath),
                    actualProfileBytes = profile.Length,
                    profileSha256 = Convert.ToHexString(SHA256.HashData(profile)),
                    profileIccHeaderAndTagCountValidated = true,
                    wpfColorContextOpenedOriginalProfile = true,
                    colorConvertedBitmapActuallyInvoked = true,
                    profileSplitIntoMultipleIccApp2Segments = true,
                    duplicateAndMissingApp2SegmentsRejected = true,
                    syntheticJpegCount = Count,
                    sourceHashesAndTimestampsUnchanged = true,
                    toolVariants = "baseline/simd",
                    runOrder = "baseline1-simd1-simd2-baseline2",
                    runs = tests,
                    productionEngineAndIndependentColorAuditMeasuredSeparately = true,
                    singleTestThreadAllocationSampleNotPeakMemory = true,
                    processWorkingSetSnapshotsNotAttributable = true,
                    coldAndWarmCacheNotControlled = true,
                    memoryPeakPerOperationMeasured = false,
                    measuredSimdSpeedupGuaranteed = false,
                    realPhotosTested = false,
                    actualRecoveredBytes = 0,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    realMediaTouched = false,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"R2.17 genuine sRGB ICC color profile PASS: {Count} generated JPEGs x 4 " +
                $"production-engine passes; {profile.Length}B OS ICC; no user files");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IccPart[] ExtractPrototypeParts(byte[] profile) =>
        IccMarkers(profile).Select((bytes, i) =>
            new IccPart(i + 1, IccMarkers(profile).Count,
                bytes.AsSpan(Encoding.ASCII.GetByteCount(IccId) + 2).ToArray())).ToArray();
}
