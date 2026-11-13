using System.Buffers.Binary;
using System.Text;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Read-only PNG integrity and feature inspection. This does not optimize or
/// rewrite images. R2 may use it as a conservative input gate.
/// </summary>
public sealed record PngInspectionResult(
    int Width, int Height, long IdatBytes, int ChunkCount,
    bool IsAnimated, bool HasUnknownCriticalChunk, bool HasAncillaryChunk)
{
    public bool CandidateForFutureLosslessOptimization =>
        !IsAnimated && !HasUnknownCriticalChunk && IdatBytes > 0;
}

public static class PngInspector
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly HashSet<string> KnownCritical = new(StringComparer.Ordinal)
    { "IHDR", "PLTE", "IDAT", "IEND" };

    private static readonly uint[] CrcTable = BuildCrcTable();

    public static PngInspectionResult Inspect(string path, CancellationToken ct = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature))
            throw new InvalidDataException("PNG署名が一致しません。");

        int chunks = 0, width = 0, height = 0;
        long idatBytes = 0;
        bool sawHeader = false, sawImage = false, sawEnd = false, afterIdat = false;
        bool animated = false, unknownCritical = false, hasAncillary = false;
        byte[] buffer = new byte[64 * 1024];
        Span<byte> header = stackalloc byte[8];
        Span<byte> ihdr = stackalloc byte[13];
        Span<byte> trailer = stackalloc byte[4];
        while (stream.Position < stream.Length)
        {
            ct.ThrowIfCancellationRequested();
            stream.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            // PNG chunks have a maximum of (2^31 - 1) bytes.
            if (length > int.MaxValue || (long)length + 4 > stream.Length - stream.Position)
                throw new InvalidDataException("PNGチャンク長が不正です。");
            if (!IsAsciiLetter(header[4]) || !IsAsciiLetter(header[5]) ||
                !IsAsciiLetter(header[6]) || !IsAsciiLetter(header[7]))
                throw new InvalidDataException("PNGチャンク名が不正です。");
            string kind = Encoding.ASCII.GetString(header[4..8]);
            bool critical = header[4] is >= (byte)'A' and <= (byte)'Z';
            if (critical && !KnownCritical.Contains(kind)) unknownCritical = true;
            if (!critical) hasAncillary = true;
            if (kind is "acTL" or "fcTL" or "fdAT") animated = true;

            chunks++;
            if (chunks == 1 && (kind != "IHDR" || length != 13))
                throw new InvalidDataException("先頭チャンクがIHDRではありません。");
            if (kind == "IHDR" && (sawHeader || length != 13))
                throw new InvalidDataException("IHDRチャンクの数または長さが不正です。");
            if (kind == "IDAT" && (!sawHeader || afterIdat))
                throw new InvalidDataException("IDATチャンクの順序が不正です。");
            if (kind == "IEND" && (!sawImage || length != 0))
                throw new InvalidDataException("IENDチャンクが不正です。");
            if (sawImage && kind != "IDAT") afterIdat = true;
            if (kind == "IDAT") { sawImage = true; idatBytes += length; }

            uint crc = 0xFFFFFFFF;
            foreach (byte letter in header[4..8])
                crc = Step(crc, letter);
            long remaining = length;
            int ihdrCopied = 0;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(remaining, buffer.Length);
                stream.ReadExactly(buffer.AsSpan(0, n));
                if (kind == "IHDR")
                {
                    buffer.AsSpan(0, n).CopyTo(ihdr[ihdrCopied..]);
                    ihdrCopied += n;
                }
                for (int i = 0; i < n; i++) crc = Step(crc, buffer[i]);
                remaining -= n;
            }
            stream.ReadExactly(trailer);
            if ((crc ^ 0xFFFFFFFF) != BinaryPrimitives.ReadUInt32BigEndian(trailer))
                throw new InvalidDataException("PNGチャンクCRCが一致しません: " + kind);

            if (kind == "IHDR")
            {
                sawHeader = true;
                width = BinaryPrimitives.ReadInt32BigEndian(ihdr[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(ihdr[4..8]);
                if (width <= 0 || height <= 0 || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1)
                    throw new InvalidDataException("PNG画像ヘッダーが不正です。");
            }
            if (kind == "IEND")
            {
                sawEnd = true;
                break;
            }
        }
        if (!sawHeader || !sawImage || !sawEnd || stream.Position != stream.Length)
            throw new InvalidDataException("PNGに必要なチャンクがない、または余分なデータがあります。");
        return new PngInspectionResult(width, height, idatBytes, chunks,
            animated, unknownCritical, hasAncillary);
    }

    private static bool IsAsciiLetter(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z';

    private static uint Step(uint crc, byte b) =>
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
}
