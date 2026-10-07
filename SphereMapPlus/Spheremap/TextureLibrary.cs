using System.Text.Json;

namespace SphereMapPlus.Spheremap;

/// <summary>
/// 贴图仓库:把导入过的 PNG 复制进插件目录统一托管,方案与导入通过仓库项引用,原文件丢失不影响使用。
/// </summary>
public sealed class TextureLibrary
{
    public sealed class LibraryItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string File { get; set; } = "";
        public DateTime Added { get; set; }
    }

    private readonly string _dir;
    private readonly string _metaPath;
    private readonly IPluginLog _log;

    public List<LibraryItem> Items { get; private set; } = [];

    public TextureLibrary(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _dir = Path.Combine(pi.GetPluginConfigDirectory(), "library");
        Directory.CreateDirectory(_dir);
        _metaPath = Path.Combine(pi.GetPluginConfigDirectory(), "library.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_metaPath))
                return;
            var doc = JsonDocument.Parse(File.ReadAllText(_metaPath));
            Items.Clear();
            if (doc.RootElement.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in arr.EnumerateArray())
                {
                    var it = new LibraryItem
                    {
                        Id = e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                        Name = e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        File = e.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                        Added = e.TryGetProperty("added", out var a) && a.TryGetDateTime(out var dt) ? dt : DateTime.Now,
                    };
                    if (File.Exists(PngPath(it)))
                        Items.Add(it);
                }
            }
        }
        catch (Exception e)
        {
            _log.Error(e, "读取贴图仓库失败");
        }
    }

    private void Save()
    {
        try
        {
            var obj = new
            {
                items = Items.Select(i => new { id = i.Id, name = i.Name, file = i.File, added = i.Added }).ToList(),
            };
            File.WriteAllText(_metaPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            _log.Error(e, "保存贴图仓库失败");
        }
    }

    public string PngPath(LibraryItem it) => Path.Combine(_dir, it.File);

    /// <summary>把 PNG 复制入库。返回错误或 null。</summary>
    public string Import(string sourcePng, string name)
    {
        try
        {
            if (!File.Exists(sourcePng))
                return "源文件不存在";
            name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePng) : name.Trim();
            var id = Guid.NewGuid().ToString("N")[..12];
            var file = $"{id}.png";
            File.Copy(sourcePng, Path.Combine(_dir, file), true);
            Items.Add(new LibraryItem { Id = id, Name = name, File = file, Added = DateTime.Now });
            Save();
            return null;
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    public string Rename(string id, string newName)
    {
        var it = Items.FirstOrDefault(i => i.Id == id);
        if (it == null)
            return "仓库项不存在";
        it.Name = newName.Trim();
        Save();
        return null;
    }

    public string Delete(string id)
    {
        var it = Items.FirstOrDefault(i => i.Id == id);
        if (it == null)
            return "仓库项不存在";
        try
        {
            var p = PngPath(it);
            if (File.Exists(p))
                File.Delete(p);
        }
        catch (Exception e)
        {
            _log.Warning($"删除仓库文件失败: {e.Message}");
        }
        Items.Remove(it);
        Save();
        return null;
    }
}
