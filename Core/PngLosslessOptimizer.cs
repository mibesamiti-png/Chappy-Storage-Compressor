using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// R1.5 preview-only optimizer: original PNG is NEVER rewritten or deleted.
/// Preserves all non-IDAT chunks byte-for-byte, recompresses the exact filtered
/// scanline stream using DEFLATE, validates the output, and keeps only smaller
/// fully verified previews. No palette, pixel, metadata, or color conversion.
/// </summary>
public static class PngLosslessOptimizer
{
    private const long MaximumInflatedBytes = 512L * 1024 * 1024;
    private const int IoBufferSize = 64 * 1024;
    private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly byte[] IdatName = Encoding.ASCII.GetBytes("IDAT");
    // Known PNG ancillary chunks that can safely be preserved when the
    // *uncompressed* scanlines and pixel interpretation remain unchanged.
    // Unknown unsafe-to-copy ancillary chunks are conservatively rejected.
    private static readonly HashSet<string> KnownAncillary = new(StringComparer.Ordinal)
    {
        "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "bKGD", "hIST", "tRNS",
        "pHYs", "sPLT", "tIME", "tEXt", "zTXt", "iTXt", "eXIf"
    };

    public sealed record Result(string Source, string? PreviewPath, long OriginalBytes,
        long PreviewBytes, string Explanation)
    {
        public long SavedBytes => PreviewPath is null ? 0 : OriginalBytes - PreviewBytes;
    }

    private readonly record struct Chunk(string Name, long Offset, long DataOffset, long Length)
    {
        public long End => checked(DataOffset + Length + 4);
    }

    public static Result CreatePreview(string sourcePath, string destinationDirectory,
        CancellationToken ct = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string outputDir = Path.GetFullPath(destinationDirectory);
        if (!string.Equals(Path.GetExtension(source), ".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("R1.5の無劣化候補生成はPNGのみ対応しています。");
        if (!Directory.Exists(outputDir)) throw new DirectoryNotFoundException(outputDir);
        using var sourceGuard = new FileStream(source, FileMode.Open, FileAccess.Read,
            FileShare.Read, IoBufferSize, FileOptions.SequentialScan);
        long originalLength = sourceGuard.Length;
        if (originalLength < 45) throw new InvalidDataException("PNGが短すぎます。");

        // Read-only structural and CRC pass. Opening source with FileShare.Read
        // prevents normal concurrent writers/deleters while this method executes.
        var inspected = PngInspector.Inspect(source, ct);
        if (!inspected.CandidateForFutureLosslessOptimization)
            return new Result(source, null, originalLength, 0,
                "APNG・未知の必須チャンク等があるため、安全のため最適化しません。");
        var chunks = IndexChunks(sourceGuard, ct);
        if (chunks.Any(c => c.Name.Length != 4 ||
            (char.IsLower(c.Name[0]) && char.IsUpper(c.Name[3]) &&
             !KnownAncillary.Contains(c.Name))))
            return new Result(source, null, originalLength, 0,
                "未知のunsafe-to-copy付加チャンクがあり、無劣化保証のため最適化しません。");
        long exactScanlineLength = ExpectedInflatedLength(sourceGuard, chunks[0]);
        if (exactScanlineLength > MaximumInflatedBytes)
            return new Result(source, null, originalLength, 0,
                "展開後512MiBを超えるPNGはR1.5では処理しません。");
        var idats = chunks.Where(x => x.Name == "IDAT").ToList();
        if (idats.Count == 0) throw new InvalidDataException("IDATがありません。");

        // A second full-size file is required for safe verification. No in-place
        // rewrites and no implicit deletion of the only original copy.
        var drive = new DriveInfo(Path.GetPathRoot(outputDir)!);
        if (!drive.IsReady || drive.AvailableFreeSpace < checked(originalLength + 8L * 1024 * 1024))
            return new Result(source, null, originalLength, 0,
                "候補保存先の空き容量が不足しています（元サイズ + 8MiBを要求）。別ドライブを選択してください。");

        string baseName = Path.GetFileNameWithoutExtension(source);
        if (baseName.Length > 55) baseName = baseName[..55];
        string candidate = Path.Combine(outputDir,
            baseName + ".chappy-preview-" + Guid.NewGuid().ToString("N") + ".png");
        bool keep = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, IoBufferSize, FileOptions.SequentialScan))
            {
                CopyRange(sourceGuard, output, 0, idats[0].Offset, ct);
                using (var idatWriter = new IdatChunkWriter(output))
                {
                    using (var compressor = new ZLibStream(idatWriter,
                        CompressionLevel.SmallestSize, leaveOpen: true))
                    using (var payload = new IdatPayloadStream(source, idats))
                    using (var decompressor = new ZLibStream(payload,
                        CompressionMode.Decompress, leaveOpen: false))
                    {
                        CopyInflated(decompressor, compressor, exactScanlineLength, ct);
                    }
                    idatWriter.Complete();
                }
                CopyRange(sourceGuard, output, idats[^1].End,
                    originalLength - idats[^1].End, ct);
                output.Flush(flushToDisk: true);
            }
            long size = new FileInfo(candidate).Length;
            if (size >= originalLength)
                return new Result(source, null, originalLength, size,
                    "安全な無劣化再圧縮はできましたが、容量が減らないため候補を破棄しました。");

            ct.ThrowIfCancellationRequested();
            PngInspector.Inspect(candidate, ct);
            if (!SameNonIdatChunks(source, candidate, ct) ||
                !SameInflatedData(source, candidate, ct))
                throw new InvalidDataException("無劣化照合に失敗しました。候補を破棄します。");
            // Original cannot have changed via normal Windows file operations,
            // but the source lock is held until after final verification.
            keep = true;
            return new Result(source, candidate, originalLength, size,
                "展開後の画像データとIDAT以外の全チャンクが完全一致。元ファイルは未変更です。");
        }
        finally
        {
            if (!keep && File.Exists(candidate))
            {
                try { File.Delete(candidate); }
                catch { /* Do not obscure original exception; show leftover in docs/UI. */ }
            }
        }
    }

    private static List<Chunk> IndexChunks(FileStream stream, CancellationToken ct)
    {
        stream.Position = 0;
        Span<byte> sig = stackalloc byte[8];
        stream.ReadExactly(sig);
        if (!sig.SequenceEqual(PngSignature)) throw new InvalidDataException("PNG署名が不正です。");
        var chunks = new List<Chunk>();
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            long offset = stream.Position;
            stream.ReadExactly(header);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (len > int.MaxValue || (long)len + 4 > stream.Length - stream.Position)
                throw new InvalidDataException("PNGチャンク長が不正です。");
            string name = Encoding.ASCII.GetString(header[4..8]);
            chunks.Add(new Chunk(name, offset, stream.Position, len));
            stream.Position += len + 4;
            if (name == "IEND") break;
        }
        if (stream.Position != stream.Length || chunks.Count < 3 ||
            chunks[0].Name != "IHDR" || chunks[^1].Name != "IEND")
            throw new InvalidDataException("PNGのチャンク列が不正です。");
        return chunks;
    }

    private static long ExpectedInflatedLength(FileStream input, Chunk ihdr)
    {
        if (ihdr.Length != 13) throw new InvalidDataException("IHDRの長さが不正です。");
        input.Position = ihdr.DataOffset;
        Span<byte> hdr = stackalloc byte[13];
        input.ReadExactly(hdr);
        uint w = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..8]);
        int depth = hdr[8], color = hdr[9], compression = hdr[10], filter = hdr[11], interlace = hdr[12];
        int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        bool validDepth = color switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (w == 0 || h == 0 || channels == 0 || !validDepth || compression != 0 ||
            filter != 0 || interlace is not (0 or 1))
            throw new InvalidDataException("対応外/不正なPNGヘッダーです。");
        long PixelsPerRow(uint width) => checked(((long)width * channels * depth + 7) / 8 + 1);
        if (interlace == 0) return checked(PixelsPerRow(w) * h);
        // Adam7 pass sizes; a pass without any pixels has no scanline bytes.
        int[] xStart = { 0, 4, 0, 2, 0, 1, 0 }, yStart = { 0, 0, 4, 0, 2, 0, 1 };
        int[] dx = { 8, 8, 4, 4, 2, 2, 1 }, dy = { 8, 8, 8, 4, 4, 2, 2 };
        long total = 0;
        for (int i = 0; i < 7; i++)
        {
            long pw = w <= xStart[i] ? 0 : ((long)w - xStart[i] + dx[i] - 1) / dx[i];
            long ph = h <= yStart[i] ? 0 : ((long)h - yStart[i] + dy[i] - 1) / dy[i];
            if (pw > 0 && ph > 0) total = checked(total + PixelsPerRow((uint)pw) * ph);
        }
        return total;
    }

    private static void CopyInflated(Stream source, Stream destination, long exactBytes,
        CancellationToken ct)
    {
        long count = 0;
        byte[] buffer = new byte[IoBufferSize];
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            count = checked(count + n);
            if (count > exactBytes) throw new InvalidDataException("PNG展開後データが画像寸法を超えています。");
            destination.Write(buffer, 0, n);
        }
        if (count != exactBytes)
            throw new InvalidDataException($"PNG展開後データ長が不一致: {count}/{exactBytes}");
    }

    private static void CopyRange(FileStream input, Stream output, long start, long length,
        CancellationToken ct)
    {
        input.Position = start;
        byte[] buffer = new byte[IoBufferSize];
        while (length > 0)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(length, buffer.Length);
            input.ReadExactly(buffer.AsSpan(0, n));
            output.Write(buffer, 0, n);
            length -= n;
        }
    }

    private static bool SameNonIdatChunks(string a, string b, CancellationToken ct)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ca = IndexChunks(fa, ct).Where(x => x.Name != "IDAT").ToList();
        var cb = IndexChunks(fb, ct).Where(x => x.Name != "IDAT").ToList();
        if (ca.Count != cb.Count) return false;
        byte[] aa = new byte[IoBufferSize], bb = new byte[IoBufferSize];
        for (int i = 0; i < ca.Count; i++)
        {
            if (ca[i].Name != cb[i].Name || ca[i].Length != cb[i].Length) return false;
            fa.Position = ca[i].Offset;
            fb.Position = cb[i].Offset;
            long size = ca[i].Length + 12;
            while (size > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(size, aa.Length);
                fa.ReadExactly(aa.AsSpan(0, n));
                fb.ReadExactly(bb.AsSpan(0, n));
                if (!aa.AsSpan(0, n).SequenceEqual(bb.AsSpan(0, n))) return false;
                size -= n;
            }
        }
        return true;
    }

    private static bool SameInflatedData(string a, string b, CancellationToken ct)
    {
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        var ia = IndexChunks(fa, ct).Where(x => x.Name == "IDAT").ToList();
        var ib = IndexChunks(fb, ct).Where(x => x.Name == "IDAT").ToList();
        using var ca = new ZLibStream(new IdatPayloadStream(a, ia), CompressionMode.Decompress);
        using var cb = new ZLibStream(new IdatPayloadStream(b, ib), CompressionMode.Decompress);
        byte[] ba = new byte[IoBufferSize], bb = new byte[IoBufferSize];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int na = ReadUpTo(ca, ba);
            int nb = ReadUpTo(cb, bb);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    private static int ReadUpTo(Stream stream, byte[] buf)
    {
        int count = 0;
        while (count < buf.Length)
        {
            int n = stream.Read(buf, count, buf.Length - count);
            if (n == 0) break;
            count += n;
        }
        return count;
    }

    private sealed class IdatPayloadStream : Stream
    {
        private readonly FileStream _file;
        private readonly IReadOnlyList<Chunk> _segments;
        private int _index;
        private long _within;
        public IdatPayloadStream(string source, IReadOnlyList<Chunk> segments)
        {
            _file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                IoBufferSize, FileOptions.RandomAccess);
            _segments = segments;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> destination)
        {
            while (_index < _segments.Count)
            {
                Chunk c = _segments[_index];
                if (_within == c.Length) { _index++; _within = 0; continue; }
                int n = (int)Math.Min(destination.Length, c.Length - _within);
                if (n == 0) return 0;
                _file.Position = c.DataOffset + _within;
                int read = _file.Read(destination[..n]);
                if (read == 0) throw new EndOfStreamException("PNG IDATの読み込みが途中で終了しました。");
                _within += read;
                return read;
            }
            return 0;
        }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class IdatChunkWriter : Stream
    {
        private readonly Stream _out;
        private readonly byte[] _buffer = new byte[1024 * 1024];
        private int _used;
        private bool _completed;
        public IdatChunkWriter(Stream destination) { _out = destination; }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            if (_completed) throw new InvalidOperationException("IDATは既に完了しました。");
            while (!data.IsEmpty)
            {
                int count = Math.Min(_buffer.Length - _used, data.Length);
                data[..count].CopyTo(_buffer.AsSpan(_used));
                _used += count;
                data = data[count..];
                if (_used == _buffer.Length) WriteChunk();
            }
        }
        public void Complete()
        {
            if (_completed) return;
            if (_used > 0) WriteChunk();
            _completed = true;
        }
        private void WriteChunk()
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)_used);
            _out.Write(len);
            _out.Write(IdatName);
            _out.Write(_buffer.AsSpan(0, _used));
            uint crc = 0xFFFFFFFF;
            foreach (byte b in IdatName) crc = CrcStep(crc, b);
            foreach (byte b in _buffer.AsSpan(0, _used)) crc = CrcStep(crc, b);
            BinaryPrimitives.WriteUInt32BigEndian(len, crc ^ 0xFFFFFFFF);
            _out.Write(len);
            _used = 0;
        }
        private static readonly uint[] CrcTable = BuildCrcTable();
        private static uint CrcStep(uint crc, byte b) =>
            (crc >> 8) ^ CrcTable[(int)((crc ^ b) & 0xFF)];
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int i = 0; i < 8; i++)
                    c = (c & 1) != 0 ? 0xEDB88320U ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_completed) Complete();
            base.Dispose(disposing);
        }
        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _out.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
