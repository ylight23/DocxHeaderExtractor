using System.IO.Compression;
using System.Text;

namespace DocxHeaderExtractor.Tests;

/// <summary>Valid PNG files for tests: an 8-bit grey image of one colour, with real CRCs, so that image tools accept it.</summary>
internal static class TestPng
{
    public static byte[] Grey(int width, int height, byte value = 255)
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < height; y++)
        {
            raw.WriteByte(0);   // filter: none
            for (var x = 0; x < width; x++) raw.WriteByte(value);
        }
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(z);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BigEndian(header, 0, width); BigEndian(header, 4, height);
        header[8] = 8; header[9] = 0;   // 8 bits, grey
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void BigEndian(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BigEndian(len, 0, data.Length);
        s.Write(len);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        var crc = new byte[4]; BigEndian(crc, 0, (int)Crc32(typed));
        s.Write(crc);
    }

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}
