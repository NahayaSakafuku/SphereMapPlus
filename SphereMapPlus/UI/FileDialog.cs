using System.Runtime.InteropServices;

namespace SphereMapPlus.UI;

/// <summary>Win32 打开文件对话框(comdlg32 GetOpenFileNameW)。</summary>
internal static class FileDialog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public nint hwndOwner;
        public nint hInstance;
        public nint lpstrFilter;
        public nint lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public nint lpstrFile;
        public int nMaxFile;
        public nint lpstrFileTitle;
        public int nMaxFileTitle;
        public nint lpstrInitialDir;
        public nint lpstrTitle;
        public int Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        public nint lpstrDefExt;
        public nint lCustData;
        public nint lpfnHook;
        public nint lpTemplateName;
        public nint pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

    private const int OFN_EXPLORER = 0x80000;
    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int OFN_PATHMUSTEXIST = 0x800;
    private const int OFN_HIDEREADONLY = 0x4;
    private const int OFN_NOCHANGEDIR = 0x8;

    /// <summary>打开"选择 PNG"对话框。返回完整路径或 null(取消)。</summary>
    public static string? PickPng()
        => PickFile("PNG 图片\0*.png\0所有文件\0*.*\0\0", "选择要导入的球面贴图 PNG");

    /// <summary>打开"选择方案包"对话框。返回完整路径或 null(取消)。</summary>
    public static string? PickSchemePackage()
        => PickFile("SphereMapPlus 方案包\0*.smpk\0\0", "选择要导入的方案包");

    private static string? PickFile(string filter, string title)
    {
        var filterH = Marshal.StringToHGlobalUni(filter);
        var fileBuf = Marshal.AllocHGlobal(1024 * 2);
        var titleH = Marshal.StringToHGlobalUni(title);
        try
        {
            Marshal.WriteInt16(fileBuf, 0, 0);
            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                lpstrFilter = filterH,
                lpstrFile = fileBuf,
                nMaxFile = 1024,
                lpstrTitle = titleH,
                Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR,
            };
            if (!GetOpenFileNameW(ref ofn))
                return null;
            return Marshal.PtrToStringUni(fileBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(filterH);
            Marshal.FreeHGlobal(fileBuf);
            Marshal.FreeHGlobal(titleH);
        }
    }

    /// <summary>保存文件对话框。返回完整路径或 null(取消)。</summary>
    public static string? SaveFile(string filter, string defaultName, string title)
    {
        var filterH = Marshal.StringToHGlobalUni(filter);
        var fileBuf = Marshal.AllocHGlobal(1024 * 2);
        var titleH = Marshal.StringToHGlobalUni(title);
        var defExt = Marshal.StringToHGlobalUni(filter.Split('\0').Where(s => s.StartsWith("*")).FirstOrDefault()?.TrimStart('*') ?? "");
        try
        {
            var name = defaultName;
            if (!name.Contains('.'))
                name += (defExt != nint.Zero) ? Marshal.PtrToStringUni(defExt) : "";
            Marshal.Copy(name.ToCharArray(), 0, fileBuf, Math.Min(name.Length, 1023));

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                lpstrFilter = filterH,
                lpstrFile = fileBuf,
                nMaxFile = 1024,
                lpstrTitle = titleH,
                lpstrDefExt = defExt,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR | 0x2 /* OVERWRITEPROMPT */,
            };
            if (!GetSaveFileNameW(ref ofn))
                return null;
            return Marshal.PtrToStringUni(fileBuf);
        }
        finally
        {
            Marshal.FreeHGlobal(filterH);
            Marshal.FreeHGlobal(fileBuf);
            Marshal.FreeHGlobal(titleH);
            if (defExt != nint.Zero)
                Marshal.FreeHGlobal(defExt);
        }
    }
}
