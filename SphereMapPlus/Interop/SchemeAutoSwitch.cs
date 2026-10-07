using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using SphereMapPlus.Spheremap;

namespace SphereMapPlus.Interop;

/// <summary>
/// 贴图方案 + 联动自动切换。
/// 方案 = 命名的一组 {切片 ← 仓库贴图},绑定一个或多个 Penumbra mod;
/// 检测:通过 Penumbra 的 GetPlayerResourcePaths 获取「玩家当前实际生效的 mod → 资源路径」映射,
/// 方案绑定的 mod 出现在其中即命中,按方案列表顺序(优先级)应用最高者。
/// 触发:Glamourer StateChanged / Penumbra GameObjectRedrawn;检测在渲染线程(RenderThreadPump)执行。
/// </summary>
public sealed class SchemeAutoSwitch : IDisposable
{
    public sealed class SchemeSlice
    {
        public int Slice { get; set; }
        public string LibraryId { get; set; } = "";
    }

    public sealed class Scheme
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string> Bindings { get; set; } = [];
        public List<SchemeSlice> Slices { get; set; } = [];
    }

    private readonly IPluginLog _log;
    private readonly IDalamudPluginInterface _pi;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objects;
    private readonly SlicePatchService _patch;
    private readonly TextureLibrary _library;
    private readonly string _schemesPath;

    public List<Scheme> Schemes { get; private set; } = [];
    public bool AutoSwitchEnabled { get; set; } = true;
    public string CurrentSchemeName { get; internal set; } = "";
    public string LastEval { get; set; } = "尚未检测";

    private volatile bool _pendingEval;
    private readonly object _evalLock = new();

    // Penumbra 已安装 mod 名单缓存(用于下拉选择)
    private List<string> _installedMods = [];
    private DateTime _installedModsTime;

    public SchemeAutoSwitch(IDalamudPluginInterface pi, IPluginLog log, IClientState clientState,
        IObjectTable objects, SlicePatchService patch, TextureLibrary library)
    {
        _pi = pi;
        _log = log;
        _clientState = clientState;
        _objects = objects;
        _patch = patch;
        _library = library;
        _schemesPath = Path.Combine(pi.GetPluginConfigDirectory(), "schemes.json");
        Load();
        SubscribeIpc();
    }

    // ---------- 持久化 ----------
    private void Load()
    {
        try
        {
            if (!File.Exists(_schemesPath))
                return;
            var json = File.ReadAllText(_schemesPath);
            var doc = JsonDocument.Parse(json);
            Schemes.Clear();
            if (doc.RootElement.TryGetProperty("schemes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var s = new Scheme
                    {
                        Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    };
                    if (e.TryGetProperty("bindings", out var bs) && bs.ValueKind == JsonValueKind.Array)
                        foreach (var b in bs.EnumerateArray())
                            s.Bindings.Add(b.GetString() ?? "");
                    if (e.TryGetProperty("slices", out var ss) && ss.ValueKind == JsonValueKind.Array)
                        foreach (var t in ss.EnumerateArray())
                            s.Slices.Add(new SchemeSlice
                            {
                                Slice = t.TryGetProperty("slice", out var sl) ? sl.GetInt32() : 0,
                                LibraryId = t.TryGetProperty("libraryId", out var lid) ? lid.GetString() ?? "" : "",
                            });
                    Schemes.Add(s);
                }
            }
            if (doc.RootElement.TryGetProperty("autoSwitch", out var asEl))
                AutoSwitchEnabled = asEl.GetBoolean();
            _log.Info($"已加载贴图方案:{Schemes.Count} 个");
        }
        catch (Exception e)
        {
            _log.Error(e, "读取贴图方案失败");
        }
    }

    public void Save()
    {
        try
        {
            var obj = new
            {
                autoSwitch = AutoSwitchEnabled,
                schemes = Schemes.Select(s => new
                {
                    id = s.Id,
                    name = s.Name,
                    bindings = s.Bindings,
                    slices = s.Slices.Select(t => new { slice = t.Slice, libraryId = t.LibraryId }).ToList(),
                }).ToList(),
            };
            File.WriteAllText(_schemesPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            _log.Error(e, "保存贴图方案失败");
        }
    }

    // ---------- IPC 订阅 ----------
    private void SubscribeIpc()
    {
        try
        {
            // 注意:Glamourer 上游的 Label 常量实际为 "Penumbra.StateChanged.V2"(复制粘贴产物),订阅须用真实通道名
            _pi.GetIpcSubscriber<nint, nint>("Penumbra.StateChanged.V2").Subscribe(OnGlamourerStateChanged);
        }
        catch (Exception e)
        {
            _log.Warning($"订阅 Glamourer StateChanged 失败(未安装或版本过旧): {e.Message}");
        }
        try
        {
            _pi.GetIpcSubscriber<nint, int, nint>("Penumbra.GameObjectRedrawn").Subscribe(OnGameObjectRedrawn);
        }
        catch (Exception e)
        {
            _log.Warning($"订阅 Penumbra GameObjectRedrawn 失败: {e.Message}");
        }
    }

    private void OnGlamourerStateChanged(nint objectAddress)
    {
        try
        {
            var p = PlayerObject();
            if (p == null || p.Address != objectAddress)
                return;
            lock (_evalLock)
                _pendingEval = true;
        }
        catch
        {
            // ignore
        }
    }

    private void OnGameObjectRedrawn(nint objectPointer, int objectIndex)
    {
        try
        {
            var p = PlayerObject();
            if (p == null || p.ObjectIndex != objectIndex)
                return;
            lock (_evalLock)
                _pendingEval = true;
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>由 UI 绘制回调(渲染线程)每帧调用。</summary>
    public void RenderThreadPump()
    {
        bool eval;
        lock (_evalLock)
        {
            eval = _pendingEval;
            _pendingEval = false;
        }
        if (eval)
        {
            try
            {
                Evaluate();
            }
            catch (Exception e)
            {
                _log.Error(e, "方案自动切换评估异常");
            }
        }
    }

    // ---------- 检测 ----------
    private IGameObject? PlayerObject()
    {
        try
        {
            return _clientState.IsLoggedIn ? _objects.FirstOrDefault(o => o.ObjectIndex == 0) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>最近一次资源树采样中的原始路径样本(诊断用)。</summary>
    public string LastRawSample { get; set; } = "(未采样)";

    /// <summary>获取玩家当前实际生效的 mod 名集合。
    /// GetPlayerResourcePaths 返回「mod 文件磁盘路径 → 可能的游戏路径」,键是磁盘路径而非 mod 名,
    /// 因此用 GetModList 的「显示名 → 目录名」映射,按路径段把文件归到所属 mod。</summary>
    public HashSet<string> GetActiveMods()
    {
        var result = new HashSet<string>();
        var sub = _pi.GetIpcSubscriber<Dictionary<ushort, Dictionary<string, HashSet<string>>>>("Penumbra.GetPlayerResourcePaths.V5");
        var byCollection = sub.InvokeFunc();

        var name2dir = new List<(string Name, string Dir)>();
        try
        {
            var listSub = _pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
            foreach (var (name, dir) in listSub.InvokeFunc())
                name2dir.Add((name, dir));
        }
        catch (Exception e)
        {
            _log.Warning($"获取 Penumbra mod 列表失败: {e.Message}");
        }

        var sample = new List<string>();
        foreach (var (_, byPath) in byCollection)
        {
            foreach (var fullPath in byPath.Keys)
            {
                if (sample.Count < 3)
                    sample.Add(fullPath);
                // 归一到显示名
                foreach (var (name, dir) in name2dir)
                {
                    if (dir.Length > 0 && ContainsSegment(fullPath, dir))
                    {
                        result.Add(name);
                        break;
                    }
                }
            }
        }
        LastRawSample = sample.Count > 0 ? string.Join(" | ", sample) : "(空)";
        return result;
    }

    /// <summary>路径中是否含有路径段 segment(忽略大小写,兼容 / 与 \)。</summary>
    private static bool ContainsSegment(string path, string segment)
    {
        if (string.IsNullOrEmpty(path) || segment.Length == 0)
            return false;
        var seg = "/" + segment.Replace('\\', '/') + "/";
        var norm = "/" + path.Replace('\\', '/') + "/";
        return norm.IndexOf(seg, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public void Evaluate()
    {
        if (!AutoSwitchEnabled || Schemes.Count == 0)
            return;
        if (PlayerObject() == null || !_clientState.IsLoggedIn)
            return;

        HashSet<string> activeMods;
        try
        {
            activeMods = GetActiveMods();
        }
        catch (Exception e)
        {
            LastEval = $"Penumbra 资源树获取失败: {e.Message}";
            return;
        }
        if (activeMods.Count == 0)
        {
            LastEval = "玩家当前没有生效的 mod 资源";
            return;
        }

        foreach (var scheme in Schemes)
        {
            if (scheme.Bindings.Count == 0 || scheme.Slices.Count == 0)
                continue;
            var hitMods = scheme.Bindings.Where(activeMods.Contains).ToList();
            if (hitMods.Count == 0)
                continue;

            if (scheme.Name == CurrentSchemeName)
            {
                LastEval = $"当前方案 {scheme.Name} 仍然命中({string.Join(", ", hitMods)})";
                return;
            }
            var slices = scheme.Slices
                .Select(t => (t.Slice, _library.Items.FirstOrDefault(i => i.Id == t.LibraryId)))
                .Where(t => t.Item2 != null)
                .Select(t => (t.Item1, _library.PngPath(t.Item2!)))
                .ToList();
            if (slices.Count == 0)
            {
                LastEval = $"方案 {scheme.Name} 命中但切片引用的仓库项缺失";
                return;
            }
            var err = _patch.ApplyScheme(scheme.Name, slices);
            if (err == null)
            {
                CurrentSchemeName = scheme.Name;
                LastEval = $"✔ 自动切换 → {scheme.Name}(命中 mod: {string.Join(", ", hitMods)})";
                _log.Info(LastEval);
            }
            else
            {
                LastEval = $"自动切换到 {scheme.Name} 失败: {err}";
                _log.Warning(LastEval);
            }
            return;
        }
        LastEval = $"无方案命中(生效 mod:{(activeMods.Count > 0 ? string.Join(", ", activeMods.Take(5)) + (activeMods.Count > 5 ? " 等" : "") : "无")};资源样本:{LastRawSample})";
    }

    // ---------- Penumbra 名单 ----------
    /// <summary>已安装 mod 名单(缓存 60 秒)。</summary>
    public List<string> GetInstalledMods()
    {
        if (_installedMods.Count == 0 || (DateTime.Now - _installedModsTime).TotalSeconds > 60)
        {
            try
            {
                var sub = _pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
                var list = sub.InvokeFunc();
                _installedMods = list.Keys.OrderBy(k => k).ToList();
                _installedModsTime = DateTime.Now;
            }
            catch (Exception e)
            {
                _log.Warning($"获取 Penumbra mod 列表失败: {e.Message}");
            }
        }
        return _installedMods;
    }

    // ---------- 方案管理 ----------
    public Scheme AddScheme(string name)
    {
        var s = new Scheme { Id = Guid.NewGuid().ToString("N")[..12], Name = name };
        Schemes.Add(s);
        Save();
        return s;
    }

    public void DeleteScheme(string id)
    {
        var s = Schemes.FirstOrDefault(x => x.Id == id);
        if (s == null)
            return;
        if (CurrentSchemeName == s.Name)
            CurrentSchemeName = "";
        Schemes.Remove(s);
        Save();
    }

    public void MoveScheme(string id, int delta)
    {
        var idx = Schemes.FindIndex(s => s.Id == id);
        var ni = idx + delta;
        if (idx < 0 || ni < 0 || ni >= Schemes.Count)
            return;
        (Schemes[idx], Schemes[ni]) = (Schemes[ni], Schemes[idx]);
        Save();
    }

    // ---------- 方案导出 / 导入 ----------
    /// <summary>把方案打包为 .smpk(ZIP:方案清单 + 全部引用的仓库贴图 + 绑定 mod 名单)。返回错误或 null。</summary>
    public string ExportScheme(Scheme s, string zipPath)
    {
        try
        {
            var missing = s.Slices.Where(t => _library.Items.All(i => i.Id != t.LibraryId)).ToList();
            if (missing.Count > 0)
                return $"切片引用的仓库项缺失({missing.Count} 个)";

            using var fs = new FileStream(zipPath, FileMode.Create);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
            var manifest = new
            {
                version = 1,
                kind = "SphereMapPlusScheme",
                name = s.Name,
                bindings = s.Bindings,
                slices = s.Slices.Select(t =>
                {
                    var lib = _library.Items.First(i => i.Id == t.LibraryId);
                    return new { slice = t.Slice, file = lib.File, sourceName = lib.Name };
                }).ToList(),
            };
            var manifestEntry = zip.CreateEntry("scheme.json");
            using (var w = new StreamWriter(manifestEntry.Open()))
                w.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var lib in s.Slices
                         .Select(t => _library.Items.FirstOrDefault(i => i.Id == t.LibraryId))
                         .OfType<TextureLibrary.LibraryItem>())
            {
                var entry = zip.CreateEntry($"textures/{lib.File}");
                using var w = entry.Open();
                w.Write(File.ReadAllBytes(_library.PngPath(lib)));
            }
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>从 .smpk 导入方案:贴图自动入库(新建仓库项),绑定 mod 名单与切片映射照搬。返回错误或 null。</summary>
    public string ImportScheme(string zipPath)
    {
        try
        {
            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("scheme.json");
            if (manifestEntry == null)
                return "包内缺少 scheme.json";

            string name;
            var bindings = new List<string>();
            var sliceDefs = new List<(int Slice, string File, string SourceName)>();
            using (var r = new StreamReader(manifestEntry.Open()))
            {
                var doc = JsonDocument.Parse(r.ReadToEnd());
                var root = doc.RootElement;
                if (root.TryGetProperty("kind", out var k) && k.GetString() != "SphereMapPlusScheme")
                    return "不是 SphereMapPlus 方案包";
                name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "导入方案";
                if (root.TryGetProperty("bindings", out var bs) && bs.ValueKind == JsonValueKind.Array)
                    foreach (var b in bs.EnumerateArray())
                        bindings.Add(b.GetString() ?? "");
                if (root.TryGetProperty("slices", out var ss) && ss.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in ss.EnumerateArray())
                    {
                        var file = t.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
                        var sourceName = t.TryGetProperty("sourceName", out var sn) ? sn.GetString() ?? "" : "";
                        var slice = t.TryGetProperty("slice", out var sl) ? sl.GetInt32() : 0;
                        sliceDefs.Add((slice, file, sourceName));
                    }
                }
            }
            if (sliceDefs.Count == 0)
                return "包内没有切片定义";

            // 贴图逐个入库,建立 旧文件名 → 新仓库项 映射
            var idMap = new Dictionary<string, string>();
            foreach (var (_, file, sourceName) in sliceDefs)
            {
                var entry = zip.GetEntry($"textures/{file}");
                if (entry == null)
                    return $"包内缺少贴图 {file}";
                var tmp = Path.Combine(Path.GetTempPath(), "smp_import_" + Guid.NewGuid().ToString("N")[..8] + ".png");
                using (var outFs = File.Create(tmp))
                    entry.Open().CopyTo(outFs);
                var importName = string.IsNullOrWhiteSpace(sourceName)
                    ? Path.GetFileNameWithoutExtension(file)
                    : sourceName;
                var impErr = _library.Import(tmp, importName);
                File.Delete(tmp);
                if (impErr != null)
                    return $"贴图 {file} 入库失败: {impErr}";
                var newItem = _library.Items.LastOrDefault(i => i.Name == importName)
                              ?? throw new InvalidOperationException($"贴图 {file} 入库后未能定位");
                idMap[file] = newItem.Id;
            }

            if (Schemes.Any(x => x.Name == name))
                name += "(导入)";
            var scheme = new Scheme
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Name = name,
                Bindings = bindings,
                Slices = sliceDefs.Select(t => new SchemeSlice { Slice = t.Slice, LibraryId = idMap[t.File] }).ToList(),
            };
            Schemes.Add(scheme);
            Save();
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    public void Dispose()
    {
        try
        {
            _pi.GetIpcSubscriber<nint, nint>("Penumbra.StateChanged.V2").Unsubscribe(OnGlamourerStateChanged);
        }
        catch { }
        try
        {
            _pi.GetIpcSubscriber<nint, int, nint>("Penumbra.GameObjectRedrawn").Unsubscribe(OnGameObjectRedrawn);
        }
        catch { }
    }
}
