using System;
using System.IO;
using System.IO.Compression;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Writes a screenshot as PNG - without an extra package.
///
/// System.Drawing would be a new dependency just for this one file, and
/// Avalonia's bitmap classes belong on the UI thread. A PNG consists of only
/// three building blocks, though: header (IHDR), zlib-compressed rows (IDAT)
/// and end (IEND), each with a CRC-32. .NET ships zlib compression itself
/// (ZLibStream). What remains is this code - and it can be tested without
/// Windows and without a screen.
/// </summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Writes 32-bit pixels in Windows format (B, G, R, A; rows top-down) as a
    /// 24-bit RGB PNG. A screenshot's alpha channel carries nothing and is
    /// dropped.
    /// </summary>
    public static void WriteBgra(Stream output, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Leeres Bild.");
        if (bgra.Length < (long)width * height * 4) throw new ArgumentException("Zu wenige Bildpunkte.", nameof(bgra));

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, (uint)width);
        WriteBigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;    // 8 bits per color channel
        ihdr[9] = 2;    // color type 2 = RGB
        ihdr[10] = 0;   // Deflate
        ihdr[11] = 0;   // standard filter method
        ihdr[12] = 0;   // no interlacing
        WriteChunk(output, "IHDR", ihdr);

        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Per row: one filter byte, then the color values. Filter 1 ("Sub") stores
                // only the difference to the left neighbor - uniform areas such as windows
                // and the taskbar become zeros and compress much better than the raw
                // values.
                var row = new byte[1 + width * 3];
                var prev = new byte[3];
                for (int y = 0; y < height; y++)
                {
                    row[0] = 1;
                    var src = bgra.Slice(y * width * 4, width * 4);
                    prev[0] = prev[1] = prev[2] = 0;
                    for (int x = 0, o = 1; x < width; x++, o += 3)
                    {
                        byte r = src[x * 4 + 2], g = src[x * 4 + 1], b = src[x * 4];
                        row[o]     = (byte)(r - prev[0]);
                        row[o + 1] = (byte)(g - prev[1]);
                        row[o + 2] = (byte)(b - prev[2]);
                        prev[0] = r; prev[1] = g; prev[2] = b;
                    }
                    zlib.Write(row, 0, row.Length);
                }
            }
            WriteChunk(output, "IDAT", packed.GetBuffer().AsSpan(0, (int)packed.Length));
        }

        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Convenience overload for tests and small images.</summary>
    public static byte[] EncodeBgra(int width, int height, ReadOnlySpan<byte> bgra)
    {
        using var ms = new MemoryStream();
        WriteBgra(ms, width, height, bgra);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        WriteBigEndian(head, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        output.Write(head);
        output.Write(data);

        // The checksum covers type AND data, not the length.
        var crc = Crc32Update(0xFFFFFFFFu, head[4..]);
        crc = Crc32Update(crc, data) ^ 0xFFFFFFFFu;
        Span<byte> tail = stackalloc byte[4];
        WriteBigEndian(tail, crc);
        output.Write(tail);
    }

    private static void WriteBigEndian(Span<byte> target, uint value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    // ------------------------------------------------------------ CRC-32

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>CRC-32 as in PNG and ZIP - public for the tests.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) => Crc32Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
