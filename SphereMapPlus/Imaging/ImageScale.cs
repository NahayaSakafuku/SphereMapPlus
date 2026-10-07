namespace SphereMapPlus.Imaging;

/// <summary>图像缩放(双线性)与 mip 链生成(Box 2×2 逐级减半)。</summary>
internal static class ImageScale
{
    /// <summary>双线性缩放到目标尺寸。</summary>
    public static byte[] Resize(byte[] rgba, int sw, int sh, int dw, int dh)
    {
        if (sw == dw && sh == dh)
            return rgba;
        var dst = new byte[dw * dh * 4];
        var xr = (float)sw / dw;
        var yr = (float)sh / dh;
        for (var y = 0; y < dh; y++)
        {
            var fy = (y + 0.5f) * yr - 0.5f;
            var y0 = (int)Math.Floor(fy);
            var ty = fy - y0;
            var y0c = Math.Clamp(y0, 0, sh - 1);
            var y1c = Math.Clamp(y0 + 1, 0, sh - 1);
            for (var x = 0; x < dw; x++)
            {
                var fx = (x + 0.5f) * xr - 0.5f;
                var x0 = (int)Math.Floor(fx);
                var tx = fx - x0;
                var x0c = Math.Clamp(x0, 0, sw - 1);
                var x1c = Math.Clamp(x0 + 1, 0, sw - 1);

                for (var ch = 0; ch < 4; ch++)
                {
                    var p00 = rgba[(y0c * sw + x0c) * 4 + ch];
                    var p10 = rgba[(y0c * sw + x1c) * 4 + ch];
                    var p01 = rgba[(y1c * sw + x0c) * 4 + ch];
                    var p11 = rgba[(y1c * sw + x1c) * 4 + ch];
                    var v = (p00 * (1 - tx) + p10 * tx) * (1 - ty)
                          + (p01 * (1 - tx) + p11 * tx) * ty;
                    dst[(y * dw + x) * 4 + ch] = (byte)Math.Clamp(v + 0.5f, 0, 255);
                }
            }
        }
        return dst;
    }

    /// <summary>生成完整 mip 链:levels[0] 为原图,逐级 Box 减半直到 1×1。输入尺寸应为 2 的幂。</summary>
    public static byte[][] GenMipChain(byte[] rgba0, int width, int height)
    {
        var levels = new List<byte[]> { rgba0 };
        var w = width;
        var h = height;
        while (w > 1 || h > 1)
        {
            var nw = Math.Max(1, w / 2);
            var nh = Math.Max(1, h / 2);
            levels.Add(BoxDown(levels[^1], w, h, nw, nh));
            w = nw;
            h = nh;
        }
        return [.. levels];
    }

    /// <summary>Box 滤波减半(4 像素均值的 2×2 版本,处理奇数尺寸边缘)。</summary>
    public static byte[] BoxDown(byte[] rgba, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var y0 = Math.Min(y * 2, sh - 1);
            var y1 = Math.Min(y * 2 + 1, sh - 1);
            for (var x = 0; x < dw; x++)
            {
                var x0 = Math.Min(x * 2, sw - 1);
                var x1 = Math.Min(x * 2 + 1, sw - 1);
                for (var ch = 0; ch < 4; ch++)
                {
                    var s = rgba[(y0 * sw + x0) * 4 + ch]
                          + rgba[(y0 * sw + x1) * 4 + ch]
                          + rgba[(y1 * sw + x0) * 4 + ch]
                          + rgba[(y1 * sw + x1) * 4 + ch];
                    dst[(y * dw + x) * 4 + ch] = (byte)((s + 2) / 4);
                }
            }
        }
        return dst;
    }
}
