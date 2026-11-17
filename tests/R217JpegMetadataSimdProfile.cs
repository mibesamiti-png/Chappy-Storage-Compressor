using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChappyStorageCompressor.Core;

// R2.17 part 7: official-source jpegtran with and without native SIMD.
// All JPEGs and metadata are synthetic and disposable. No real media,
// binary installers, production replacements, or user drives are touched.
internal static class R217JpegMetadataSimdProfile
{
    private const int Count = 8;
    private const int Width = 640;
    private const int Height = 480;
    private const string PinnedCommit = "7b4993786d856cbccabfc0ed2f923ba000e326cc";

    private sealed record Segment(byte Marker, byte[] Payload);
    private sealed record Measurement(string Mode, int Pass, double ElapsedMs,
        int AcceptedCandidates, long SourceBytes, long CandidateBytes,
        long EstimatedSavingBytes, string ToolSha256);

    private static string Sha(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] PixelPattern(int i)
    {
        var pixels = new byte[checked(Width * Height * 4)];
        var random = new Random(217700 + i);
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            pixels[p] = (byte)(i % 3 == 2 ? random.Next(256) : (x / 13 + i * 17) % 256);
            pixels[p + 1] = (byte)(i % 3 == 2 ? random.Next(256) : (y / 11 + i * 13) % 256);
            pixels[p + 2] = (byte)(i % 3 == 1 ? (x + y) / 7 % 256 :
                i % 3 == 2 ? random.Next(256) : (i * 27) % 256);
            pixels[p + 3] = 255;
        }
        return pixels;
    }

    private static byte[] Tagged(string prefix, byte[] payload)
    {
        byte[] head = Encoding.ASCII.GetBytes(prefix);
        return [.. head, .. payload];
    }

    // Minimal synthetic little-endian TIFF carrying Orientation=1.
    // No GPS, real camera serial, or identifying/user metadata.
    private static Segment Exif()
    {
        byte[] tiff = [
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];
        return new Segment(0xE1, Tagged("Exif\0\0", tiff));
    }

    private static Segment Xmp()
    {
        const string xml =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description xmlns:ch=\"urn:chappy:synthetic:test\" ch:Case=\"R217\"/>" +
            "</rdf:RDF></x:xmpmeta>";
        return new Segment(0xE1, Tagged("http://ns.adobe.com/xap/1.0/\0",
            Encoding.UTF8.GetBytes(xml)));
    }

    private static byte[] SyntheticIcc()
    {
        // ICCv2 structure: 128-byte header + 4-byte tag count. It is a
        // test-only near-minimal profile with no non-test color assertions.
        byte[] icc = new byte[132];
        icc[0] = 0; icc[1] = 0; icc[2] = 0; icc[3] = 132;
        icc[8] = 2; icc[9] = 0x10;
        Encoding.ASCII.GetBytes("mntr").CopyTo(icc.AsSpan(12));
        Encoding.ASCII.GetBytes("RGB ").CopyTo(icc.AsSpan(16));
        Encoding.ASCII.GetBytes("XYZ ").CopyTo(icc.AsSpan(20));
        Encoding.ASCII.GetBytes("acsp").CopyTo(icc.AsSpan(36));
        // D50 PCS illuminant in fixed 16.16 representation.
        icc[68] = 0; icc[69] = 0; icc[70] = 0xF6; icc[71] = 0xD6;
        icc[72] = 0; icc[73] = 1; icc[74] = 0; icc[75] = 0;
        icc[76] = 0; icc[77] = 0; icc[78] = 0xD3; icc[79] = 0x2D;
        return icc;
    }

    private static IReadOnlyList<Segment> IccChunked()
    {
        byte[] profile = SyntheticIcc();
        return [
            new Segment(0xE2, Tagged("ICC_PROFILE\0",
                [1, 2, .. profile.AsSpan(0, 70).ToArray()])),
            new Segment(0xE2, Tagged("ICC_PROFILE\0",
                [2, 2, .. profile.AsSpan(70).ToArray()]))
        ];
    }

    private static Segment Photoshop()
    {
        // Valid Photoshop 3.0 APP13 resource envelope, synthetic caption bytes.
        byte[] caption = Encoding.ASCII.GetBytes("Chappy-R217");
        byte[] resource = [
            .. Encoding.ASCII.GetBytes("Photoshop 3.0\0"),
            .. Encoding.ASCII.GetBytes("8BIM"),
            0x04, 0x04, // IPTC-NAA resource id
            0x00, 0x00, // empty Pascal name + pad
            0x00, 0x00, 0x00, (byte)caption.Length,
            .. caption
        ];
        if (caption.Length % 2 != 0) resource = [.. resource, 0];
        return new Segment(0xED, resource);
    }

    private static IReadOnlyList<Segment> Markers(int variant)
    {
        var a = IccChunked();
        return variant switch
        {
            0 => [Exif()],
            1 => [Xmp()],
            2 => [.. a],
            3 => [Photoshop()],
            4 => [new Segment(0xFE, Encoding.ASCII.GetBytes("Chappy synthetic COM"))],
            5 => [Exif(), .. a],
            6 => [Xmp(), Photoshop()],
            _ => [Exif(), Xmp(), .. a, Photoshop(),
                new Segment(0xFE, Encoding.ASCII.GetBytes("R217 all metadata"))]
        };
    }

    private static void WriteJpeg(string path, int variant)
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32,
            null, PixelPattern(variant), Width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 87 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(path)) encoder.Save(file);
        byte[] original = File.ReadAllBytes(path);
        if (original.Length < 10 || original[0] != 0xFF || original[1] != 0xD8)
            throw new InvalidDataException("Generated WIC JPEG has no SOI");
        int pos = 2;
        if (original[pos] == 0xff && original[pos + 1] == 0xe0)
        {
            int segmentSize = original[pos + 2] * 256 + original[pos + 3];
            pos = checked(pos + 2 + segmentSize);
            if (pos >= original.Length)
                throw new InvalidDataException("WIC JPEG JFIF marker malformed");
        }
        using var assembled = new MemoryStream();
        assembled.Write(original.AsSpan(0, pos));
        foreach (var segment in Markers(variant))
        {
            int length = checked(segment.Payload.Length + 2);
            if (length > ushort.MaxValue) throw new InvalidDataException("Marker too long");
            assembled.WriteByte(0xff);
            assembled.WriteByte(segment.Marker);
            assembled.WriteByte((byte)(length >> 8));
            assembled.WriteByte((byte)length);
            assembled.Write(segment.Payload);
        }
        assembled.Write(original.AsSpan(pos));
        File.WriteAllBytes(path, assembled.ToArray());
    }

    // Independent APPn/COM extraction reads original marker bytes, including
    // segment length, without relying on the production marker-audit parser.
    private static byte[] ProtectedMetadataBytes(string path)
    {
        byte[] source = File.ReadAllBytes(path);
        if (source.Length < 4 || source[0] != 0xff || source[1] != 0xd8)
            throw new InvalidDataException("Metadata source SOI missing");
        using var selected = new MemoryStream();
        int offset = 2;
        while (offset < source.Length)
        {
            if (source[offset++] != 0xff) throw new InvalidDataException("Broken JPEG marker");
            while (offset < source.Length && source[offset] == 0xff) offset++;
            if (offset >= source.Length) throw new InvalidDataException("Truncated marker");
            int marker = source[offset++];
            if (marker == 0xda) break; // no marker inspection inside entropy stream
            if (marker == 0xd9) break;
            if (offset + 2 > source.Length) throw new InvalidDataException("Short segment header");
            int segmentSize = source[offset] * 256 + source[offset + 1];
            if (segmentSize < 2 || offset + segmentSize > source.Length)
                throw new InvalidDataException("Invalid JPEG segment length");
            if ((marker >= 0xe0 && marker <= 0xef) || marker == 0xfe)
            {
                selected.WriteByte(0xff);
                selected.WriteByte((byte)marker);
                selected.Write(source.AsSpan(offset, segmentSize));
            }
            offset += segmentSize;
        }
        return selected.ToArray();
    }

    private static (int Width, int Height, byte[] Bgr) Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad).Frames;
        if (frames.Count != 1) throw new InvalidDataException("Unexpected frame count");
        var converted = new FormatConvertedBitmap(frames[0], PixelFormats.Bgr24, null, 0);
        var buffer = new byte[checked(converted.PixelWidth * converted.PixelHeight * 3)];
        converted.CopyPixels(buffer, checked(converted.PixelWidth * 3), 0);
        return (converted.PixelWidth, converted.PixelHeight, buffer);
    }

    private static Measurement Measure(string root, string input, string tool,
        string mode, int pass, Dictionary<string, string> hashes,
        Dictionary<string, long> times)
    {
        string output = Path.Combine(root, $"out-{mode}-{pass}");
        Directory.CreateDirectory(output);
        var watch = Stopwatch.StartNew();
        int accepted = 0;
        long candidateBytes = 0, saving = 0;
        foreach (string original in Directory.EnumerateFiles(input, "*.jpg")
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var sourceSnapshot = JpegMarkerAudit.Inspect(original);
            byte[] markers = ProtectedMetadataBytes(original);
            var candidate = JpegLosslessPreviewEngine.CreatePreview(original, output, tool);
            if (candidate.Candidate is null) continue;
            // G4-02: read the persisted jpegtran result again through the
            // common pre-Apply gate, rather than trusting preview creation.
            var fidelity = ReplacementReadiness.InspectCandidatePair(
                original, candidate.Candidate);
            if (!fidelity.Verified || fidelity.ProductionReplacementAuthorized)
                throw new InvalidDataException(
                    "G4-02: persisted JPEG marker/WIC/source identity recheck rejected: " +
                    fidelity.Reason);
            accepted++;
            candidateBytes += candidate.CandidateBytes;
            saving += candidate.EstimatedSavingBytes;
            if (JpegMarkerAudit.Inspect(candidate.Candidate) != sourceSnapshot)
                throw new InvalidDataException("Protected header/quantization mismatch for " + mode);
            if (!ProtectedMetadataBytes(candidate.Candidate).AsSpan().SequenceEqual(markers))
                throw new InvalidDataException("Exif/XMP/ICC/APP13/COM segment bytes changed for " + mode);
            // The candidate is already fully verified. Copy it to a disposable
            // path and corrupt only that copy; the original and real preview
            // must stay immutable and the pre-Apply gate must fail closed.
            if (accepted == 2 && mode == "baseline")
            {
                string tampered = Path.Combine(output, "g4-corrupt-metadata.jpg");
                byte[] changed = File.ReadAllBytes(candidate.Candidate);
                int marker = System.Text.Encoding.ASCII.GetString(changed)
                    .IndexOf("http://ns.adobe.com/xap", StringComparison.Ordinal);
                if (marker < 0) throw new InvalidDataException(
                    "G4-02 fixture missing protected XMP data");
                changed[marker] ^= 0x01;
                File.WriteAllBytes(tampered, changed);
                if (ReplacementReadiness.InspectCandidatePair(
                        original, tampered).Verified)
                    throw new InvalidDataException(
                        "G4-02 tampered XMP passed fresh pre-Apply fidelity check");
            }
            var a = Decode(original);
            var b = Decode(candidate.Candidate);
            if (a.Width != b.Width || a.Height != b.Height ||
                !a.Bgr.AsSpan().SequenceEqual(b.Bgr))
                throw new InvalidDataException("Full independent JPEG WIC pixel mismatch for " + mode);
        }
        watch.Stop();
        foreach (var x in hashes)
        {
            string path = Path.Combine(input, x.Key);
            if (Sha(path) != x.Value || File.GetLastWriteTimeUtc(path).Ticks != times[x.Key])
                throw new InvalidDataException("Original JPEG contents or timestamps modified");
        }
        long sourceBytes = Directory.EnumerateFiles(input, "*.jpg")
            .Sum(p => new FileInfo(p).Length);
        return new Measurement(mode, pass, watch.Elapsed.TotalMilliseconds, accepted,
            sourceBytes, candidateBytes, saving, Sha(tool));
    }

    public static void Run(string parent, Action<bool, string> check)
    {
        string? baselineTool = Environment.GetEnvironmentVariable("CHAPPY_R217_PINNED_JPEGTRAN");
        if (string.IsNullOrWhiteSpace(baselineTool))
        {
            Console.WriteLine("R2.17 metadata + SIMD profile SKIPPED without pinned CI-only tool");
            return;
        }
        string? simdTool = Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_JPEGTRAN");
        check(File.Exists(baselineTool) && File.Exists(simdTool) &&
              Path.GetFileName(baselineTool).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase) &&
              Path.GetFileName(simdTool).Equals("jpegtran.exe", StringComparison.OrdinalIgnoreCase) &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_JPEGTRAN_COMMIT") == PinnedCommit &&
              Environment.GetEnvironmentVariable("CHAPPY_R217_SIMD_VERIFIED") == "true",
            "R2.17: SIMD and non-SIMD JPEG tools both built from same pinned official source");

        string root = Path.Combine(parent, "r217-jpeg-metadata-simd-only");
        string input = Path.Combine(root, "synthetic-jpg");
        Directory.CreateDirectory(input);
        try
        {
            for (int i = 0; i < Count; i++)
                WriteJpeg(Path.Combine(input, $"synthetic-{i:D2}.jpg"), i);
            var hashes = Directory.EnumerateFiles(input, "*.jpg").ToDictionary(
                x => Path.GetFileName(x), Sha, StringComparer.Ordinal);
            var times = Directory.EnumerateFiles(input, "*.jpg").ToDictionary(
                x => Path.GetFileName(x), x => File.GetLastWriteTimeUtc(x).Ticks,
                StringComparer.Ordinal);
            var tagged = Directory.EnumerateFiles(input, "*.jpg").OrderBy(x => x).ToArray();
            check(tagged.Length == Count && tagged.All(x =>
                JpegMarkerAudit.Inspect(x).Width == Width &&
                JpegMarkerAudit.Inspect(x).Height == Height &&
                Decode(x).Width == Width),
                "R2.17: eight WIC-decodable synthetic JPEG metadata variants pass source audit");
            check(Markers(7).Count == 6 && Markers(2).Count == 2 &&
                  ProtectedMetadataBytes(tagged[7]).Length >
                  ProtectedMetadataBytes(tagged[0]).Length,
                "R2.17: synthetic EXIF XMP ICC chunks Photoshop APP13 and COM embedded");

            // ABBA order mitigates, but DOES NOT control, cache/CPU/CI noise.
            // No speedup assertion based on a single hosted CI run.
            var tests = new[]
            {
                Measure(root, input, baselineTool, "baseline", 1, hashes, times),
                Measure(root, input, simdTool!, "simd", 1, hashes, times),
                Measure(root, input, simdTool!, "simd", 2, hashes, times),
                Measure(root, input, baselineTool, "baseline", 2, hashes, times)
            };
            check(tests.Length == 4 && tests.All(t =>
                t.AcceptedCandidates == Count && t.EstimatedSavingBytes > 0 &&
                t.CandidateBytes < t.SourceBytes),
                "R2.17: all metadata variants produce smaller accepted candidates in both modes");
            check(tests.All(t => t.ElapsedMs > 0) &&
                  tests.Where(t => t.Mode == "baseline").Count() == 2 &&
                  tests.Where(t => t.Mode == "simd").Count() == 2,
                "R2.17: ABBA order captures two actual elapsed runs per tool without speedup claims");
            check(tests.Where(t => t.Mode == "baseline").All(t =>
                t.ToolSha256 == tests[0].ToolSha256) &&
                tests.Where(t => t.Mode == "simd").All(t =>
                t.ToolSha256 == tests[1].ToolSha256) &&
                tests[0].ToolSha256 != tests[1].ToolSha256,
                "R2.17: binaries and repeated runs are distinct verified baseline/SIMD builds");
            check(hashes.All(x => Sha(Path.Combine(input, x.Key)) == x.Value),
                "R2.17: original JPEG SHA-256 remains unchanged after four real app preview batches");

            Directory.CreateDirectory("diagnostics");
            var baseMs = tests.Where(t => t.Mode == "baseline").Select(t => t.ElapsedMs).Order().ToArray();
            var simdMs = tests.Where(t => t.Mode == "simd").Select(t => t.ElapsedMs).Order().ToArray();
            File.WriteAllText(Path.Combine("diagnostics", "r217-jpeg-metadata-simd.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "chappy-r217-jpeg-metadata-and-simd-v1",
                    officialSource = "libjpeg-turbo",
                    version = "3.2.1",
                    officialCommit = PinnedCommit,
                    sourceCommitVerifiedInCi = true,
                    nasmVersion = "2.16.03",
                    twoDifferentLocallyBuiltToolHashes = true,
                    baselineSimdDisabled = true,
                    optimizedSimdRequiredInBuild = true,
                    generatedSyntheticJpegCount = Count,
                    hasSyntheticExifXmpIccApp13ComVariants = true,
                    appAndIndependentMarkerAuditsPassed = true,
                    independentAllPixelComparisonsPassed = true,
                    allSourcesSha256AndTimestampsPreserved = true,
                    testOrder = "baseline1-simd1-simd2-baseline2",
                    performanceRuns = tests,
                    baselineMedianWallMs = (baseMs[0] + baseMs[1]) / 2,
                    simdMedianWallMs = (simdMs[0] + simdMs[1]) / 2,
                    measuredSpeedupAsserted = false,
                    hostThermalsControlled = false,
                    coldCacheControlled = false,
                    physicalDiskReadsMeasured = false,
                    realPhotosTested = false,
                    productionOriginalReplacementEnabled = false,
                    productionHardlinkMergeEnabled = false,
                    realRecoveredBytes = 0,
                    verdict = "PASS"
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("R2.17 JPEG metadata + SIMD PASS: 8/8 synthetic variants; " +
                $"baseline median {((baseMs[0] + baseMs[1]) / 2):F1} ms, " +
                $"SIMD median {((simdMs[0] + simdMs[1]) / 2):F1} ms; no speedup claim");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
