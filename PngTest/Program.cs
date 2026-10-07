using System.Numerics;
using System.Runtime.InteropServices;
using SphereMapPlus.Imaging;
using SphereMapPlus.Native;
using SphereMapPlus.Probe;

// ===== 离线验证:图像管线 + D3D 导入全链路 =====

[DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
    nint featureLevels, uint numFeatureLevels, uint sdkVersion,
    out nint device, out nint featureLevel, out nint immediateContext);

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "✔" : "✖")} {name}");
    if (!ok) failures++;
}

// ---------- 1. PNG 读写往返 ----------
{
    const int w = 64, h = 64;
    var src = new byte[w * h * 4];
    var rng = new Random(42);
    rng.NextBytes(src);
    for (var i = 3; i < src.Length; i += 4) src[i] = 255; // alpha 固定(BC1 不透明)
    var path = Path.Combine(Path.GetTempPath(), "smp_roundtrip.png");
    PngWriter.Save(path, w, h, src);
    var ok = PngReader.TryDecode(File.ReadAllBytes(path), out var decoded, out var dw, out var dh, out var err);
    Check($"PNG 读写往返({dw}x{dh})", ok && dw == w && dh == h && decoded.SequenceEqual(src));
    if (!ok) Console.WriteLine("  " + err);
}

// ---------- 2. BC1 编码质量 ----------
{
    // 纯色
    var solid = new byte[64 * 64 * 4];
    for (var i = 0; i < solid.Length; i += 4) { solid[i] = 30; solid[i + 1] = 120; solid[i + 2] = 240; solid[i + 3] = 255; }
    var enc = Bc1Encoder.Encode(solid, 64, 64);
    var dec = SliceReader.DecodeBc1(enc, 64, 64);
    var maxErr = 0;
    for (var i = 0; i < dec.Length; i++) maxErr = Math.Max(maxErr, Math.Abs(dec[i] - solid[i]));
    Check($"BC1 纯色编码误差(最大 {maxErr})", maxErr <= 6);

    // 渐变
    var grad = new byte[64 * 64 * 4];
    for (var y = 0; y < 64; y++)
    for (var x = 0; x < 64; x++)
    {
        var o = (y * 64 + x) * 4;
        grad[o] = (byte)(x * 4); grad[o + 1] = (byte)(y * 4); grad[o + 2] = 128; grad[o + 3] = 255;
    }
    enc = Bc1Encoder.Encode(grad, 64, 64);
    dec = SliceReader.DecodeBc1(enc, 64, 64);
    var totalErr = 0L;
    for (var i = 0; i < dec.Length; i++) totalErr += Math.Abs(dec[i] - grad[i]);
    var avg = totalErr / dec.Length;
    Check($"BC1 渐变编码平均误差 {avg}(应 < 12)", avg < 12);
}

// ---------- 3. mip 链 ----------
{
    var img = new byte[256 * 256 * 4];
    var mips = ImageScale.GenMipChain(img, 256, 256);
    var dimsOk = mips.Length == 9;
    for (var i = 0; i < mips.Length && dimsOk; i++)
    {
        var dim = 256 >> i;
        dimsOk = mips[i].Length == dim * dim * 4;
    }
    Check("mip 链 256→9 级尺寸正确", dimsOk);

    var small = ImageScale.Resize(img, 256, 256, 100, 50);
    Check("任意尺寸缩放", small.Length == 100 * 50 * 4);
}

// ---------- 4. D3D 导入全链路模拟(真实设备) ----------
{
    var hr = D3D11CreateDevice(0, 1, 0, 0, 0, 0, 7, out var device, out var fl, out var context);
    if (hr != 0) { Console.WriteLine("✖ 无法创建 D3D 设备"); failures++; return; }

    unsafe
    {
        // 2 切片 4x4 BC1:切片0=红/蓝测试块,切片1=绿块
        var slice0 = new byte[] { 0x00, 0xF8, 0x1F, 0x00, 0xE4, 0, 0, 0 };
        var slice1 = new byte[] { 0xE0, 0x07, 0xE0, 0x07, 0, 0, 0, 0 };
        fixed (byte* p0 = slice0, p1 = slice1)
        fixed (D3D11.SubresourceData* pSub = new[]
        {
            new D3D11.SubresourceData { Data = p0, Pitch = 8, SlicePitch = 8 },
            new D3D11.SubresourceData { Data = p1, Pitch = 8, SlicePitch = 8 },
        })
        {
            var texDesc = new D3D11.Texture2DDesc
            {
                Width = 4, Height = 4, MipLevels = 1, ArraySize = 2,
                Format = 71, SampleDesc = new D3D11.SampleDesc { Count = 1, Quality = 0 },
                Usage = 0, BindFlags = 0x8,
            };
            hr = D3D11.CreateTexture2D((void*)device, texDesc, pSub, out var tex);
            Check("建纹理", hr == 0);
            if (hr != 0) return;

            // 快照回读
            var okRead = SliceReader.TryReadAllRaw(device, context, (nint)tex, 2, 1, 4, 4, 71, out var snap, out var snapDesc, out var err);
            Check($"全子资源快照({(okRead ? snap.Length : 0)} 个)", okRead && snap[0].SequenceEqual(slice0) && snap[1].SequenceEqual(slice1));
            if (!okRead) { Console.WriteLine("  " + err); return; }

            // 模拟导入:替换切片1 为蓝色块
            var import = new byte[] { 0xA0, 0x0F, 0xA0, 0x0F, 0, 0, 0, 0 }; // 蓝色
            var data = new byte[2][];
            data[0] = (byte[])snap[0].Clone();
            data[1] = import;

            var okUp = SliceReader.UploadAllRaw(device, context, (nint)tex, 2, 1, 4, 4, 71, data, out var upErr);
            Check("上传(替换切片1)", okUp);
            if (!okUp) { Console.WriteLine("  " + upErr); return; }

            // 再读回验证
            var okRead2 = SliceReader.TryReadAllRaw(device, context, (nint)tex, 2, 1, 4, 4, 71, out var after, out var afterDesc, out var err2);
            Check("替换后切片0 未被破坏", okRead2 && after[0].SequenceEqual(slice0));
            Check("替换后切片1 已更新", okRead2 && after[1].SequenceEqual(import));
            if (!okRead2) Console.WriteLine("  " + err2);

            // 还原(整组快照回写)
            var okRes = SliceReader.UploadAllRaw(device, context, (nint)tex, 2, 1, 4, 4, 71, snap, out var resErr);
            var okRead3 = SliceReader.TryReadAllRaw(device, context, (nint)tex, 2, 1, 4, 4, 71, out var after2, out var afterDesc2, out var err3);
            Check("还原后切片1 恢复原始", okRes && okRead3 && after2[1].SequenceEqual(slice1));
        }
    }
}

Console.WriteLine(failures == 0 ? "== 全部离线验证通过 ==" : $"== {failures} 项失败 ==");
Environment.Exit(failures == 0 ? 0 : 1);
