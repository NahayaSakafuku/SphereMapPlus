using System.Runtime.InteropServices;

namespace SphereMapPlus.Native;

/// <summary>读取游戏内存前的安全性检查(VirtualQuery),杜绝探针野指针访问违规。</summary>
internal static unsafe class Memory
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint PAGE_GUARD = 0x100;
    private const uint PAGE_NOACCESS = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, nint dwLength);

    private static bool IsReadableProtect(uint protect)
    {
        if (protect == PAGE_NOACCESS || (protect & PAGE_GUARD) != 0)
            return false;
        // 可读权限:READONLY=2, READWRITE=4, WRITECOPY=8, EXECUTE_READ=0x20, EXECUTE_READWRITE=0x40, EXECUTE_WRITECOPY=0x80
        return protect is 0x02 or 0x04 or 0x08 or 0x20 or 0x40 or 0x80;
    }

    /// <summary>检查 [ptr, ptr+len) 是否全部落在已提交且可读的内存区域。</summary>
    public static bool IsReadable(nint ptr, nint len)
    {
        if (ptr == 0 || len <= 0)
            return false;
        try
        {
            var cur = ptr;
            var end = ptr + len;
            while (cur < end)
            {
                if (VirtualQuery(cur, out var mbi, (nint)sizeof(MEMORY_BASIC_INFORMATION)) == 0)
                    return false;
                if (mbi.State != MEM_COMMIT || !IsReadableProtect(mbi.Protect))
                    return false;
                var regionEnd = mbi.BaseAddress + mbi.RegionSize;
                if (regionEnd <= cur)
                    return false;
                cur = regionEnd;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否可安全读取一个指针字段。</summary>
    public static bool IsReadablePointer(nint ptr)
        => ptr != 0 && IsReadable(ptr, 1);
}
