using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FastFileExplorer.Models;

namespace FastFileExplorer.Services;

public sealed class FileIconProvider
{
    private readonly ConcurrentDictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageSource _fallbackIcon;
    private readonly ImageSource _folderIcon;

    public FileIconProvider()
    {
        var fallbackHandle = GetSystemIconHandleForFile("file.txt");
        _fallbackIcon = fallbackHandle != IntPtr.Zero ? CreateIconSource(fallbackHandle) : CreateGeometryFallbackIcon();
        var folderHandle = GetSystemIconHandleForFolder();
        _folderIcon = folderHandle != IntPtr.Zero ? CreateIconSource(folderHandle) : _fallbackIcon;
        _cache["__folder__"] = _folderIcon;
    }

    public ImageSource GetIcon(IndexedItem item)
    {
        var key = item.Kind == IndexedItemKind.Folder ? "__folder__" : item.Extension;
        return _cache.GetOrAdd(key, _ =>
        {
            var probePath = item.Kind == IndexedItemKind.Folder
                ? string.Empty
                : item.Extension is "(none)" or "folder" or null
                    ? "file"
                    : $"file.{item.Extension}";
            var iconHandle = item.Kind == IndexedItemKind.Folder
                ? GetSystemIconHandleForFolder()
                : GetSystemIconHandleForFile(probePath);

            if (iconHandle == IntPtr.Zero)
            {
                return _fallbackIcon;
            }

            return CreateIconSource(iconHandle);
        });
    }

    public ImageSource GetQuickIcon(IndexedItem item)
    {
        return item.Kind == IndexedItemKind.Folder ? _folderIcon : _fallbackIcon;
    }

    private ImageSource CreateIconSource(IntPtr iconHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                iconHandle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            return source;
        }
        finally
        {
            if (iconHandle != IntPtr.Zero)
            {
                _ = DestroyIcon(iconHandle);
            }
        }
    }

    private static ImageSource CreateGeometryFallbackIcon()
    {
        var drawing = new GeometryDrawing(
            System.Windows.Media.Brushes.Transparent,
            new System.Windows.Media.Pen(System.Windows.Media.Brushes.Gray, 1),
            new RectangleGeometry(new System.Windows.Rect(1, 1, 14, 14), 2, 2));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static IntPtr GetSystemIconHandleForFolder()
    {
        var stockInfo = new SHSTOCKICONINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>()
        };
        var hr = SHGetStockIconInfo(SIID_FOLDER, SHGSI_ICON | SHGSI_SMALLICON, ref stockInfo);
        return hr == 0 ? stockInfo.hIcon : IntPtr.Zero;
    }

    private static IntPtr GetSystemIconHandleForFile(string path)
    {
        var shinfo = new SHFILEINFO();
        _ = SHGetFileInfo(
            path,
            FILE_ATTRIBUTE_NORMAL,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        return shinfo.hIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(
        uint siid,
        uint uFlags,
        ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public IntPtr iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint SHGSI_ICON = 0x000000100;
    private const uint SHGSI_SMALLICON = 0x000000001;
    private const uint SIID_FOLDER = 0x3;
}
