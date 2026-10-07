using System.Runtime.InteropServices;
using SphereMapPlus.Native;

namespace SphereMapPlus.Probe;

/// <summary>
/// GPU 切片回读:建与源纹理同结构的 staging 资源,CopyResource 整组拷贝后按子资源索引 Map 读取各切片。
/// 只使用已离线实证的调用:CreateTexture2D(5)、CopyResource(47)、Map(14)、Unmap(15)。
/// 用 ID3D11Multithread 的 Enter/Leave 包住对立即上下文的操作,避免与渲染线程竞争。
/// 支持非压缩格式(RGBA/BGRA/R8)与 BC1(解码为 RGBA)。
/// </summary>
internal static unsafe class SliceReader
{
    public static uint CalcSubresource(int mip, int slice, int mipCount)
        => (uint)(mip + slice * mipCount);

    /// <summary>
    /// 回读全部子资源(32 切片 × 9 mip)的原始数据(BC1 块字节 / 非压缩像素行),用于快照与重组。
    /// staging 描述符优先用目标纹理的真实 D3D 描述符(GetDesc),避免任何隐性不匹配导致 CopyResource 被静默忽略。
    /// 输出实际使用的描述符,调用方应据此确定子资源尺寸。索引与 D3D 一致:mip + slice × mipCount。
    /// </summary>
    public static bool TryReadAllRaw(nint d3dDevice, nint d3dContext, nint d3dTexture, int arraySize, int mipCount, int width, int height, int dxgiFormat, out byte[][] subs, out D3D11.Texture2DDesc liveDesc, out string error)
    {
        subs = [];
        liveDesc = default;
        error = string.Empty;
        var dev = (void*)d3dDevice;
        var ctx = (void*)d3dContext;
        var tex = (void*)d3dTexture;
        if (dev == null || ctx == null || tex == null)
        {
            error = "设备/上下文/纹理指针为空";
            return false;
        }

        // 真实描述符优先
        D3D11.GetTexture2DDesc(tex, out var live);
        if (live.Width == 0 || live.Height == 0 || live.MipLevels == 0 || live.ArraySize == 0)
        {
            // GetDesc 不可用时退回参数构造
            live = new D3D11.Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)mipCount,
                ArraySize = (uint)arraySize,
                Format = dxgiFormat,
                SampleDesc = new D3D11.SampleDesc { Count = 1, Quality = 0 },
            };
        }
        live.Usage = D3D11.UsageStaging;
        live.BindFlags = 0;
        live.CpuAccessFlags = D3D11.CpuAccessRead;
        liveDesc = live;

        var w = (int)live.Width;
        var h = (int)live.Height;
        var mips = (int)live.MipLevels;
        var slices = (int)live.ArraySize;
        var fmt = live.Format;
        var bpp = D3D11.BytesPerPixel(fmt);
        var bpb = D3D11.BytesPerBlock(fmt);
        if (bpp <= 0 && bpb <= 0)
        {
            error = $"格式 0x{fmt:X} 暂不支持";
            return false;
        }

        var hr = D3D11.CreateTexture2D(dev, live, null, out var staging);
        if (hr != 0 || staging == null)
        {
            error = $"创建 staging 失败: 0x{hr:X8}";
            return false;
        }

        try
        {
            var haveLock = D3D11.TryGetMultithread(ctx, out var mt);
            if (haveLock)
                D3D11.Enter(mt);
            try
            {
                D3D11.CopyResource(ctx, staging, tex);
                var total = slices * mips;
                var result = new byte[total][];
                for (var slice = 0; slice < slices; slice++)
                {
                    for (var mip = 0; mip < mips; mip++)
                    {
                        var sw = Math.Max(1, w >> mip);
                        var sh = Math.Max(1, h >> mip);
                        var idx = (int)CalcSubresource(mip, slice, mips);
                        hr = D3D11.Map(ctx, staging, (uint)idx, D3D11.MapRead, 0, out var mapped);
                        if (hr != 0 || mapped.Data == null)
                        {
                            error = $"Map 子资源 {idx} 失败: 0x{hr:X8}";
                            return false;
                        }
                        try
                        {
                            result[idx] = CopySubresourceRaw((byte*)mapped.Data, (int)mapped.RowPitch, sw, sh, bpp, bpb);
                        }
                        finally
                        {
                            D3D11.Unmap(ctx, staging, (uint)idx);
                        }
                    }
                }
                subs = result;
                return true;
            }
            finally
            {
                if (haveLock)
                    D3D11.Leave(mt);
            }
        }
        finally
        {
            D3D11.Release(staging);
        }
    }

    /// <summary>单子资源紧凑化拷贝(压缩格式按块行,非压缩按像素行)。</summary>
    private static byte[] CopySubresourceRaw(byte* src, int rowPitch, int w, int h, int bpp, int bpb)
    {
        byte[] raw;
        int rowLen;
        int rows;
        if (bpb > 0)
        {
            var bw = (w + 3) / 4;
            var bh = (h + 3) / 4;
            rowLen = bw * bpb;
            rows = bh;
            raw = new byte[rowLen * rows];
        }
        else
        {
            rowLen = w * bpp;
            rows = h;
            raw = new byte[rowLen * rows];
        }
        fixed (byte* dst = raw)
        {
            for (var y = 0; y < rows; y++)
                Buffer.MemoryCopy(src + y * rowPitch, dst + y * rowLen, rowLen, rowLen);
        }
        return raw;
    }

    /// <summary>
    /// 把整组子资源数据写入到既存纹理(上传路径):建同结构 staging(带初始数据)→ CopyResource 到目标。
    /// subs 索引与 D3D 一致:mip + slice × mipCount。用于替换/还原切片内容,不触碰任何指针。
    /// </summary>
    public static bool UploadAllRaw(nint d3dDevice, nint d3dContext, nint d3dTargetTexture, int arraySize, int mipCount, int width, int height, int dxgiFormat, byte[][] subs, out string error)
    {
        error = string.Empty;
        var dev = (void*)d3dDevice;
        var ctx = (void*)d3dContext;
        var tex = (void*)d3dTargetTexture;
        if (dev == null || ctx == null || tex == null)
        {
            error = "设备/上下文/纹理指针为空";
            return false;
        }

        // 拼接为连续缓冲
        var offsets = new int[subs.Length];
        var total = 0;
        for (var i = 0; i < subs.Length; i++)
        {
            offsets[i] = total;
            total += subs[i].Length;
        }
        var full = new byte[total];
        for (var i = 0; i < subs.Length; i++)
            Array.Copy(subs[i], 0, full, offsets[i], subs[i].Length);

        // staging 描述符以目标纹理的真实描述符为准(仅改用法/访问标志),确保 CopyResource 不被静默忽略
        D3D11.GetTexture2DDesc(tex, out var live);
        if (live.Width == 0 || live.Height == 0 || live.MipLevels == 0 || live.ArraySize == 0)
        {
            live = new D3D11.Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)mipCount,
                ArraySize = (uint)arraySize,
                Format = dxgiFormat,
                SampleDesc = new D3D11.SampleDesc { Count = 1, Quality = 0 },
            };
        }
        live.Usage = D3D11.UsageStaging;
        live.BindFlags = 0;
        live.CpuAccessFlags = D3D11.CpuAccessWrite;

        var bpp = D3D11.BytesPerPixel(live.Format);
        var bpb = D3D11.BytesPerBlock(live.Format);
        if (bpp <= 0 && bpb <= 0)
        {
            error = $"格式 0x{live.Format:X} 暂不支持";
            return false;
        }

        try
        {
            fixed (byte* pFull = full)
            fixed (D3D11.SubresourceData* pInit = new D3D11.SubresourceData[subs.Length])
            {
                var lw = (int)live.Width;
                var lh = (int)live.Height;
                var lm = (int)live.MipLevels;
                var ls = (int)live.ArraySize;
                for (var slice = 0; slice < ls; slice++)
                {
                    for (var mip = 0; mip < lm; mip++)
                    {
                        var idx = (int)CalcSubresource(mip, slice, lm);
                        var w = Math.Max(1, lw >> mip);
                        var h = Math.Max(1, lh >> mip);
                        nint rowLen, sliceLen;
                        if (bpb > 0)
                        {
                            var bw = (w + 3) / 4;
                            var bh = (h + 3) / 4;
                            rowLen = bw * bpb;
                            sliceLen = bw * bh * bpb;
                        }
                        else
                        {
                            rowLen = w * bpp;
                            sliceLen = w * h * bpp;
                        }
                        pInit[idx].Data = pFull + offsets[idx];
                        pInit[idx].Pitch = (uint)rowLen;
                        pInit[idx].SlicePitch = (uint)sliceLen;
                    }
                }

                var hr = D3D11.CreateTexture2D(dev, live, pInit, out var staging);
                if (hr != 0 || staging == null)
                {
                    error = $"创建上传 staging 失败: 0x{hr:X8}";
                    return false;
                }

                try
                {
                    var haveLock = D3D11.TryGetMultithread(ctx, out var mt);
                    if (haveLock)
                        D3D11.Enter(mt);
                    try
                    {
                        D3D11.CopyResource(ctx, tex, staging);
                    }
                    finally
                    {
                        if (haveLock)
                            D3D11.Leave(mt);
                    }
                    return true;
                }
                finally
                {
                    D3D11.Release(staging);
                }
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// 用整组子资源数据创建一个全新的 DEFAULT 纹理(带初始数据)。
    /// 游戏的 .tex 资源是 IMMUTABLE(只读),无法就地写入,因此导入采用「新建纹理 + 指针交换」。
    /// desc 传目标纹理的真实描述符;内部强制 Usage=DEFAULT、确保 SHADER_RESOURCE 绑定、清空 CPU 访问。
    /// </summary>
    public static bool CreateFullTexture(nint d3dDevice, D3D11.Texture2DDesc desc, int mipCount, byte[][] subs, out nint newTexture, out string error)
    {
        newTexture = 0;
        error = string.Empty;
        var dev = (void*)d3dDevice;
        if (dev == null)
        {
            error = "设备为空";
            return false;
        }

        var bpp = D3D11.BytesPerPixel(desc.Format);
        var bpb = D3D11.BytesPerBlock(desc.Format);
        if (bpp <= 0 && bpb <= 0)
        {
            error = $"格式 0x{desc.Format:X} 暂不支持";
            return false;
        }

        desc.Usage = 0; // DEFAULT
        desc.CpuAccessFlags = 0;
        if ((desc.BindFlags & 0x8) == 0) // SHADER_RESOURCE
            desc.BindFlags |= 0x8;

        var lw = (int)desc.Width;
        var lh = (int)desc.Height;
        var lm = (int)desc.MipLevels;
        var ls = (int)desc.ArraySize;
        if (subs.Length != ls * lm)
        {
            error = $"子资源数量不符({subs.Length} != {ls * lm})";
            return false;
        }

        var offsets = new int[subs.Length];
        var total = 0;
        for (var i = 0; i < subs.Length; i++)
        {
            offsets[i] = total;
            total += subs[i].Length;
        }
        var full = new byte[total];
        for (var i = 0; i < subs.Length; i++)
            Array.Copy(subs[i], 0, full, offsets[i], subs[i].Length);

        try
        {
            fixed (byte* pFull = full)
            fixed (D3D11.SubresourceData* pInit = new D3D11.SubresourceData[subs.Length])
            {
                for (var slice = 0; slice < ls; slice++)
                {
                    for (var mip = 0; mip < lm; mip++)
                    {
                        var idx = (int)CalcSubresource(mip, slice, lm);
                        var w = Math.Max(1, lw >> mip);
                        var h = Math.Max(1, lh >> mip);
                        nint rowLen, sliceLen;
                        if (bpb > 0)
                        {
                            var bw = (w + 3) / 4;
                            var bh = (h + 3) / 4;
                            rowLen = bw * bpb;
                            sliceLen = bw * bh * bpb;
                        }
                        else
                        {
                            rowLen = w * bpp;
                            sliceLen = w * h * bpp;
                        }
                        pInit[idx].Data = pFull + offsets[idx];
                        pInit[idx].Pitch = (uint)rowLen;
                        pInit[idx].SlicePitch = (uint)sliceLen;
                    }
                }

                var hr = D3D11.CreateTexture2D(dev, desc, pInit, out var tex);
                if (hr != 0 || tex == null)
                {
                    error = $"创建纹理失败: 0x{hr:X8}";
                    return false;
                }
                newTexture = (nint)tex;
                return true;
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    /// <summary>游戏的 D3D 设备(Forwarder),供 UI 层创建预览纹理用。</summary>
    public static nint GameDevice() => KernelForwarder;

    /// <summary>游戏内便捷入口:自动获取游戏 D3D 设备(Forwarder)与立即上下文后执行回读。</summary>
    public static bool TryReadSlice(nint d3dTexture, int slice, int arraySize, int mipCount, int width, int height, int dxgiFormat, out byte[] rgba, out string error)
        => TryReadSlice(KernelForwarder, KernelDeviceContext, d3dTexture, slice, arraySize, mipCount, width, height, dxgiFormat, out rgba, out error);

    /// <summary>核心回读:显式提供 D3D 设备与立即上下文(可离线测试)。</summary>
    public static bool TryReadSlice(nint d3dDevice, nint d3dContext, nint d3dTexture, int slice, int arraySize, int mipCount, int width, int height, int dxgiFormat, out byte[] rgba, out string error)
    {
        rgba = [];
        error = string.Empty;
        try
        {
            var tex = (void*)d3dTexture;
            if (tex == null)
            {
                error = "D3D 纹理指针为空";
                return false;
            }

            var bpp = D3D11.BytesPerPixel(dxgiFormat);
            var bpb = D3D11.BytesPerBlock(dxgiFormat);
            if (bpp <= 0 && bpb <= 0)
            {
                error = $"格式 0x{dxgiFormat:X} 暂不支持回读";
                return false;
            }

            var dev = (void*)d3dDevice;
            if (dev == null)
            {
                error = "D3D 设备为空";
                return false;
            }

            var ctx = (void*)d3dContext;
            if (ctx == null)
            {
                error = "D3D 上下文为空";
                return false;
            }

            // staging:与源纹理同结构(CopyResource 要求完全一致)
            var desc = new D3D11.Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = (uint)mipCount,
                ArraySize = (uint)arraySize,
                Format = dxgiFormat,
                SampleDesc = new D3D11.SampleDesc { Count = 1, Quality = 0 },
                Usage = D3D11.UsageStaging,
                BindFlags = 0,
                CpuAccessFlags = D3D11.CpuAccessRead,
                MiscFlags = 0,
            };
            var hr = D3D11.CreateTexture2D(dev, desc, null, out var staging);
            if (hr != 0 || staging == null)
            {
                error = $"创建 staging 纹理失败: HRESULT 0x{hr:X8}";
                return false;
            }

            try
            {
                var haveLock = D3D11.TryGetMultithread(ctx, out var mt);
                if (haveLock)
                    D3D11.Enter(mt);

                try
                {
                    D3D11.CopyResource(ctx, staging, tex);
                    hr = D3D11.Map(ctx, staging, CalcSubresource(0, slice, mipCount), D3D11.MapRead, 0, out var mapped);
                    if (hr != 0 || mapped.Data == null)
                    {
                        error = $"Map 失败: HRESULT 0x{hr:X8}";
                        return false;
                    }

                    try
                    {
                        var src = (byte*)mapped.Data;
                        var rowPitch = (int)mapped.RowPitch;
                        if (bpp > 0)
                        {
                            rgba = CopyRows(src, rowPitch, width, height, bpp);
                            if (dxgiFormat is 87 or 91) // BGRA → RGBA
                                for (var i = 0; i < rgba.Length; i += 4)
                                    (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
                        }
                        else
                        {
                            var blocksW = (width + 3) / 4;
                            var blocksH = (height + 3) / 4;
                            var blockRowLen = blocksW * bpb;
                            var blocks = new byte[blockRowLen * blocksH];
                            fixed (byte* dst = blocks)
                            {
                                for (var y = 0; y < blocksH; y++)
                                    Buffer.MemoryCopy(src + y * rowPitch, dst + y * blockRowLen, blockRowLen, blockRowLen);
                            }
                            rgba = DecodeBc1(blocks, width, height);
                        }
                    }
                    finally
                    {
                        D3D11.Unmap(ctx, staging, CalcSubresource(0, slice, mipCount));
                    }
                }
                finally
                {
                    if (haveLock)
                        D3D11.Leave(mt);
                }

                return true;
            }
            finally
            {
                D3D11.Release(staging);
            }
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    private static byte[] CopyRows(byte* src, int rowPitch, int width, int height, int bpp)
    {
        var outBytes = new byte[width * height * bpp];
        var rowLen = width * bpp;
        fixed (byte* dst = outBytes)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(src + y * rowPitch, dst + y * rowLen, rowLen, rowLen);
        }
        return outBytes;
    }

    /// <summary>BC1(DXT1)→RGBA8。标准 4 色/3 色调色板解码。</summary>
    public static byte[] DecodeBc1(byte[] blocks, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        var blocksW = (width + 3) / 4;
        var blocksH = (height + 3) / 4;
        var palette = new Vector4[4];
        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                var blockOffset = (by * blocksW + bx) * 8;
                var c0 = (uint)(blocks[blockOffset] | (blocks[blockOffset + 1] << 8));
                var c1 = (uint)(blocks[blockOffset + 2] | (blocks[blockOffset + 3] << 8));
                uint indices = blocks[blockOffset + 4]
                             | (uint)(blocks[blockOffset + 5] << 8)
                             | (uint)(blocks[blockOffset + 6] << 16)
                             | (uint)(blocks[blockOffset + 7] << 24);

                palette[0] = Rgb565ToRgba(c0);
                palette[1] = Rgb565ToRgba(c1);
                if (c0 > c1)
                {
                    palette[2] = new Vector4(
                        (palette[0].X * 2 + palette[1].X) / 3,
                        (palette[0].Y * 2 + palette[1].Y) / 3,
                        (palette[0].Z * 2 + palette[1].Z) / 3, 1f);
                    palette[3] = new Vector4(
                        (palette[0].X + palette[1].X * 2) / 3,
                        (palette[0].Y + palette[1].Y * 2) / 3,
                        (palette[0].Z + palette[1].Z * 2) / 3, 1f);
                }
                else
                {
                    palette[2] = new Vector4(
                        (palette[0].X + palette[1].X) / 2,
                        (palette[0].Y + palette[1].Y) / 2,
                        (palette[0].Z + palette[1].Z) / 2, 1f);
                    palette[3] = new Vector4(0f, 0f, 0f, 0f);
                }

                for (var t = 0; t < 16; t++)
                {
                    var x = bx * 4 + (t & 3);
                    var y = by * 4 + (t >> 2);
                    if (x >= width || y >= height)
                        continue;
                    var idx = (int)((indices >> (t * 2)) & 3);
                    var p = palette[idx];
                    var o = (y * width + x) * 4;
                    rgba[o] = (byte)(p.X * 255f + 0.5f);
                    rgba[o + 1] = (byte)(p.Y * 255f + 0.5f);
                    rgba[o + 2] = (byte)(p.Z * 255f + 0.5f);
                    rgba[o + 3] = (byte)(p.W * 255f + 0.5f);
                }
            }
        }
        return rgba;
    }

    private static Vector4 Rgb565ToRgba(uint c)
    {
        var r = (c >> 11) & 0x1F;
        var g = (c >> 5) & 0x3F;
        var b = c & 0x1F;
        return new Vector4(r / 31f, g / 63f, b / 31f, 1f);
    }

    /// <summary>游戏的 CID3D11Forwarder(Kernel::Device 偏移 0xE0AA8,具备 ID3D11Device vtable)。</summary>
    private static nint KernelForwarder
    {
        get
        {
            var dev = Device.Instance();
            if (dev == null)
                return 0;
            return *(nint*)((byte*)dev + 0xE0AA8);
        }
    }

    /// <summary>游戏的 ID3D11DeviceContext4(Kernel::Device 偏移 0xE0AB0)。</summary>
    private static nint KernelDeviceContext
    {
        get
        {
            var dev = Device.Instance();
            if (dev == null)
                return 0;
            return *(nint*)((byte*)dev + 0xE0AB0);
        }
    }
}
