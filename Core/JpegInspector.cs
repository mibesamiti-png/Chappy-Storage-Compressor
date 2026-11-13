namespace ChappyStorageCompressor.Core;

/// <summary>
/// R1.7 JPEG header inspection ONLY. Does not recompress or claim pixel equality.
/// Scan entropy, multiple scans and the entire image body are not decoded.
/// </summary>
public static class JpegInspector
{
    public sealed record Header(int Width, int Height, int Precision, bool Progressive,
        int ApplicationMarkers, bool HasExif, bool HasIcc, bool HasXmp,
        bool HasEndMarker, string Warning);

    public static Header Inspect(string file, CancellationToken ct = default)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        if (fs.Length < 6 || fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
            throw new InvalidDataException("JPEGのSOI署名がありません。");
        long inspected = 0;
        int width = 0, height = 0, precision = 0, app = 0;
        bool progressive = false, exif = false, icc = false, xmp = false, reachedScan = false;
        while (fs.Position < fs.Length && inspected < 16L * 1024 * 1024)
        {
            ct.ThrowIfCancellationRequested();
            int prefix = fs.ReadByte();
            if (prefix != 0xFF) throw new InvalidDataException("JPEGマーカーの境界が不正です。");
            int marker;
            do { marker = fs.ReadByte(); } while (marker == 0xFF);
            if (marker < 0) throw new InvalidDataException("途中でJPEGデータが終了しました。");
            if (marker == 0xD9) break;
            if (marker == 0xDA) { reachedScan = true; break; } // entropy follows
            if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            int hi = fs.ReadByte(), lo = fs.ReadByte();
            if (hi < 0 || lo < 0) throw new InvalidDataException("JPEGセグメント長がありません。");
            int length = hi * 256 + lo;
            if (length < 2 || fs.Position + length - 2 > fs.Length)
                throw new InvalidDataException("JPEGセグメントがファイル外を指しています。");
            long payloadStart = fs.Position;
            int payloadLength = length - 2;
            inspected = checked(inspected + length + 2);
            if (marker is >= 0xE0 and <= 0xEF)
            {
                app++;
                int count = Math.Min(payloadLength, 64);
                byte[] lead = new byte[count];
                fs.ReadExactly(lead);
                // Strict preservation of all APPn/COM is a future optimizer gate.
                if (marker == 0xE1 && lead.AsSpan().StartsWith("Exif\0\0"u8)) exif = true;
                if (marker == 0xE1 && lead.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8)) xmp = true;
                if (marker == 0xE2 && lead.AsSpan().StartsWith("ICC_PROFILE\0"u8)) icc = true;
            }
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or
                0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                if (payloadLength < 6) throw new InvalidDataException("JPEGのSOF長が不正です。");
                fs.Position = payloadStart;
                precision = fs.ReadByte();
                int h1 = fs.ReadByte(), h2 = fs.ReadByte(), w1 = fs.ReadByte(), w2 = fs.ReadByte();
                height = h1 * 256 + h2;
                width = w1 * 256 + w2;
                progressive = marker is 0xC2 or 0xC6 or 0xCA or 0xCE;
                if (width == 0 || height == 0) throw new InvalidDataException("画像サイズが不正です。");
            }
            fs.Position = payloadStart + payloadLength;
        }
        if (!reachedScan || width == 0 || height == 0)
            throw new InvalidDataException("JPEGヘッダのSOF/SOSが見つかりません。");
        fs.Position = fs.Length - 2;
        bool eoi = fs.ReadByte() == 0xFF && fs.ReadByte() == 0xD9;
        return new Header(width, height, precision, progressive, app, exif, icc, xmp, eoi,
            "ヘッダ検査のみ。全スキャン復号・画素比較・メタデータ完全保持の証明ではありません。");
    }
}
