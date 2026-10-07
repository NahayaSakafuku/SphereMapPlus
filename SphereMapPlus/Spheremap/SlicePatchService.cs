using System.Text.Json;
using SphereMapPlus.Imaging;
using SphereMapPlus.Native;
using SphereMapPlus.Probe;

namespace SphereMapPlus.Spheremap;

/// <summary>
/// 球面贴图切片导入服务。
/// 游戏的 .tex 资源纹理是 IMMUTABLE(只读),无法就地写入,因此导入采用「新建纹理 + Kernel.Texture 指针交换」:
/// 用完整数据创建 DEFAULT 纹理与对应 SRV,把 Kernel.Texture 内的 D3D 纹理/SRV 指针换成新的;
/// 原始指针被保存,可随时还原。所有 D3D 操作统一在渲染线程(RenderThreadPump)执行;
/// 登录后检测到球面数组就绪/更换时自动重放;连续失败自动熔断。
/// </summary>
public sealed unsafe class SlicePatchService : IDisposable
{
    public sealed class ImportEntry
    {
        public int Slice { get; set; }
        public string PngPath { get; set; } = "";
    }

    private sealed class ImportCache
    {
        public byte[][] EncodedMips = [];
        public DateTime SourceTime;
        public string Error = "";
    }

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IClientState _clientState;
    private readonly string _configPath;

    public List<ImportEntry> Entries { get; private set; } = [];
    public bool AutoApply { get; set; } = true;
    public string LastStatus { get; set; } = string.Empty;

    private readonly Dictionary<int, ImportCache> _cache = new();
    private byte[][]? _snapshot;          // 原始内容快照(游戏纹理世代)
    private nint _snapshotTexPtr;
    private nint _lastPatchedPtr;
    private int _frame;
    private bool _tickFaults;
    private bool _loggedDesc;

    // 实际 D3D 描述符(快照时确定)
    private int _liveW, _liveH, _liveMips, _liveArray, _liveFormat;

    // 指针交换状态
    private nint _swappedKtPtr;           // 已交换的 Kernel.Texture*
    private nint _origD3D, _origSrv;      // 原始指针(还原用)
    private readonly List<nint> _createdTex = new();
    private readonly List<nint> _createdSrv = new();

    // 延迟应用与熔断
    private volatile bool _pendingApply;
    private int _failCount;
    private nint _failPtr;

    public SlicePatchService(IDalamudPluginInterface pi, IPluginLog log, IFramework framework, IClientState clientState)
    {
        _log = log;
        _framework = framework;
        _clientState = clientState;
        _configPath = Path.Combine(pi.GetPluginConfigDirectory(), "sphere_imports.json");
        LoadConfig();
        _framework.Update += Tick;
    }

    // ---------- 配置 ----------
    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
                return;
            var json = File.ReadAllText(_configPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("autoApply", out var aa))
                AutoApply = aa.GetBoolean();
            Entries.Clear();
            if (doc.RootElement.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var entry = new ImportEntry
                    {
                        Slice = e.TryGetProperty("slice", out var s) ? s.GetInt32() : 0,
                        PngPath = e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                    };
                    if (File.Exists(entry.PngPath))
                        Entries.Add(entry);
                }
            }
            _log.Info($"已加载导入配置:{Entries.Count} 项");
        }
        catch (Exception e)
        {
            _log.Error(e, "读取导入配置失败");
        }
    }

    public void SaveConfig()
    {
        try
        {
            var obj = new
            {
                autoApply = AutoApply,
                entries = Entries.Select(e => new { slice = e.Slice, path = e.PngPath }).ToList(),
            };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            _log.Error(e, "保存导入配置失败");
        }
    }

    // ---------- 框架线程 Tick(纯内存检查) ----------
    private void Tick(IFramework framework)
    {
        if (_tickFaults)
            return;
        try
        {
            _frame++;
            if (!AutoApply || Entries.Count == 0 || !_clientState.IsLoggedIn)
                return;
            if (_frame % 60 != 0)
                return;

            var info = SphereArray.ReadInfo();
            if (!info.Loaded || info.D3DTexture == 0 || info.TexArraySize <= 0)
                return;
            if (info.D3DTexture == _lastPatchedPtr)
                return;
            if (_failCount >= 2 && _failPtr == info.D3DTexture)
                return;

            _pendingApply = true;
        }
        catch (Exception e)
        {
            _tickFaults = true;
            _log.Error(e, "自动应用检查异常,已停用");
        }
    }

    /// <summary>由 UI 绘制回调(渲染线程)每帧调用,执行排队的 D3D 工作。</summary>
    public void RenderThreadPump()
    {
        if (!_pendingApply)
            return;
        _pendingApply = false;
        try
        {
            var info = SphereArray.ReadInfo();
            if (!info.Loaded || info.D3DTexture == 0 || info.TexArraySize <= 0)
                return;
            if (info.D3DTexture == _lastPatchedPtr)
                return;

            _log.Info($"(渲染线程)自动应用 {Entries.Count} 项导入...");
            var err = ApplyAll(info);
            if (err == null)
            {
                _failCount = 0;
                _log.Info("自动应用完成");
            }
            else
            {
                _failCount++;
                _failPtr = info.D3DTexture;
                _log.Warning($"自动应用失败({_failCount}/2,{(_failCount >= 2 ? "已熔断,可在窗口手动重试" : "将重试")}): {err}");
                LastStatus = $"自动应用失败: {err}";
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "渲染线程自动应用异常");
        }
    }

    // ---------- 导入执行 ----------
    public string Import(int slice, string pngPath)
    {
        if (!File.Exists(pngPath))
            return "PNG 文件不存在";
        Entries.RemoveAll(e => e.Slice == slice);
        Entries.Add(new ImportEntry { Slice = slice, PngPath = pngPath });
        _cache.Remove(slice);
        SaveConfig();
        _failCount = 0;

        var info = SphereArray.ReadInfo();
        if (!info.Loaded || info.D3DTexture == 0)
        {
            LastStatus = $"切片 {slice} 已保存,等待游戏内球面数组就绪后自动应用";
            return null;
        }
        var err = ApplyAll(info);
        LastStatus = err ?? $"✔ 已导入切片 {slice} ← {Path.GetFileName(pngPath)}(新纹理已交换生效)";
        return err;
    }

    /// <summary>应用一个方案(以切片→PNG 路径集合表示)。返回错误或 null。</summary>
    public string ApplyScheme(string schemeName, IEnumerable<(int Slice, string PngPath)> slices)
    {
        var list = slices.Where(t => File.Exists(t.PngPath)).ToList();
        Entries.Clear();
        foreach (var (s, p) in list)
            Entries.Add(new ImportEntry { Slice = s, PngPath = p });
        SaveConfig();
        _failCount = 0;

        var info = SphereArray.ReadInfo();
        if (!info.Loaded || info.D3DTexture == 0)
        {
            LastStatus = $"方案 {schemeName} 已保存,等待球面数组就绪后自动应用";
            return null;
        }
        var err = ApplyAll(info);
        LastStatus = err ?? $"✔ 已应用方案 {schemeName}";
        return err;
    }

    public string RemoveImport(int slice)
    {
        Entries.RemoveAll(e => e.Slice == slice);
        _cache.Remove(slice);
        SaveConfig();
        _failCount = 0;
        var info = SphereArray.ReadInfo();
        if (!info.Loaded || info.D3DTexture == 0)
        {
            LastStatus = $"切片 {slice} 的导入已移除";
            return null;
        }
        var err = ApplyAll(info);
        LastStatus = err ?? $"✔ 已还原切片 {slice} 的原始内容";
        return err;
    }

    public string RestoreAll()
    {
        Entries.Clear();
        _cache.Clear();
        SaveConfig();
        _failCount = 0;
        RestorePointers();
        _lastPatchedPtr = 0;
        LastStatus = "✔ 已全部还原为原始内容";
        return null;
    }

    // ---------- 核心 ----------
    private string EnsureSnapshot(in SphereArray.SphereInfo info)
    {
        // 已处于交换世代:快照(原始内容)依然有效
        var kt = SphereArray.GetKernelTexturePtr();
        if (_snapshot != null && kt != null && _swappedKtPtr == (nint)kt)
            return null;
        if (_snapshot != null && _snapshotTexPtr == info.D3DTexture)
            return null;

        // 新的游戏纹理世代:丢弃旧交换状态
        ReleaseCreated();
        _swappedKtPtr = 0;
        _origD3D = 0;
        _origSrv = 0;

        if (!SliceReader.TryReadAllRaw(KernelForwarderSafe(), KernelContextSafe(),
                info.D3DTexture, info.TexArraySize, info.MipCount, info.TexActualWidth, info.TexActualHeight,
                info.DxgiFormat, out var snap, out var desc, out var err))
            return "快照失败: " + err;

        _snapshot = snap;
        _snapshotTexPtr = info.D3DTexture;
        _liveW = (int)desc.Width;
        _liveH = (int)desc.Height;
        _liveMips = (int)desc.MipLevels;
        _liveArray = (int)desc.ArraySize;
        _liveFormat = desc.Format;
        if (!_loggedDesc)
        {
            _loggedDesc = true;
            _log.Info($"实际 D3D 描述符: {_liveW}x{_liveH} Mips={_liveMips} Array={_liveArray} Format=0x{_liveFormat:X} Usage={(int)desc.Usage} Sample={desc.SampleDesc.Count} Misc=0x{desc.MiscFlags:X}");
        }
        else
        {
            _log.Info($"已记录原始快照({snap.Length} 个子资源,{_liveW}x{_liveH} M{_liveMips} A{_liveArray})");
        }
        return null;
    }

    /// <summary>核心:快照 + 应用导入 + 创建新纹理 + 指针交换 + 校验。返回错误或 null。</summary>
    internal string ApplyAll(in SphereArray.SphereInfo info)
    {
        var snapErr = EnsureSnapshot(info);
        if (snapErr != null)
            return snapErr;
        if (_liveFormat != 71)
            return $"仅支持 BC1 数组(实际 DXGI 0x{_liveFormat:X})";

        // 组装:快照为底,叠加导入
        var data = new byte[_snapshot!.Length][];
        for (var i = 0; i < _snapshot.Length; i++)
            data[i] = (byte[])_snapshot[i].Clone();

        foreach (var entry in Entries)
        {
            if (entry.Slice < 0 || entry.Slice >= _liveArray)
                return $"切片号 {entry.Slice} 超出范围(0-{_liveArray - 1})";
            var enc = GetEncoded(entry, _liveW, _liveH);
            if (enc.Error.Length > 0)
                return $"切片 {entry.Slice}: {enc.Error}";
            for (var mip = 0; mip < _liveMips && mip < enc.EncodedMips.Length; mip++)
            {
                var idx = (int)SliceReader.CalcSubresource(mip, entry.Slice, _liveMips);
                if (data[idx].Length != enc.EncodedMips[mip].Length)
                    return $"切片 {entry.Slice} mip{mip} 尺寸不符({enc.EncodedMips[mip].Length} != {data[idx].Length})";
                data[idx] = enc.EncodedMips[mip];
            }
        }

        var swapErr = SwapInTexture(data);
        if (swapErr != null)
            return swapErr;

        // 写回校验:交换后 info.D3DTexture 应为新纹理;回读比对
        var after = SphereArray.ReadInfo();
        if (after.Loaded && after.D3DTexture != 0)
        {
            string vErr = null;
            byte[][] verify = null;
            var verified = SliceReader.TryReadAllRaw(KernelForwarderSafe(), KernelContextSafe(), after.D3DTexture,
                _liveArray, _liveMips, _liveW, _liveH, _liveFormat, out verify, out _, out vErr);
            if (verified)
            {
                foreach (var s in Entries.Select(e => e.Slice).Distinct())
                {
                    var enc = GetEncoded(Entries.First(e => e.Slice == s), _liveW, _liveH);
                    var idx = (int)SliceReader.CalcSubresource(0, s, _liveMips);
                    var match = verify[idx].AsSpan().SequenceEqual(enc.EncodedMips[0]);
                    _log.Info($"写回校验 切片{s}: {(match ? "✔ 新纹理内容确认" : "✖ 不一致!")}");
                }
            }
        }

        _lastPatchedPtr = after.D3DTexture;
        return null;
    }

    /// <summary>诊断:全部切片刷成不同纯色(新建纹理 + 指针交换)。</summary>
    internal string ApplyTestPattern(in SphereArray.SphereInfo info)
    {
        var snapErr = EnsureSnapshot(info);
        if (snapErr != null)
            return snapErr;
        if (_liveFormat != 71)
            return $"仅支持 BC1 数组(实际 DXGI 0x{_liveFormat:X})";

        var data = new byte[_snapshot!.Length][];
        for (var i = 0; i < _snapshot.Length; i++)
            data[i] = (byte[])_snapshot[i].Clone();

        for (var slice = 0; slice < _liveArray; slice++)
        {
            var (r, g, b) = HsvToRgb(slice / (float)_liveArray, 1f, 1f);
            for (var mip = 0; mip < _liveMips; mip++)
            {
                var w = Math.Max(1, _liveW >> mip);
                var h = Math.Max(1, _liveH >> mip);
                var rgba = new byte[w * h * 4];
                for (var i = 0; i < w * h; i++)
                {
                    rgba[i * 4] = r;
                    rgba[i * 4 + 1] = g;
                    rgba[i * 4 + 2] = b;
                    rgba[i * 4 + 3] = 255;
                }
                var idx = (int)SliceReader.CalcSubresource(mip, slice, _liveMips);
                data[idx] = Bc1Encoder.Encode(rgba, w, h);
            }
        }

        var swapErr = SwapInTexture(data);
        if (swapErr != null)
            return swapErr;
        _lastPatchedPtr = SphereArray.ReadInfo().D3DTexture;
        return null;
    }

    /// <summary>创建新纹理与新 SRV,交换 Kernel.Texture 的两个指针。世代变化时保存原始指针。</summary>
    private string SwapInTexture(byte[][] data)
    {
        var kt = SphereArray.GetKernelTexturePtr();
        if (kt == null)
            return "无法获取 Kernel.Texture(球面数组渲染对象)";

        if (_swappedKtPtr != (nint)kt)
        {
            // 新世代:保存原始指针,清理上一世代创建的资源
            ReleaseCreated();
            _origD3D = (nint)kt->D3D11Texture2D;
            _origSrv = (nint)kt->D3D11ShaderResourceView;
            _swappedKtPtr = (nint)kt;
            _log.Info($"保存原始指针: D3D=0x{_origD3D:X} SRV=0x{_origSrv:X}");
        }

        // 用当前 SRV 的描述符(交换前后格式一致)
        var curSrv = (void*)kt->D3D11ShaderResourceView;
        if (curSrv == null)
            return "当前 SRV 为空";
        D3D11.GetSrvDesc(curSrv, out var srvDesc);

        // 新纹理的描述符:取当前 D3D 纹理的真实描述符
        D3D11.GetTexture2DDesc((void*)kt->D3D11Texture2D, out var liveDesc);
        if (liveDesc.Width == 0)
            return "无法读取当前纹理描述符";

        if (!SliceReader.CreateFullTexture(KernelForwarderSafe(), liveDesc, _liveMips, data, out var newTex, out var cErr))
            return cErr;

        if (D3D11.CreateShaderResourceView((void*)KernelForwarderSafe(), (void*)newTex, srvDesc, out var newSrv) != 0)
        {
            D3D11.Release((void*)newTex);
            return "新 SRV 创建失败";
        }

        // 交换!
        kt->D3D11Texture2D = (void*)newTex;
        kt->D3D11ShaderResourceView = newSrv;
        _createdTex.Add(newTex);
        _createdSrv.Add((nint)newSrv);
        _log.Info($"指针交换完成: 新 D3D=0x{newTex:X} 新 SRV=0x{(nint)newSrv:X}(本世代累计创建 {_createdTex.Count} 个)");
        return null;
    }

    private void RestorePointers()
    {
        try
        {
            var kt = SphereArray.GetKernelTexturePtr();
            if (kt != null && _swappedKtPtr == (nint)kt && _origD3D != 0)
            {
                kt->D3D11Texture2D = (void*)_origD3D;
                kt->D3D11ShaderResourceView = (void*)_origSrv;
                _log.Info("已还原 Kernel.Texture 原始指针");
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "还原指针失败");
        }
        ReleaseCreated();
        _swappedKtPtr = 0;
        _origD3D = 0;
        _origSrv = 0;
    }

    private void ReleaseCreated()
    {
        foreach (var t in _createdTex)
            D3D11.Release((void*)t);
        foreach (var s in _createdSrv)
            D3D11.Release((void*)s);
        _createdTex.Clear();
        _createdSrv.Clear();
    }

    private ImportCache GetEncoded(ImportEntry entry, int targetW, int targetH)
    {
        if (_cache.TryGetValue(entry.Slice, out var cached))
        {
            var t = File.GetLastWriteTimeUtc(entry.PngPath);
            if (t == cached.SourceTime && cached.Error.Length == 0 && cached.EncodedMips.Length > 0)
                return cached;
        }

        var c = new ImportCache();
        try
        {
            c.SourceTime = File.GetLastWriteTimeUtc(entry.PngPath);
            var png = File.ReadAllBytes(entry.PngPath);
            if (!PngReader.TryDecode(png, out var rgba, out var w, out var h, out var perr))
            {
                c.Error = perr;
            }
            else
            {
                var rgbaT = ImageScale.Resize(rgba, w, h, targetW, targetH);
                var mips = ImageScale.GenMipChain(rgbaT, targetW, targetH);
                var encoded = new byte[mips.Length][];
                for (var i = 0; i < mips.Length; i++)
                {
                    var mw = Math.Max(1, targetW >> i);
                    var mh = Math.Max(1, targetH >> i);
                    encoded[i] = Bc1Encoder.Encode(mips[i], mw, mh);
                }
                c.EncodedMips = encoded;
            }
        }
        catch (Exception e)
        {
            c.Error = e.GetType().Name + ": " + e.Message;
        }
        _cache[entry.Slice] = c;
        return c;
    }

    private static (byte, byte, byte) HsvToRgb(float h, float s, float v)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - (int)(h * 6);
        var p = v * (1 - s);
        var q = v * (1 - f * s);
        var t = v * (1 - (1 - f) * s);
        return i switch
        {
            0 => ((byte)(v * 255), (byte)(t * 255), (byte)(p * 255)),
            1 => ((byte)(q * 255), (byte)(v * 255), (byte)(p * 255)),
            2 => ((byte)(p * 255), (byte)(v * 255), (byte)(t * 255)),
            3 => ((byte)(p * 255), (byte)(q * 255), (byte)(v * 255)),
            4 => ((byte)(t * 255), (byte)(p * 255), (byte)(v * 255)),
            _ => ((byte)(v * 255), (byte)(p * 255), (byte)(q * 255)),
        };
    }

    private static nint KernelForwarderSafe()
    {
        var dev = Device.Instance();
        return dev == null ? 0 : *(nint*)((byte*)dev + 0xE0AA8);
    }

    private static nint KernelContextSafe()
    {
        var dev = Device.Instance();
        return dev == null ? 0 : *(nint*)((byte*)dev + 0xE0AB0);
    }

    public void Dispose()
    {
        _framework.Update -= Tick;
        RestorePointers();
    }
}
