using System.Text;

using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace SphereMapPlus.Native;

/// <summary>
/// CharacterUtility 资源表访问。表中 97 号槽位是全局球面贴图纹理数组
/// (chara/common/texture/sphere_d_array.tex,Texture2DArray),与 Penumbra 的 SphereDArrayTexIndex 一致。
/// CharacterUtility 布局:0x00 vtable,0x08 起 115 个 ResourceHandle 指针(与 Penumbra.CharacterUtilityData 相同)。
///
/// 安全性:所有解引用前都做 VirtualQuery 可读性校验与字段合理性校验,并用
/// ExpectedPath 精确匹配门控——CharacterUtility 在游戏早期初始化,槽位指针可能短暂指向
/// 未初始化/异构对象,直接按 TextureResourceHandle 解释会访问违规导致游戏原生崩溃。
/// </summary>
internal static unsafe class SphereArray
{
    public const int SphereDArraySlot = 97;
    public const int TotalSlots = 115;
    private const int HandlesOffset = 0x8;
    public const string ExpectedPath = "chara/common/texture/sphere_d_array.tex";

    public static bool TryGetInstance(out CharacterUtility* inst)
    {
        inst = null;
        try { inst = CharacterUtility.Instance(); }
        catch { }
        return inst != null && Memory.IsReadablePointer((nint)inst);
    }

    public static ResourceHandle* GetHandle(int slot)
    {
        if (!TryGetInstance(out var inst))
            return null;
        var table = (ResourceHandle**)((byte*)inst + HandlesOffset);
        if (!Memory.IsReadable(new nint(table), sizeof(nint) * (slot + 1)))
            return null;
        var handle = table[slot];
        if (handle == null || !Memory.IsReadablePointer((nint)handle))
            return null;
        // vtable 指针必须有效,句柄本体至少要能容纳 ResourceHandle 基类(0xB0)
        if (*(nint*)handle == 0 || !Memory.IsReadable(*(nint*)handle, 8) || !Memory.IsReadable((nint)handle, 0xB0))
            return null;
        return handle;
    }

    /// <summary>防御性读取句柄内嵌的 StdString 文件名。任何不合理状态都返回 false,绝不猜测性解引用。</summary>
    public static bool TryReadHandleName(ResourceHandle* h, out string name)
    {
        name = string.Empty;
        var s = &h->FileName;
        var len = s->Length;
        var cap = s->Capacity;
        if (len == 0)
            return true;
        // 合法性:长度不超过容量,且都处于可信范围(std::string SSO 阈值 16)
        if (len > 4096ul || cap < len || cap > 0x40000ul)
            return false;
        byte* p = cap >= 16 ? s->BufferPtr : s->Buffer;
        if (p == null)
            return false;
        if (!Memory.IsReadable((nint)p, (nint)len))
            return false;
        for (var i = 0ul; i < len; i++)
        {
            var c = p[i];
            if (c < 0x20 || c >= 0x7F)
                return false; // 游戏路径纯可打印 ASCII,出现其他字节即为未初始化内存
        }
        name = Encoding.ASCII.GetString(p, (int)len);
        return true;
    }

    /// <summary>取得 97 号槽位的 TextureResourceHandle,只有当其文件名精确匹配球面数组路径时才返回。</summary>
    public static TextureResourceHandle* GetSphereArrayHandle(out string name)
    {
        name = string.Empty;
        var h = GetHandle(SphereDArraySlot);
        if (h == null)
            return null;
        if (!TryReadHandleName(h, out name))
            return null;
        if (!string.Equals(name, ExpectedPath, StringComparison.OrdinalIgnoreCase))
            return null;
        // 精确匹配通过,句柄可信为 TextureResourceHandle(0x150)。TextureResourceHandle.Texture 字段读取前再验一次。
        var texHandle = (TextureResourceHandle*)h;
        if (!Memory.IsReadable((nint)texHandle, 0x150))
            return null;
        return texHandle;
    }

    /// <summary>97 号槽位资源对应的 Kernel.Texture*(渲染对象,内含 D3D 纹理/SRV 指针)。</summary>
    public static Texture* GetKernelTexturePtr()
    {
        var h = GetSphereArrayHandle(out _);
        if (h == null)
            return null;
        var tex = h->Texture;
        if (tex == null || !Memory.IsReadable((nint)tex, 0xC8))
            return null;
        return tex;
    }

    /// <summary>球面数组运行时信息。Loaded=false 表示当前不可信/未就绪,其余字段不应使用。</summary>
    public readonly record struct SphereInfo(
        bool Loaded,
        string FileName,
        uint HeaderFlags,
        uint HeaderFormat,
        int Width,
        int Height,
        int MipCount,
        int HeaderArraySize,
        uint TexFormat,
        uint TexFlags,
        int TexArraySize,
        int TexActualWidth,
        int TexActualHeight,
        nint D3DTexture,
        nint D3DSrv)
    {
        public bool Is2DArray => (HeaderFlags & 0x10000000) != 0; // TextureFlags.TextureType2DArray
        public int DxgiFormat => D3D11.ToDxgiFormat(TexFormat);
        public int Bpp => D3D11.BytesPerPixel(DxgiFormat);
    }

    public static SphereInfo ReadInfo()
    {
        var h = GetSphereArrayHandle(out var name);
        if (h == null)
            return default;

        // GPU 侧可能尚未创建(Texture 在 LoadIntoKernel 后才有),此时只报文件名
        if (!Memory.IsReadable((nint)(&h->Texture), sizeof(nint)))
            return default;
        var tex = h->Texture;
        if (tex == null)
            return new SphereInfo(true, name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (!Memory.IsReadable((nint)tex, 0xC8))
            return default;

        var header = h->Header;
        return new SphereInfo(
            true,
            name,
            (uint)header.Type,
            (uint)header.Format,
            header.Width,
            header.Height,
            header.MipCountAndFlag & 0x1F,
            header.ArraySize,
            (uint)tex->TextureFormat,
            (uint)tex->Flags,
            tex->ArraySize,
            (int)tex->ActualWidth,
            (int)tex->ActualHeight,
            (nint)tex->D3D11Texture2D,
            (nint)tex->D3D11ShaderResourceView);
    }

    /// <summary>全部槽位的 (序号, 文件名, 是否tex) 列表,用于调试。读取失败的槽位跳过。</summary>
    public static List<(int Slot, string Name, bool IsTex)> DumpAllSlots()
    {
        var list = new List<(int, string, bool)>();
        for (var i = 0; i < TotalSlots; i++)
        {
            var h = GetHandle(i);
            if (h == null)
                continue;
            if (!TryReadHandleName(h, out var name))
                continue;
            list.Add((i, name, name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)));
        }
        return list;
    }
}
