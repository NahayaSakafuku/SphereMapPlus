using System.Runtime.InteropServices;

namespace SphereMapPlus.Native;

    /// <summary>
    /// 极简 D3D11 COM vtable 互操作。只实现探针所需的几个方法,槽位号已离线实证(创建真实设备逐个验证):
    /// ID3D11Device: CreateTexture2D=5, CreateShaderResourceView=7;
    /// ID3D11DeviceChild: GetDevice=3; ID3D11View: GetResource=7; ID3D11ShaderResourceView: GetDesc=8;
    /// 注意 ID3D11DeviceContext 直接继承 ID3D11DeviceChild(不是 ID3D11View!),方法从槽 7 开始:
    /// CopyResource=47, Map=14, Unmap=15;
    /// ID3D11Multithread: Enter=4, Leave=5 (IID 9B7E4E00-342C-4106-A19F-4F27-04F689F0)。
    /// </summary>
internal static unsafe class D3D11
{
    public static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IID_ID3D11Multithread = new(0x9B7E4E00, 0x342C, 0x4106, 0xA1, 0x9F, 0x4F, 0x27, 0x04, 0xF6, 0x89, 0xF0);

    /// <summary>取 COM 对象 vtable 第 slot 槽中存储的函数地址。注意必须解引用两次:先取 vtable 指针,再取槽位里的值。</summary>
    private static void* Slot(void* obj, int slot)
    {
        var vtable = *(nint**)obj;
        return ((void**)vtable)[slot];
    }

    // ---- IUnknown ----
    public static int QueryInterface(void* obj, in Guid iid, out void* outPtr)
    {
        var q = (delegate* unmanaged<void*, Guid*, void**, int>)Slot(obj, 0);
        var g = iid;
        void* p = null;
        var hr = q(obj, &g, &p);
        outPtr = p;
        return hr;
    }

    public static uint Release(void* obj)
        => ((delegate* unmanaged<void*, uint>)Slot(obj, 2))(obj);

    // ---- ID3D11DeviceChild / ID3D11View ----
    public static void* GetDevice(void* deviceChild)
    {
        void* dev = null;
        ((delegate* unmanaged<void*, void**, void>)Slot(deviceChild, 3))(deviceChild, &dev);
        return dev;
    }

    /// <summary>ID3D11View::GetResource(vtable 7):取视图背后的真实资源。</summary>
    public static void* GetResource(void* view)
    {
        void* res = null;
        ((delegate* unmanaged<void*, void**, void>)Slot(view, 7))(view, &res);
        return res;
    }

    /// <summary>ID3D11ShaderResourceView::GetDesc(vtable 8):读取游戏自己创建视图时的完整描述符。</summary>
    public static void GetSrvDesc(void* srv, out SrvDesc desc)
    {
        var d = new SrvDesc();
        ((delegate* unmanaged<void*, SrvDesc*, void>)Slot(srv, 8))(srv, &d);
        desc = d;
    }

    /// <summary>ID3D11Texture2D::GetDesc(vtable 10):读取资源创建时的真实描述符。</summary>
    public static void GetTexture2DDesc(void* tex, out Texture2DDesc desc)
    {
        var d = new Texture2DDesc();
        ((delegate* unmanaged<void*, Texture2DDesc*, void>)Slot(tex, 10))(tex, &d);
        desc = d;
    }

    // ---- ID3D11Device ----
    public static int CreateTexture2D(void* device, in Texture2DDesc desc, void* initialData, out void* texture)
    {
        var f = (delegate* unmanaged<void*, Texture2DDesc*, void*, void**, int>)Slot(device, 5);
        void* p = null;
        var d = desc;
        var hr = f(device, &d, initialData, &p);
        texture = p;
        return hr;
    }

    public static int CreateShaderResourceView(void* device, void* resource, in SrvDesc desc, out void* srv)
    {
        var f = (delegate* unmanaged<void*, void*, SrvDesc*, void**, int>)Slot(device, 7);
        void* p = null;
        var d = desc;
        var hr = f(device, resource, &d, &p);
        srv = p;
        return hr;
    }

    // ---- ID3D11DeviceContext(直接继承 ID3D11DeviceChild,方法从槽 7 起) ----
    public static void CopyResource(void* ctx, void* dst, void* src)
        => ((delegate* unmanaged<void*, void*, void*, void>)Slot(ctx, 47))(ctx, dst, src);

    public static int Map(void* ctx, void* resource, uint subresource, uint mapType, uint flags, out MappedSubresource mapped)
    {
        var f = (delegate* unmanaged<void*, void*, uint, uint, uint, MappedSubresource*, int>)Slot(ctx, 14);
        MappedSubresource m = default;
        var hr = f(ctx, resource, subresource, mapType, flags, &m);
        mapped = m;
        return hr;
    }

    public static void Unmap(void* ctx, void* resource, uint subresource)
        => ((delegate* unmanaged<void*, void*, uint, void>)Slot(ctx, 15))(ctx, resource, subresource);

    // ---- ID3D11Multithread ----
    public static bool TryGetMultithread(void* ctx, out void* mt)
    {
        mt = null;
        return QueryInterface(ctx, IID_ID3D11Multithread, out var p) == 0 ? (mt = p) != null : false;
    }

    public static void Enter(void* mt) => ((delegate* unmanaged<void*, void>)Slot(mt, 4))(mt);

    public static void Leave(void* mt) => ((delegate* unmanaged<void*, void>)Slot(mt, 5))(mt);

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public SampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Tex2DArraySrv
    {
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SrvDesc
    {
        public int Format;
        public uint ViewDimension;
        public Tex2DArraySrv Texture2DArray;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MappedSubresource
    {
        public void* Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SubresourceData
    {
        public void* Data;
        public uint Pitch;
        public uint SlicePitch;
    }

    // 常量(与 SDK 枚举一致)
    public const uint UsageStaging = 3;
    public const uint CpuAccessRead = 0x20000;
    public const uint CpuAccessWrite = 0x10000;
    public const uint MapRead = 1;
    public const uint SrvDimensionTexture2DArray = 5;
    public const uint MipLevelsAll = 0xFFFFFFFF;

    // 游戏纹理格式 → DXGI
    public static int ToDxgiFormat(uint gameFormat) => gameFormat switch
    {
        0x1450 => 87,  // B8G8R8A8_UNORM
        0x1451 => 91,  // B8G8R8X8_UNORM
        0x2460 => 10,  // R16G16B16A16_FLOAT
        0x6432 => 98,  // BC7_UNORM
        0x3420 => 71,  // BC1_UNORM
        0x3431 => 77,  // BC3_UNORM
        0x1132 => 61,  // R8_UNORM
        _ => 0,
    };

    /// <summary>每种 DXGI 格式每像素字节数(用于回读缓冲;压缩格式返回 0 表示按块处理)。</summary>
    public static int BytesPerPixel(int dxgiFormat) => dxgiFormat switch
    {
        87 => 4,   // B8G8R8A8
        91 => 4,
        10 => 8,   // R16G16B16A16 float
        61 => 1,   // R8
        _ => 0,    // BC1 等块压缩格式在 SliceReader 中按 8 字节/块单独处理
    };

    /// <summary>块压缩格式:每 4×4 块字节数(BC1=8);非压缩格式返回 0。</summary>
    public static int BytesPerBlock(int dxgiFormat) => dxgiFormat switch
    {
        71 => 8,   // BC1
        _ => 0,
    };
}
