using System.IO.Compression;

namespace SphereMapPlus.Imaging;

/// <summary>极简 PNG 写入器:8bit RGBA,filter 0, zlib(ZLibStream)。无第三方依赖。</summary>
internal static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    public static void Save(string path, int width, int height, ReadOnlySpan<byte> rgba)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> ihdr = stackalloc byte[13];
        WriteBE32(ihdr, 0, (uint)width);
        WriteBE32(ihdr, 4, (uint)height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type RGBA
        ihdr[10] = 0; // compression
        ihdr[11] = 0; // filter
        ihdr[12] = 0; // interlace
        WriteChunk(fs, "IHDR"u8, ihdr);

        var stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0; // filter none
            rgba.Slice(y * stride, stride).CopyTo(raw.AsSpan(y * (stride + 1) + 1));
        }

        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, true))
            zlib.Write(raw);
        WriteChunk(fs, "IDAT"u8, ms.ToArray());

        WriteChunk(fs, "IEND"u8, []);
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBE32(len, 0, (uint)data.Length);
        s.Write(len);
        s.Write(type);

        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(type.Length));
        s.Write(data);

        Span<byte> crc = stackalloc byte[4];
        WriteBE32(crc, 0, Crc32(crcInput));
        s.Write(crc);
    }

    private static void WriteBE32(Span<byte> dst, int offset, uint value)
    {
        dst[offset] = (byte)(value >> 24);
        dst[offset + 1] = (byte)(value >> 16);
        dst[offset + 2] = (byte)(value >> 8);
        dst[offset + 3] = (byte)value;
    }
}
