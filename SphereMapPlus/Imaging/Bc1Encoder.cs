using System.Numerics;

namespace SphereMapPlus.Imaging;

/// <summary>RGBA8 → BC1 编码器。始终使用 4 色模式(c0>c1,不透明),逐块最小/最大亮度端点 + 最近邻索引。</summary>
internal static class Bc1Encoder
{
    public static byte[] Encode(byte[] rgba, int width, int height)
    {
        var blocksW = (width + 3) / 4;
        var blocksH = (height + 3) / 4;
        var blocks = new byte[blocksW * blocksH * 8];
        var px = new Vector3[16];

        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                var count = 0;
                for (var t = 0; t < 16; t++)
                {
                    var x = bx * 4 + (t & 3);
                    var y = by * 4 + (t >> 2);
                    if (x >= width || y >= height)
                        continue;
                    var o = (y * width + x) * 4;
                    px[count++] = new Vector3(rgba[o], rgba[o + 1], rgba[o + 2]);
                }

                EncodeBlock(px, count, blocks, (by * blocksW + bx) * 8);
            }
        }
        return blocks;
    }

    private static void EncodeBlock(Vector3[] px, int count, byte[] blocks, int offset)
    {
        if (count == 0)
        {
            // 全空块:黑
            blocks[offset] = 0; blocks[offset + 1] = 0;
            blocks[offset + 2] = 0; blocks[offset + 3] = 0;
            return;
        }

        // 找亮度最低/最高的像素作为端点
        var minI = 0;
        var maxI = 0;
        float minL = float.MaxValue, maxL = float.MinValue;
        for (var i = 0; i < count; i++)
        {
            var l = Vector3.Dot(px[i], new Vector3(0.299f, 0.587f, 0.114f));
            if (l < minL) { minL = l; minI = i; }
            if (l > maxL) { maxL = l; maxI = i; }
        }

        var c0 = maxI; // 亮端
        var c1 = minI; // 暗端(保证 565 数值上 c0>c1,进入 4 色模式)
        if (Pack565(px[c0]) <= Pack565(px[c1]))
        {
            // 端点 565 编码后可能相等/反序:微调,保证 c0>c1
            (c0, c1) = (c1, c0);
            if (Pack565(px[c0]) <= Pack565(px[c1]))
            {
                // 仍相等(纯色块):把 c1 抬 1,避免落入 3 色透明模式
                var p = px[c1];
                px[c1] = new Vector3(Math.Min(255f, p.X + 1f), p.Y, p.Z);
                if (Pack565(px[c0]) <= Pack565(px[c1]))
                {
                    px[c1] = new Vector3(p.X, Math.Min(255f, p.Y + 1f), p.Z);
                }
            }
        }

        var e0 = px[c0];
        var e1 = px[c1];
        var pal0 = new Vector3(e0.X, e0.Y, e0.Z);
        var pal1 = new Vector3(e1.X, e1.Y, e1.Z);
        var pal2 = (pal0 * 2 + pal1) / 3;
        var pal3 = (pal0 + pal1 * 2) / 3;

        uint indices = 0;
        for (var t = 0; t < count; t++)
        {
            indices |= (uint)Nearest(px[t], pal0, pal1, pal2, pal3) << (t * 2);
        }
        // 剩余 texel(count..15)填索引 0(亮度端点)——超出图像的 texel 不可见

        var w0 = Pack565(e0);
        var w1 = Pack565(e1);
        blocks[offset] = (byte)w0;
        blocks[offset + 1] = (byte)(w0 >> 8);
        blocks[offset + 2] = (byte)w1;
        blocks[offset + 3] = (byte)(w1 >> 8);
        blocks[offset + 4] = (byte)indices;
        blocks[offset + 5] = (byte)(indices >> 8);
        blocks[offset + 6] = (byte)(indices >> 16);
        blocks[offset + 7] = (byte)(indices >> 24);
    }

    private static int Nearest(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var da = Vector3.DistanceSquared(p, a);
        var db = Vector3.DistanceSquared(p, b);
        var dc = Vector3.DistanceSquared(p, c);
        var dd = Vector3.DistanceSquared(p, d);
        var best = 0;
        var bestD = da;
        if (db < bestD) { bestD = db; best = 1; }
        if (dc < bestD) { bestD = dc; best = 2; }
        if (dd < bestD) { bestD = dd; best = 3; }
        return best;
    }

    /// <summary>RGB888 → RGB565。</summary>
    private static uint Pack565(Vector3 c)
        => ((uint)(c.X / 255f * 31f + 0.5f) << 11)
         | ((uint)(c.Y / 255f * 63f + 0.5f) << 5)
         | (uint)(c.Z / 255f * 31f + 0.5f);
}
