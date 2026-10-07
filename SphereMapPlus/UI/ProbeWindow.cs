using Dalamud.Utility;
using SphereMapPlus.Imaging;
using SphereMapPlus.Native;
using SphereMapPlus.Probe;
using SphereMapPlus.Interop;
using SphereMapPlus.Spheremap;

namespace SphereMapPlus.UI;

/// <summary>探针主窗口:读取并展示 CharacterUtility[97] 球面贴图数组的运行时信息,支持切片预览、PNG 导出与导入替换。
/// 默认关闭,登录后才允许读取游戏内存(早期读取会撞上未初始化的 CharacterUtility 导致崩溃)。</summary>
public sealed unsafe class ProbeWindow : IDisposable
{
    private readonly IDalamudPluginInterface _pi;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly SlicePatchService _patch;
    private readonly TextureLibrary _library;
    private readonly SchemeAutoSwitch _autoSwitch;

    private bool _visible;
    private int _faults;
    private bool _loggedFirstRead;

    // 每切片 SRV 缓存(绑定到特定底层 D3D 纹理;纹理更换时重建)
    private nint[] _sliceSrvs = [];
    private nint _srvSourceTexture;
    private int _srvCount;

    private int _exportSlice;
    private string _status = string.Empty;
    private string _exportDir = string.Empty;
    private bool _autoPreview;
    private string _importPath = string.Empty;


    public ProbeWindow(IDalamudPluginInterface pi, IPluginLog log, IClientState clientState, SlicePatchService patch, TextureLibrary library, SchemeAutoSwitch autoSwitch)
    {
        _pi = pi;
        _log = log;
        _clientState = clientState;
        _patch = patch;
        _library = library;
        _autoSwitch = autoSwitch;
    }

    public bool Visible
    {
        get => _visible;
        set => _visible = value;
    }

    public void Toggle()
    {
        _visible = !_visible;
        if (_visible)
            _log.Info("探针窗口打开");
    }

    public void Draw()
    {
        if (_faults < 3)
        {
            _patch.RenderThreadPump();   // 渲染线程:执行排队的自动应用(D3D 操作统一在此线程)
            _autoSwitch.RenderThreadPump(); // 渲染线程:方案自动切换评估
        }
        if (!_visible || _faults >= 3)
            return;

        try
        {
            DrawInner();
        }
        catch (Exception e)
        {
            _faults++;
            _log.Error(e, $"探针绘制失败({_faults}/3)");
        }
    }

    private void DrawInner()
    {
        ImGui.SetNextWindowSizeConstraints(new Vector2(760, 420), new Vector2(1400, 1800));
        if (!ImGui.Begin("SphereMapPlus", ref _visible))
        {
            ImGui.End();
            return;
        }

        if (!_clientState.IsLoggedIn)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "请登录进入游戏后再查看(避免在资源初始化阶段读取内存)");
            ImGui.End();
            return;
        }

        var ok = SphereArray.TryGetInstance(out _);
        var info = ok ? SphereArray.ReadInfo() : default;
        if (info.Loaded && !_loggedFirstRead)
        {
            _loggedFirstRead = true;
            _log.Info($"球面数组就绪: {info.FileName} 切片={info.TexArraySize} 文件头ArraySize={info.HeaderArraySize} " +
                      $"尺寸={info.TexActualWidth}x{info.TexActualHeight} Mip={info.MipCount} " +
                      $"游戏格式=0x{info.TexFormat:X} DXGI=0x{info.DxgiFormat:X} 标志=0x{info.HeaderFlags:X8}");
        }

        if (ImGui.BeginTabBar("##smp_tabs"))
        {
            if (ImGui.BeginTabItem("导入工作台"))
            {
                DrawInfoSection(info);
                ImGui.Separator();
                DrawPreviewSection(info);
                ImGui.Separator();
                DrawExportSection(info);
                ImGui.Separator();
                DrawImportSection(info);
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("贴图仓库"))
            {
                DrawLibraryTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("方案与联动"))
            {
                DrawSchemesTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if (ImGui.CollapsingHeader("全部槽位(CharacterUtility 资源表)"))
        {
            var rows = SphereArray.DumpAllSlots();
            ImGui.BeginChild("slots", new Vector2(0, 300));
            foreach (var (slot, name, isTex) in rows)
            {
                var color = slot == SphereArray.SphereDArraySlot ? new Vector4(1f, 0.9f, 0.3f, 1f)
                    : isTex ? new Vector4(0.7f, 0.9f, 1f, 1f)
                    : new Vector4(0.8f, 0.8f, 0.8f, 1f);
                ImGui.TextColored(color, $"[{slot,3}] {(isTex ? "TEX" : "   ")} {name}");
            }
            ImGui.EndChild();
        }

        ImGui.End();
    }

    // ---------- 贴图仓库 ----------
    private string _libNameBuf = string.Empty;
    private readonly Dictionary<string, (nint Srv, int W, int H)> _thumbs = new();

    private nint GetThumb(string pngPath)
    {
        if (_thumbs.TryGetValue(pngPath, out var t))
            return t.Srv;
        try
        {
            if (!File.Exists(pngPath))
                return 0;
            if (!PngReader.TryDecode(File.ReadAllBytes(pngPath), out var rgba, out var w, out var h, out _))
                return 0;
            var small = ImageScale.Resize(rgba, w, h, 64, 64);
            for (var i = 0; i < small.Length; i += 4)
                (small[i], small[i + 2]) = (small[i + 2], small[i]); // RGBA→BGRA
            var desc = new D3D11.Texture2DDesc
            {
                Width = 64, Height = 64, MipLevels = 1, ArraySize = 1,
                Format = 87,
                SampleDesc = new D3D11.SampleDesc { Count = 1, Quality = 0 },
                Usage = 0, BindFlags = 0x8,
            };
            nint srv = 0;
            unsafe
            {
                fixed (byte* p = small)
                {
                    var sd = new D3D11.SubresourceData { Data = p, Pitch = 64 * 4, SlicePitch = 64 * 64 * 4 };
                    if (D3D11.CreateTexture2D((void*)SliceReader.GameDevice(), desc, &sd, out var tex) == 0 && tex != null)
                    {
                        var sd2 = new D3D11.SrvDesc { Format = 87, ViewDimension = 4 }; // TEXTURE2D
                        // 单纹理 2D 视图:{MostDetailedMip, MipLevels} 8 字节
                        unsafe
                        {
                            var u = (uint*)&sd2.Texture2DArray;
                            u[0] = 0; u[1] = 1; // mip0, 1 级
                        }
                        if (D3D11.CreateShaderResourceView((void*)SliceReader.GameDevice(), tex, sd2, out var s) == 0)
                            srv = (nint)s;
                        D3D11.Release(tex);
                    }
                }
            }
            _thumbs[pngPath] = (srv, 64, 64);
            return srv;
        }
        catch
        {
            _thumbs[pngPath] = (0, 0, 0);
            return 0;
        }
    }

    private void DrawLibraryTab()
    {
        ImGui.TextWrapped("仓库中的贴图会被复制到插件目录统一托管,方案与导入引用仓库项,原文件丢失不影响使用。");
        ImGui.SetNextItemWidth(-160);
        var nameBuf = _libNameBuf;
        ImGui.InputText("##libName", ref nameBuf, 128);
        _libNameBuf = nameBuf;
        ImGui.SameLine();
        if (ImGui.Button("浏览并入库..."))
        {
            var picked = FileDialog.PickPng();
            if (picked != null)
            {
                var err = _library.Import(picked, _libNameBuf);
                _libNameBuf = string.Empty;
                if (err != null)
                    _log.Warning($"入库失败: {err}");
            }
        }

        ImGui.Separator();
        ImGui.BeginChild("libList", new Vector2(0, -1));
        foreach (var it in _library.Items.ToList())
        {
            var thumb = GetThumb(_library.PngPath(it));
            if (thumb != 0)
            {
                ImGui.Image(new ImTextureID(thumb), new Vector2(48, 48));
                ImGui.SameLine();
            }
            ImGui.BeginGroup();
            ImGui.Text($"{it.Name}");
            ImGui.TextDisabled($"{it.Id}  {it.Added:yyyy-MM-dd}");
            ImGui.EndGroup();
            ImGui.SameLine();
            var rnBuf = it.Name;
            ImGui.SetNextItemWidth(160);
            if (ImGui.InputText($"##rn{it.Id}", ref rnBuf, 128) && rnBuf != it.Name && nameBuf.Length > 0)
                _library.Rename(it.Id, nameBuf);
            ImGui.SameLine();
            if (ImGui.Button($"删除##del{it.Id}"))
                _library.Delete(it.Id);
            ImGui.Separator();
        }
        ImGui.EndChild();
    }


    // ---------- 方案与联动 ----------
    private string? _selectedSchemeId;
    private int _schemeSliceNo;
    private int _schemeLibIndex;
    private int _schemeModIndex = -1;
    private string _manualModBuf = string.Empty;

    private SchemeAutoSwitch.Scheme? SelectedScheme
    {
        get
        {
            if (_selectedSchemeId == null && _autoSwitch.Schemes.Count > 0)
                _selectedSchemeId = _autoSwitch.Schemes[0].Id;
            return _autoSwitch.Schemes.FirstOrDefault(s => s.Id == _selectedSchemeId);
        }
    }

    private void DrawSchemesTab()
    {
        var auto = _autoSwitch.AutoSwitchEnabled;
        if (ImGui.Checkbox("自动切换(按方案优先级检测 Penumbra mod 命中)", ref auto))
        {
            _autoSwitch.AutoSwitchEnabled = auto;
            _autoSwitch.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("立即重新检测"))
            _autoSwitch.Evaluate();
        ImGui.TextColored(new Vector4(0.6f, 0.9f, 1f, 1f),
            $"当前生效:{(string.IsNullOrEmpty(_autoSwitch.CurrentSchemeName) ? "(无)" : _autoSwitch.CurrentSchemeName)}");
        ImGui.TextDisabled(_autoSwitch.LastEval);

        ImGui.Separator();
        if (!ImGui.BeginTable("##schemes", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("方案", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("上移", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("下移", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("应用", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("删除", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();
        foreach (var s in _autoSwitch.Schemes.ToList())
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            var selected = s.Id == _selectedSchemeId;
            if (selected)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.9f, 0.4f, 1f));
            if (ImGui.Selectable($"{s.Name} ({s.Bindings.Count} mod / {s.Slices.Count} 切片)##sel{s.Id}", selected))
                _selectedSchemeId = s.Id;
            if (selected)
                ImGui.PopStyleColor();
            ImGui.TableSetColumnIndex(1);
            if (ImGui.Button($"上移##up{s.Id}"))
            {
                _log.Info($"[按钮] 上移 {s.Name}");
                _autoSwitch.MoveScheme(s.Id, -1);
            }
            ImGui.TableSetColumnIndex(2);
            if (ImGui.Button($"下移##dn{s.Id}"))
            {
                _log.Info($"[按钮] 下移 {s.Name}");
                _autoSwitch.MoveScheme(s.Id, 1);
            }
            ImGui.TableSetColumnIndex(3);
            if (ImGui.Button($"应用##ap{s.Id}"))
            {
                _log.Info($"[按钮] 应用 {s.Name}");
                ApplySchemeNow(s);
            }
            ImGui.TableSetColumnIndex(4);
            if (ImGui.Button($"删除##del{s.Id}"))
            {
                _log.Info($"[按钮] 删除 {s.Name}");
                _autoSwitch.DeleteScheme(s.Id);
                if (_selectedSchemeId == s.Id)
                    _selectedSchemeId = null;
            }
        }
        ImGui.EndTable();

        if (ImGui.Button("+ 新建方案"))
        {
            var s = _autoSwitch.AddScheme($"方案 {_autoSwitch.Schemes.Count + 1}");
            _selectedSchemeId = s.Id;
        }
        ImGui.SameLine();
        if (ImGui.Button("导入方案(.smpk)"))
        {
            var picked = FileDialog.PickSchemePackage();
            if (picked != null)
            {
                var err = _autoSwitch.ImportScheme(picked);
                if (err != null)
                    _log.Warning($"导入方案失败: {err}");
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("导出当前方案(.smpk)") && SelectedScheme is { } exportScheme)
        {
            var target = FileDialog.SaveFile(
                "SphereMapPlus 方案包\0*.smpk\0\0",
                exportScheme.Name, "导出方案");
            if (target != null)
            {
                var err = _autoSwitch.ExportScheme(exportScheme, target);
                if (err != null)
                    _log.Warning($"导出方案失败: {err}");
            }
        }

        var sel = SelectedScheme;
        if (sel == null)
            return;

        ImGui.Separator();
        ImGui.Text($"编辑方案:{sel.Name}");
        var nameBuf = sel.Name;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputText("方案名称##schemeName", ref nameBuf, 64) && nameBuf.Length > 0 && nameBuf != sel.Name)
        {
            sel.Name = nameBuf;
            _autoSwitch.Save();
        }

        // --- 绑定 Penumbra mod ---
        ImGui.Spacing();
        ImGui.Text("绑定的 Penumbra mod(当前穿着其改动的装备时本方案生效):");
        foreach (var b in sel.Bindings.ToList())
        {
            ImGui.BulletText(b);
            ImGui.SameLine();
            if (ImGui.Button($"移除绑定##rmb{b.GetHashCode()}"))
            {
                sel.Bindings.Remove(b);
                _autoSwitch.Save();
            }
        }

        var mods = _autoSwitch.GetInstalledMods();
        if (mods.Count > 0)
        {
            if (_schemeModIndex < 0 || _schemeModIndex >= mods.Count)
                _schemeModIndex = 0;
            ImGui.SetNextItemWidth(320);
            if (ImGui.Combo("从列表选择 mod##modCombo", ref _schemeModIndex, mods.ToArray(), mods.Count))
            {
            }
            ImGui.SameLine();
            if (ImGui.Button("添加绑定##addMod") && !sel.Bindings.Contains(mods[_schemeModIndex]))
            {
                sel.Bindings.Add(mods[_schemeModIndex]);
                _autoSwitch.Save();
            }
        }
        else
        {
            ImGui.TextDisabled("(未能获取 Penumbra mod 列表,可用下方手动输入)");
        }
        var bindBuf = _manualModBuf;
        ImGui.SetNextItemWidth(320);
        ImGui.InputText("或手动输入 mod 名称##bind", ref bindBuf, 128);
        _manualModBuf = bindBuf;
        ImGui.SameLine();
        if (ImGui.Button("添加绑定##addManual") && bindBuf.Length > 0 && !sel.Bindings.Contains(bindBuf))
        {
            sel.Bindings.Add(bindBuf);
            _autoSwitch.Save();
        }

        // --- 切片替换 ---
        ImGui.Spacing();
        ImGui.Text("切片替换(方案生效时同时替换以下切片):");
        foreach (var t in sel.Slices.ToList())
        {
            var lib = _library.Items.FirstOrDefault(i => i.Id == t.LibraryId);
            ImGui.BulletText($"切片 {t.Slice} ← {lib?.Name ?? "(缺失)"}");
            ImGui.SameLine();
            if (ImGui.Button($"移除##rms{t.Slice.GetHashCode()}"))
            {
                sel.Slices.Remove(t);
                _autoSwitch.Save();
            }
        }
        ImGui.SetNextItemWidth(100);
        ImGui.InputInt("切片号##schemeSlice", ref _schemeSliceNo);
        var libNames = _library.Items.Select(i => i.Name).ToList();
        if (libNames.Count > 0)
        {
            _schemeLibIndex = Math.Clamp(_schemeLibIndex, 0, libNames.Count - 1);
            ImGui.SetNextItemWidth(220);
            if (ImGui.Combo("仓库贴图##schemeLib", ref _schemeLibIndex, libNames.ToArray(), libNames.Count))
            {
            }
            ImGui.SameLine();
            var picked = _library.Items[_schemeLibIndex];
            var thumb = GetThumb(_library.PngPath(picked));
            if (thumb != 0)
            {
                ImGui.Image(new ImTextureID(thumb), new Vector2(48, 48));
                ImGui.SameLine();
            }
            ImGui.BeginGroup();
            ImGui.Text(picked.Name);
            ImGui.TextDisabled($"将替换到切片 {_schemeSliceNo}");
            ImGui.EndGroup();
            ImGui.SameLine();
            if (ImGui.Button("添加切片替换##addSlice") && _schemeSliceNo >= 0 && _schemeSliceNo < 32)
            {
                if (sel.Slices.All(t => t.Slice != _schemeSliceNo))
                {
                    sel.Slices.Add(new SchemeAutoSwitch.SchemeSlice { Slice = _schemeSliceNo, LibraryId = picked.Id });
                    _autoSwitch.Save();
                }
            }
        }
        else
        {
            ImGui.TextDisabled("(仓库为空,请先在「贴图仓库」导入)");
        }

        // --- 选中方案的操作(与表格按钮等效的兜底) ---
        ImGui.Spacing();
        if (ImGui.Button($"应用当前编辑的方案({sel.Name})##editApply"))
        {
            _log.Info($"[按钮] 编辑区应用 {sel.Name}");
            ApplySchemeNow(sel);
        }
        ImGui.SameLine();
        if (ImGui.Button($"删除当前方案({sel.Name})##editDel"))
        {
            _log.Info($"[按钮] 编辑区删除 {sel.Name}");
            _autoSwitch.DeleteScheme(sel.Id);
            _selectedSchemeId = null;
        }
    }

    private void ApplySchemeNow(SchemeAutoSwitch.Scheme s)
    {
        var slices = s.Slices
            .Select(t => (t.Slice, _library.Items.FirstOrDefault(i => i.Id == t.LibraryId)))
            .Where(t => t.Item2 != null)
            .Select(t => (t.Item1, _library.PngPath(t.Item2!)));
        var err = _patch.ApplyScheme(s.Name, slices);
        if (err == null)
            _autoSwitch.CurrentSchemeName = s.Name;
    }

    // ---------- 导入工作台各分区 ----------

    private void DrawInfoSection(in SphereArray.SphereInfo info)
    {
        if (!info.Loaded)
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f),
                $"槽位 [{SphereArray.SphereDArraySlot}] 尚无有效的球面数组资源(文件名未匹配或资源未加载)");
            return;
        }

        ImGui.Text($"文件名: {info.FileName}");
        ImGui.Text($"Header: 类型标志=0x{info.HeaderFlags:X8}{(info.Is2DArray ? "  [Texture2DArray]" : "  [非数组类型]")}  格式=0x{info.HeaderFormat:X}");
        ImGui.Text($"Header 尺寸: {info.Width}x{info.Height}  Mip={info.MipCount}  ArraySize(文件)={info.HeaderArraySize}");
        if (info.D3DTexture != 0)
        {
            ImGui.Text($"Kernel.Texture: ArraySize={info.TexArraySize}  实际尺寸={info.TexActualWidth}x{info.TexActualHeight}  格式=0x{info.TexFormat:X} → DXGI 0x{info.DxgiFormat:X}");
            ImGui.Text($"D3D11Texture2D = 0x{info.D3DTexture:X}   D3D11ShaderResourceView = 0x{info.D3DSrv:X}");
            ImGui.TextColored(new Vector4(0.5f, 1f, 0.7f, 1f),
                $"运行时切片数 = {info.TexArraySize}(API 侧应以此为准)");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "GPU 纹理尚未创建(Texture == null)");
        }
    }

    private void DrawPreviewSection(in SphereArray.SphereInfo info)
    {
        ImGui.Checkbox("启用切片预览(实验性:需要创建 D3D 视图,若崩溃请保持关闭)", ref _autoPreview);
        if (!_autoPreview || !info.Loaded || info.D3DTexture == 0)
        {
            if (_srvCount <= 0 && !_autoPreview)
                ImGui.TextDisabled("(预览已关闭)");
            return;
        }

        EnsureSrvs(info);

        if (_srvCount <= 0)
        {
            ImGui.Text("(切片预览不可用)");
            return;
        }

        var size = new Vector2(64, 64) * 1.5f;
        const int perRow = 8;
        ImGui.Text($"切片预览(点击选择导出/导入目标,当前: {_exportSlice}):");
        for (var i = 0; i < _srvCount; i++)
        {
            if (i % perRow != 0)
                ImGui.SameLine();
            ImGui.BeginGroup();
            if (_sliceSrvs[i] != 0)
                ImGui.Image(new ImTextureID(_sliceSrvs[i]), size);
            else
                ImGui.Dummy(size);
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _exportSlice = i;
            var label = i.ToString();
            var selected = i == _exportSlice;
            if (selected)
                ImGui.TextColored(new Vector4(1f, 0.9f, 0.3f, 1f), label);
            else
                ImGui.TextDisabled(label);
            ImGui.EndGroup();
        }
    }

    private void DrawExportSection(in SphereArray.SphereInfo info)
    {
        if (!info.Loaded || info.D3DTexture == 0)
            return;

        ImGui.SetNextItemWidth(120);
        ImGui.DragInt("导出切片序号", ref _exportSlice, 0.2f, 0, Math.Max(0, info.TexArraySize - 1));
        ImGui.SameLine();
        string msg = null;
        if (ImGui.Button("导出单张 PNG"))
            Export(info, single: true, out msg);
        if (!string.IsNullOrEmpty(msg))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), msg);
        }

        string msg2 = null;
        if (ImGui.Button($"导出全部 {info.TexArraySize} 张切片 PNG"))
            Export(info, single: false, out msg2);
        if (!string.IsNullOrEmpty(msg2))
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), msg2);
        }

        if (!string.IsNullOrEmpty(_status))
            ImGui.TextWrapped(_status);
        if (!string.IsNullOrEmpty(_exportDir) && ImGui.Button("打开导出目录"))
            Util.OpenLink(_exportDir);
    }

    private void Export(in SphereArray.SphereInfo info, bool single, out string error)
    {
        error = string.Empty;
        try
        {
            _exportDir = Path.Combine(_pi.GetPluginConfigDirectory(), "sphere_slices");
            Directory.CreateDirectory(_exportDir);

            var from = single ? Math.Clamp(_exportSlice, 0, info.TexArraySize - 1) : 0;
            var to = single ? from + 1 : info.TexArraySize;
            var okCount = 0;
            var failures = new List<string>();
            for (var i = from; i < to; i++)
            {
                if (_sliceSrvs.Length > i && _sliceSrvs[i] == 0)
                {
                    failures.Add($"切片 {i}: SRV 不可用");
                    continue;
                }
                if (!SliceReader.TryReadSlice(info.D3DTexture, i, info.TexArraySize, info.MipCount, info.TexActualWidth, info.TexActualHeight,
                        info.DxgiFormat, out var pixels, out var err))
                {
                    failures.Add($"切片 {i}: {err}");
                    continue;
                }
                var path = Path.Combine(_exportDir, $"sphere_slice_{i:D2}.png");
                PngWriter.Save(path, info.TexActualWidth, info.TexActualHeight, pixels);
                okCount++;
            }

            _status = $"导出完成: {okCount}/{to - from} 张 → {_exportDir}"
                + (failures.Count > 0 ? "\n失败: " + string.Join("; ", failures) : string.Empty);
            _log.Info(_status);
        }
        catch (Exception e)
        {
            error = "导出异常";
            _log.Error(e, "切片导出失败");
        }
    }

    private void DrawImportSection(in SphereArray.SphereInfo info)
    {
        if (!ImGui.CollapsingHeader("直接导入(替换现有切片内容)"))
            return;

        ImGui.TextWrapped("在预览网格点击选择目标切片,选择 PNG 后导入:自动缩放至 256x256、生成 9 级 mip、BC1 压缩(与原生一致)后写回。只替换切片内容,不改任何指针,可随时还原。");
        ImGui.TextColored(new Vector4(1f, 0.75f, 0.3f, 1f),
            "请用「方案」和「仓库」管理,这里仅作为快速试验。");

        ImGui.SetNextItemWidth(-160);
        var buf = _importPath;
        ImGui.InputText("##importPath", ref buf, 1024);
        _importPath = buf;
        ImGui.SameLine();
        if (ImGui.Button("浏览..."))
        {
            var picked = FileDialog.PickPng();
            if (picked != null)
            {
                _importPath = picked;
                _libNameBuf = Path.GetFileNameWithoutExtension(picked);
            }
        }

        if (info.Loaded && info.D3DTexture != 0)
        {
            if (ImGui.Button($"导入并替换切片 {_exportSlice}"))
            {
                if (!File.Exists(_importPath))
                    _status = "PNG 文件不存在";
                else
                {
                    var err = _patch.Import(_exportSlice, _importPath);
                    if (err != null)
                        _log.Warning($"导入失败: {err}");
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("顺带入库##libQuick"))
            {
                if (File.Exists(_importPath))
                {
                    var err = _library.Import(_importPath, _libNameBuf);
                    _status = err == null ? $"已入仓库:{_libNameBuf}" : "入库失败: " + err;
                }
                else
                    _status = "PNG 文件不存在";
            }
            ImGui.SameLine();
            if (ImGui.Button("全部还原"))
            {
                var err = _patch.RestoreAll();
                if (err != null)
                    _log.Warning($"全部还原失败: {err}");
            }
            ImGui.SameLine();
            if (ImGui.Button("测试高亮(全部切片变纯色)"))
            {
                var err = _patch.ApplyTestPattern(info);
                _patch.LastStatus = err == null
                    ? "已写入测试高亮:观察角色/装备渲染是否出现彩色——完全无变化说明游戏采样另有其物;看完点「全部还原」"
                    : err;
                if (err != null)
                    _log.Warning($"测试高亮失败: {err}");
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "球面数组未就绪,导入按钮不可用(保存的导入会就绪后自动应用)");
        }

        if (!string.IsNullOrEmpty(_patch.LastStatus))
            ImGui.TextWrapped(_patch.LastStatus);
    }

    private void EnsureSrvs(in SphereArray.SphereInfo info)
    {
        if (_srvSourceTexture == info.D3DTexture && _srvCount == info.TexArraySize)
            return;

        ReleaseSrvs();

        var gameSrv = (void*)info.D3DSrv;
        if (gameSrv == null || !Memory.IsReadable(info.D3DSrv, 8))
        {
            _status = "游戏 SRV 指针无效,跳过预览";
            return;
        }
        if (info.TexArraySize is < 1 or > 256)
        {
            _status = $"切片数 {info.TexArraySize} 不合理,跳过预览";
            return;
        }

        _log.Info($"SRV 派生开始: 游戏SRV=0x{info.D3DSrv:X} D3D贴图=0x{info.D3DTexture:X} 切片数={info.TexArraySize}");
        var device = D3D11.GetDevice(gameSrv);
        if (device == null)
        {
            _status = "无法从游戏 SRV 获取 D3D 设备,跳过预览";
            return;
        }

        D3D11.GetSrvDesc(gameSrv, out var baseDesc);
        if (baseDesc.ViewDimension != D3D11.SrvDimensionTexture2DArray)
        {
            _status = $"游戏 SRV 维度异常({baseDesc.ViewDimension}),跳过预览";
            return;
        }

        var resource = D3D11.GetResource(gameSrv);
        if (resource == null)
        {
            _status = "无法获取 SRV 背后的 D3D 资源,跳过预览";
            return;
        }

        _srvSourceTexture = info.D3DTexture;
        _srvCount = info.TexArraySize;
        _sliceSrvs = new nint[_srvCount];
        var created = 0;
        for (var i = 0; i < _srvCount; i++)
        {
            var d = baseDesc;
            d.Texture2DArray.FirstArraySlice = (uint)i;
            d.Texture2DArray.ArraySize = 1;
            var hr = D3D11.CreateShaderResourceView(device, resource, d, out var srv);
            _sliceSrvs[i] = hr == 0 ? (nint)srv : 0;
            if (hr == 0)
                created++;
        }
        D3D11.Release(resource);
        _log.Info($"切片 SRV 创建完成: {created}/{_srvCount}");
    }

    private void ReleaseSrvs()
    {
        foreach (var srv in _sliceSrvs)
        {
            if (srv != 0)
                D3D11.Release((void*)srv);
        }
        _sliceSrvs = [];
        _srvCount = 0;
        _srvSourceTexture = 0;
    }

    public void Dispose()
    {
        ReleaseSrvs();
        foreach (var t in _thumbs.Values)
        {
            if (t.Srv != 0)
                D3D11.Release((void*)t.Srv);
        }
        _thumbs.Clear();
    }
}
