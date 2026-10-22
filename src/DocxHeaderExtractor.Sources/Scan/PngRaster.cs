using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using DocxHeaderExtractor.Contracts.Scan;
using DocxHeaderExtractor.Sources.Common;

namespace DocxHeaderExtractor.Sources.Scan;

/// <summary>
/// A page image as grey levels (0 black to 255 white), one byte a pixel, decoded in .NET: a page is decoded once and every line is cut from it in
/// memory, where the image tool decoded the whole page again for each line (about 0.3 s a line). PNG without interlacing is read here (grey,
/// RGB, palette, with or without alpha, 1 to 16 bits); any other image (JPEG, interlaced PNG) is first turned into an 8-bit grey PNG by
/// ImageMagick, on standard input and output like every other call to it.
/// </summary>
public sealed class PngRaster
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>Row after row, <see cref="Width"/> bytes each.</summary>
    public byte[] Grey { get; }

    public PngRaster(int width, int height, byte[] grey)
    {
        if (grey.Length != width * height) throw new ArgumentException("RASTER_SIZE_MISMATCH");
        (Width, Height, Grey) = (width, height, grey);
    }

    public byte this[int x, int y] => Grey[y * Width + x];

    /// <summary>
    /// The page image as the grey levels <see cref="Load"/> gives, decoded by OpenCV: the colour channels are kept, a transparent pixel is laid
    /// over white, and the grey is the same Rec. 601 luma with the same rounding, pixel by pixel. So the levels are identical to the managed
    /// reader's, and the decoding is OpenCV's. Null when OpenCV cannot read the file or the depth is not 8 bits (<see cref="Load"/> is the way then).
    /// </summary>
    public static PngRaster? LoadGrey(string path)
    {
        // The bytes are read by .NET and decoded from memory: OpenCV's path reader cannot open a path with characters outside its code page.
        var bytes = File.ReadAllBytes(path);
        using var raw = OpenCvSharp.Cv2.ImDecode(bytes, OpenCvSharp.ImreadModes.Unchanged);
        if (raw.Empty() || raw.Depth() != 0) return null; // CV_8U is depth 0
        var width = raw.Width; var height = raw.Height; var channels = raw.Channels();
        if (channels is not (1 or 3 or 4)) return null;
        var grey = new byte[width * height];
        if (channels == 1)
        {
            for (var y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(raw.Ptr(y), grey, y * width, width);
            return new PngRaster(width, height, grey);
        }
        // Each row is copied out of OpenCV's memory into one buffer that is reused, and turned into grey at once: the whole image is never copied.
        // OpenCV keeps the channels in blue, green, red (, alpha) order.
        var row = new byte[width * channels];
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(raw.Ptr(y), row, 0, row.Length);
            var at0 = y * width;
            if (channels == 3)
                for (var x = 0; x < width; x++) grey[at0 + x] = Luma(row[3 * x + 2], row[3 * x + 1], row[3 * x]);
            else
                for (var x = 0; x < width; x++)
                {
                    var at = 4 * x; var alpha = row[at + 3];
                    grey[at0 + x] = Luma(OverWhite[alpha << 8 | row[at + 2]], OverWhite[alpha << 8 | row[at + 1]], OverWhite[alpha << 8 | row[at]]);
                }
        }
        return new PngRaster(width, height, grey);
    }

    /// <summary>The <see cref="Over"/> of every sample under every alpha, indexed by alpha then sample (the alpha is the high byte).</summary>
    static readonly byte[] OverWhite = Enumerable.Range(0, 256 * 256).Select(i => (byte)Over(i & 0xFF, i >> 8)).ToArray();

    // Each channel's share of the luma, computed once per level: the sum below is then the managed reader's sum, in the same order.
    static readonly double[] RedShare = Enumerable.Range(0, 256).Select(v => 0.298839 * v).ToArray();
    static readonly double[] GreenShare = Enumerable.Range(0, 256).Select(v => 0.586811 * v).ToArray();
    static readonly double[] BlueShare = Enumerable.Range(0, 256).Select(v => 0.114350 * v).ToArray();

    /// <summary>The Rec. 601 luma of the managed reader, computed in the same order and rounded the same way (to even).</summary>
    static byte Luma(int r, int g, int b) => (byte)Math.Clamp((int)Math.Round(RedShare[r] + GreenShare[g] + BlueShare[b]), 0, 255);

    /// <summary>A sample laid over white by its alpha, rounded as the managed reader rounds it.</summary>
    static int Over(int v, int alpha) => (v * alpha + 255 * (255 - alpha) + 127) / 255;

    /// <summary>The page image at <paramref name="path"/>; null when it is neither a readable PNG nor convertible by <paramref name="magick"/>.</summary>
    public static PngRaster? Load(string path, string? magick)
    {
        var bytes = File.ReadAllBytes(path);
        if (TryDecode(bytes) is { } raster) return raster;
        if (magick is null) return null;
        var grey = TesseractOcr.MagickBytes(magick, bytes, ["-colorspace", "Gray", "-depth", "8", "-interlace", "none", "-define", "png:color-type=0", "-define", "png:bit-depth=8"]);
        return grey is null ? null : TryDecode(grey);
    }

    /// <summary>A PNG read as grey levels; null for anything this reader does not take (not PNG, interlaced, a broken stream).</summary>
    public static PngRaster? TryDecode(byte[] png)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47) return null;
        int width = 0, height = 0, depth = 0, colour = 0;
        byte[]? palette = null;
        using var idat = new MemoryStream();
        for (var at = 8; at + 8 <= png.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (length < 0 || at + 12 + length > png.Length) return null;
            var data = png.AsSpan(at + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8]; colour = data[9];
                    if (data[12] != 0) return null;   // Adam7 interlacing: left to the image tool
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
                case "IEND": at = png.Length; continue;
            }
            at += 12 + length;
        }
        var channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (width <= 0 || height <= 0 || channels == 0 || depth is not (1 or 2 or 4 or 8 or 16) || colour == 3 && palette is null) return null;
        var bitsPerPixel = channels * depth;
        var stride = (width * bitsPerPixel + 7) / 8;
        var bpp = Math.Max(1, bitsPerPixel / 8);
        var raw = new byte[(stride + 1) * height];
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length) { var n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
            if (read < raw.Length) return null;
        }
        catch (InvalidDataException) { return null; }

        var grey = new byte[width * height];
        var previous = new byte[stride];
        var row = new byte[stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, row, 0, stride);
            for (var i = 0; i < stride; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                row[i] = filter switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + a),
                    2 => (byte)(row[i] + b),
                    3 => (byte)(row[i] + (a + b) / 2),
                    4 => (byte)(row[i] + Paeth(a, b, c)),
                    _ => throw new InvalidDataException("PNG_FILTER_UNKNOWN"),
                };
            }
            for (var x = 0; x < width; x++) grey[y * width + x] = Pixel(row, x, colour, depth, palette);
            (previous, row) = (row, previous);
        }
        return new PngRaster(width, height, grey);
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>One pixel as a grey level: samples scaled to 8 bits, colour by the Rec. 601 luma ImageMagick uses for -colorspace Gray, alpha over
    /// white.</summary>
    static byte Pixel(byte[] row, int x, int colour, int depth, byte[]? palette)
    {
        int Sample(int index)   // the index-th sample of the row, scaled to 0..255
        {
            if (depth == 16) return row[index * 2];
            if (depth == 8) return row[index];
            var bit = index * depth;
            var v = (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
            return v * 255 / ((1 << depth) - 1);
        }
        static byte Luma(int r, int g, int b) => (byte)Math.Clamp((int)Math.Round(0.298839 * r + 0.586811 * g + 0.114350 * b), 0, 255);
        static int Over(int v, int alpha) => (v * alpha + 255 * (255 - alpha) + 127) / 255;
        switch (colour)
        {
            case 0: return (byte)Sample(x);
            case 2: return Luma(Sample(3 * x), Sample(3 * x + 1), Sample(3 * x + 2));
            case 3:
            {
                var bit = x * depth;
                var i = depth == 8 ? row[x] : (row[bit / 8] >> (8 - depth - bit % 8)) & ((1 << depth) - 1);
                return 3 * i + 2 < palette!.Length ? Luma(palette[3 * i], palette[3 * i + 1], palette[3 * i + 2]) : (byte)255;
            }
            case 4: return (byte)Over(Sample(2 * x), Sample(2 * x + 1));
            default:
                var alpha = Sample(4 * x + 3);
                return Luma(Over(Sample(4 * x), alpha), Over(Sample(4 * x + 1), alpha), Over(Sample(4 * x + 2), alpha));
        }
    }

    /// <summary>The 1-bit PNG of one region with <paramref name="pad"/> pixels around it (cut at the page's edges, as the image tool cuts),
    /// the <paramref name="mask"/> boxes painted white except where they overlap <paramref name="box"/>, every pixel repeated
    /// <paramref name="scale"/> times, black where the grey level is at most <paramref name="threshold"/> of white.</summary>
    public byte[] CropBilevel(PixelBox box, int pad, int scale, IReadOnlyList<PixelBox>? mask, double threshold)
    {
        // As TesseractOcr.CropScaled asks the image tool: the corner moved inside the page, the size kept, the rest cut at the page's edge.
        int x0 = Math.Max(0, box.Left - pad), y0 = Math.Max(0, box.Top - pad);
        int x1 = Math.Min(Width, x0 + box.Width + 2 * pad), y1 = Math.Min(Height, y0 + box.Height + 2 * pad);
        int w = Math.Max(0, x1 - x0), h = Math.Max(0, y1 - y0);
        var cut = (int)Math.Floor(threshold * 255);
        // The neighbours' boxes are marked once, as white, outside the line's own box; a pixel is then tested only against this map.
        var masked = new bool[w * h];
        if (mask is not null)
            foreach (var m in mask)
                for (var y = Math.Max(m.Top, y0); y < Math.Min(m.Top + m.Height, y1); y++)
                    for (var x = Math.Max(m.Left, x0); x < Math.Min(m.Left + m.Width, x1); x++)
                        if (!(x >= box.Left && x < box.Left + box.Width && y >= box.Top && y < box.Top + box.Height))
                            masked[(y - y0) * w + (x - x0)] = true;
        var outWidth = w * scale;
        var rowBytes = (outWidth + 7) / 8;
        var raw = new byte[(rowBytes + 1) * h * scale];
        var line = new byte[rowBytes];
        for (var y = 0; y < h; y++)
        {
            Array.Clear(line);
            for (var x = 0; x < w; x++)
                if (masked[y * w + x] || this[x0 + x, y0 + y] > cut)   // 1 is white in a 1-bit grey PNG
                    for (var s = 0; s < scale; s++) { var ox = x * scale + s; line[ox / 8] |= (byte)(0x80 >> (ox % 8)); }
            for (var s = 0; s < scale; s++) Buffer.BlockCopy(line, 0, raw, (y * scale + s) * (rowBytes + 1) + 1, rowBytes);
        }
        return EncodeGrey(outWidth, h * scale, 1, raw);
    }

    /// <summary>A grey PNG from rows that already carry their filter byte (0).</summary>
    static byte[] EncodeGrey(int width, int height, int depth, byte[] filteredRows)
    {
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = (byte)depth;   // colour type 0 (grey), deflate, filter 0, no interlace
        Chunk(png, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(filteredRows);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(four, (uint)data.Length);
        s.Write(four);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        s.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc32(typed));
        s.Write(four);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
