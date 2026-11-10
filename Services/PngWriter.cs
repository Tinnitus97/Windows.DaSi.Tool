using System;
using System.IO;
using System.IO.Compression;

namespace WindowsDaSiTool.Services;

/// <summary>
/// Schreibt ein Bildschirmfoto als PNG - ohne zusaetzliches Paket.
///
/// System.Drawing waere eine neue Abhaengigkeit nur fuer diese eine Datei,
/// und die Bitmap-Klassen von Avalonia gehoeren an den UI-Thread. Ein PNG
/// besteht aber nur aus drei Bausteinen: Kopf (IHDR), zlib-gepackte Zeilen
/// (IDAT) und Ende (IEND), jeder mit einer CRC-32. Das zlib-Packen bringt
/// .NET selbst mit (ZLibStream). Bleibt dieser Code - und der laesst sich
/// ohne Windows und ohne Bildschirm pruefen.
/// </summary>
public static class PngWriter
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Schreibt 32-Bit-Pixel im Windows-Format (B, G, R, A; Zeilen von oben
    /// nach unten) als 24-Bit-RGB-PNG. Der Alphakanal eines Bildschirmfotos
    /// traegt nichts und entfaellt.
    /// </summary>
    public static void WriteBgra(Stream output, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Leeres Bild.");
        if (bgra.Length < (long)width * height * 4) throw new ArgumentException("Zu wenige Bildpunkte.", nameof(bgra));

        output.Write(Signature);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBigEndian(ihdr, (uint)width);
        WriteBigEndian(ihdr[4..], (uint)height);
        ihdr[8] = 8;    // 8 Bit je Farbkanal
        ihdr[9] = 2;    // Farbtyp 2 = RGB
        ihdr[10] = 0;   // Deflate
        ihdr[11] = 0;   // Standardfilter
        ihdr[12] = 0;   // kein Interlacing
        WriteChunk(output, "IHDR", ihdr);

        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Je Zeile: ein Filterbyte, dann die Farbwerte. Filter 1 ("Sub")
                // speichert nur den Unterschied zum linken Nachbarn - gleichmaessige
                // Flaechen wie Fenster und Taskleiste werden so zu Nullen und
                // packen deutlich besser als die Rohwerte.
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

    /// <summary>Bequeme Form fuer Tests und kleine Bilder.</summary>
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

        // Die Pruefsumme laeuft ueber Typ UND Daten, nicht ueber die Laenge.
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

    /// <summary>CRC-32 wie in PNG und ZIP - fuer die Pruefungen offen gelegt.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data) => Crc32Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
