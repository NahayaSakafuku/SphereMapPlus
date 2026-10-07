using System.IO.Compression;

namespace SphereMapPlus.Imaging;

/// <summary>极简 PNG 读取器:8-bit、非隔行,color type 0/2/4/6。输出 RGBA8。无第三方依赖。</summary>
internal static class PngReader
{
    public static bool TryDecode(byte[] png, out byte[] rgba, out int width, out int height, out string error)
    {
        rgba = [];
        width = height = 0;
        error = string.Empty;
        try
        {
            if (png.Length < 8 || png[0] != 0x89 || png[1] != 0x50 || png[2] != 0x4E || png[3] != 0x47)
            {
                error = "不是 PNG 文件";
                return false;
            }

            var pos = 8;
            int w = 0, h = 0, bitDepth = 0, colorType = 0, interlace = 0;
            var idat = new List<byte[]>();
            while (pos + 8 <= png.Length)
            {
                var len = (int)ReadBE32(png, pos);
                var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                var dataStart = pos + 8;
                if (dataStart + len + 4 > png.Length)
                    break;

                switch (type)
                {
                    case "IHDR":
                        w = (int)ReadBE32(png, dataStart);
                        h = (int)ReadBE32(png, dataStart + 4);
                        bitDepth = png[dataStart + 8];
                        colorType = png[dataStart + 9];
                        interlace = png[dataStart + 12];
                        break;
                    case "IDAT":
                        var chunk = new byte[len];
                        Array.Copy(png, dataStart, chunk, 0, len);
                        idat.Add(chunk);
                        break;
                    case "IEND":
                        pos = png.Length;
                        break;
                }
                if (pos == png.Length)
                    break;
                pos = dataStart + len + 4; // 跳过 CRC
            }

            if (w <= 0 || h <= 0 || h > 8192 || w > 8192)
            {
                error = "PNG 尺寸无效";
                return false;
            }
            if (bitDepth != 8)
            {
                error = $"不支持的位深 {bitDepth}(仅支持 8-bit)";
                return false;
            }
            if (interlace != 0)
            {
                error = "不支持隔行(Adam7)PNG";
                return false;
            }
            if (colorType is not (0 or 2 or 4 or 6))
            {
                error = $"不支持的 color type {colorType}(调色板图请另存为 RGB/RGBA)";
                return false;
            }
            if (idat.Count == 0)
            {
                error = "PNG 缺少图像数据";
                return false;
            }

            // zlib 解压
            var total = 0;
            foreach (var c in idat) total += c.Length;
            var compressed = new byte[total];
            var off = 0;
            foreach (var c in idat)
            {
                Array.Copy(c, 0, compressed, off, c.Length);
                off += c.Length;
            }
            using var ms = new MemoryStream(compressed);
            using var zlib = new ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
            var raw = new MemoryStream();
            zlib.CopyTo(raw);
            var rawBytes = raw.ToArray();

            var channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
            var stride = w * channels;
            var expected = (stride + 1) * h;
            if (rawBytes.Length < expected)
            {
                error = $"PNG 数据不完整({rawBytes.Length}/{expected})";
                return false;
            }

            // 反滤波
            var unfiltered = new byte[stride * h];
            var prev = -stride;
            for (var y = 0; y < h; y++)
            {
                var filter = rawBytes[y * (stride + 1)];
                var src = y * (stride + 1) + 1;
                var dst = y * stride;
                for (var x = 0; x < stride; x++)
                {
                    var a = x >= channels ? unfiltered[dst + x - channels] : (byte)0;
                    var b = prev >= 0 ? unfiltered[prev + x] : (byte)0;
                    var c = x >= channels && prev >= 0 ? unfiltered[prev + x - channels] : (byte)0;
                    var v = rawBytes[src + x];
                    unfiltered[dst + x] = filter switch
                    {
                        0 => v,
                        1 => (byte)(v + a),
                        2 => (byte)(v + b),
                        3 => (byte)(v + (byte)((a + b) >> 1)),
                        4 => (byte)(v + Paeth(a, b, c)),
                        _ => throw new InvalidDataException($"未知 PNG 滤波 {filter}"),
                    };
                }
                prev = dst;
            }

            // 展开 RGBA
            rgba = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                var o = i * 4;
                switch (colorType)
                {
                    case 0: // 灰度
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = unfiltered[i];
                        rgba[o + 3] = 255;
                        break;
                    case 4: // 灰度+alpha
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = unfiltered[i * 2];
                        rgba[o + 3] = unfiltered[i * 2 + 1];
                        break;
                    case 2: // RGB
                        rgba[o] = unfiltered[i * 3];
                        rgba[o + 1] = unfiltered[i * 3 + 1];
                        rgba[o + 2] = unfiltered[i * 3 + 2];
                        rgba[o + 3] = 255;
                        break;
                    default: // RGBA
                        rgba[o] = unfiltered[i * 4];
                        rgba[o + 1] = unfiltered[i * 4 + 1];
                        rgba[o + 2] = unfiltered[i * 4 + 2];
                        rgba[o + 3] = unfiltered[i * 4 + 3];
                        break;
                }
            }

            width = w;
            height = h;
            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static uint ReadBE32(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
}
