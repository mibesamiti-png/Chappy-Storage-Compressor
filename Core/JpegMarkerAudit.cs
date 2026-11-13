using System.Security.Cryptography;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Conservative JPEG marker parser used only to gate preview output.  It captures
/// all APPn/COM payloads, quantization and SOF declarations. It follows scan
/// boundaries, byte stuffing and restart markers rather than looking for FFxx
/// sequences in compressed entropy. Unsupported JPEGs are rejected, not modified.
/// </summary>
public static class JpegMarkerAudit
{
    public sealed record Snapshot(int Width, int Height, int Precision, bool Baseline,
        int ScanCount, string ProtectedMarkerSha256, int ProtectedMarkerCount);

    public static Snapshot Inspect(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        if (fs.Length < 10 || fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
            throw new InvalidDataException("JPEG SOIがありません。");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int count = 0, width = 0, height = 0, precision = 0, scans = 0;
        bool baseline = false, sawEoi = false, inScan = false;
        const int MaxProtectedBytes = 32 * 1024 * 1024;
        int protectedBytes = 0;
        while (fs.Position < fs.Length)
        {
            ct.ThrowIfCancellationRequested();
            int marker;
            if (!inScan)
            {
                if (fs.ReadByte() != 0xFF)
                    throw new InvalidDataException("JPEGマーカーの開始が不正です。");
                do { marker = fs.ReadByte(); } while (marker == 0xFF);
            }
            else
            {
                // Entropy data: 0xFF00 is stuffed, 0xFFD0..D7 are restart markers.
                while (true)
                {
                    int octet = fs.ReadByte();
                    if (octet < 0) throw new InvalidDataException("JPEGスキャンが終了していません。");
                    if (octet != 0xFF) continue;
                    do { marker = fs.ReadByte(); } while (marker == 0xFF);
                    if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7) continue;
                    break;
                }
                inScan = false;
            }
            if (marker < 0) throw new InvalidDataException("JPEGが途中で終了しました。");
            if (marker == 0xD9) { sawEoi = true; break; }
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                throw new InvalidDataException("サポート対象外のJPEGマーカー配置です。");
            int hi = fs.ReadByte(), lo = fs.ReadByte();
            if (hi < 0 || lo < 0) throw new InvalidDataException("JPEGセグメント長が欠落しています。");
            int length = hi * 256 + lo;
            if (length < 2 || length - 2 > fs.Length - fs.Position)
                throw new InvalidDataException("JPEGセグメント長が不正です。");
            int payloadLength = length - 2;
            byte[] payload = new byte[payloadLength];
            fs.ReadExactly(payload);
            ct.ThrowIfCancellationRequested();
            if (marker == 0xDA)
            {
                scans++;
                if (scans > 1) throw new NotSupportedException("複数スキャンJPEGはR1.8の検証対象外です。");
                inScan = true;
            }
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                if (payloadLength < 6 || width != 0) throw new InvalidDataException("JPEGのSOFが不正です。");
                precision = payload[0];
                height = payload[1] * 256 + payload[2];
                width = payload[3] * 256 + payload[4];
                baseline = marker == 0xC0;
            }
            // Keep both contents and ordering of original metadata and image-defining tables.
            // DHT is intentionally excluded: optimizing Huffman tables is the only desired change.
            if (marker is >= 0xE0 and <= 0xEF || marker is 0xFE or 0xDB or 0xDD or 0xDC or 0xC0)
            {
                protectedBytes = checked(protectedBytes + payloadLength + 4);
                if (protectedBytes > MaxProtectedBytes)
                    throw new NotSupportedException("保護対象JPEGメタデータが32MiBを超えます。");
                hash.AppendData(new byte[] { (byte)0xFF, (byte)marker, (byte)hi, (byte)lo });
                hash.AppendData(payload);
                count++;
            }
        }
        if (!sawEoi || scans != 1 || !baseline || precision != 8 || width <= 0 || height <= 0)
            throw new NotSupportedException("R1.8は8bitベースラインJPEG・1スキャンのみ候補を作成します。");
        if (fs.Position != fs.Length)
            throw new NotSupportedException("EOIの後にデータがあるJPEGは安全のため対象外です。");
        return new Snapshot(width, height, precision, baseline, scans,
            Convert.ToHexString(hash.GetHashAndReset()), count);
    }
}
