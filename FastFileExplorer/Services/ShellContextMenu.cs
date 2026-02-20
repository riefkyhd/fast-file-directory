using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace FastFileExplorer.Services;

public static class ShellContextMenu
{
    private static int _menuOpen;
    public static class CommandIds
    {
        public const int OpenContainingFolder = 0x1001;
        public const int CopyPath = 0x1002;
        public const int EditWithVsCode = 0x1003;
    }

    public readonly record struct CustomMenuItem(int Id, string Text);

    public static void Show(Window owner, string path, IReadOnlyList<CustomMenuItem> customItems, System.Windows.Point screenPoint, Action<int> onCustomCommand)
    {
        if (owner is null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var hwnd = new WindowInteropHelper(owner).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        if (Interlocked.Exchange(ref _menuOpen, 1) == 1)
        {
            return;
        }

        IntPtr pidl = IntPtr.Zero;
        IntPtr contextMenuPtr = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;
        IContextMenu2? contextMenu2 = null;
        IContextMenu3? contextMenu3 = null;
        HwndSource? source = null;
        HwndSourceHook? hook = null;

        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
            {
                return;
            }

            var iidShellFolder = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidl, ref iidShellFolder, out parentFolder, out var childPidl) != 0 || parentFolder is null)
            {
                return;
            }

            var apidl = new[] { childPidl };
            var iidContextMenu = typeof(IContextMenu).GUID;
            parentFolder.GetUIObjectOf(hwnd, 1, apidl, ref iidContextMenu, IntPtr.Zero, out contextMenuPtr);
            if (contextMenuPtr == IntPtr.Zero)
            {
                return;
            }

            contextMenu = (IContextMenu)Marshal.GetObjectForIUnknown(contextMenuPtr);
            contextMenu2 = contextMenu as IContextMenu2;
            contextMenu3 = contextMenu as IContextMenu3;

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            foreach (var item in customItems)
            {
                AppendMenu(menu, MF_STRING, (UIntPtr)item.Id, item.Text);
            }

            if (customItems.Count > 0)
            {
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, string.Empty);
            }

            const int idCmdFirst = 0x2000;
            const int idCmdLast = 0x7FFF;
            contextMenu.QueryContextMenu(menu, 0, idCmdFirst, idCmdLast, CMF_NORMAL);

            source = HwndSource.FromHwnd(hwnd);
            if (source is not null && (contextMenu2 is not null || contextMenu3 is not null))
            {
                hook = (IntPtr hwndHook, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (contextMenu3 is not null && contextMenu3.HandleMenuMsg2(msg, wParam, lParam, out var result) == 0)
                    {
                        handled = msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR;
                        return result;
                    }

                    if (contextMenu2 is not null && contextMenu2.HandleMenuMsg(msg, wParam, lParam) == 0)
                    {
                        handled = msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM;
                        return IntPtr.Zero;
                    }

                    return IntPtr.Zero;
                };

                source.AddHook(hook);
            }

            var selected = TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                (int)screenPoint.X,
                (int)screenPoint.Y,
                hwnd,
                IntPtr.Zero);

            if (selected == 0)
            {
                return;
            }

            if (selected >= CommandIds.OpenContainingFolder && selected <= CommandIds.EditWithVsCode)
            {
                onCustomCommand(selected);
                return;
            }

            if (selected >= idCmdFirst)
            {
                try
                {
                    InvokeContextMenuCommand(contextMenu, hwnd, selected - idCmdFirst, screenPoint);
                }
                catch
                {
                    // Ignore shell verb invocation failures.
                }
            }
        }
        catch
        {
            // Swallow native menu exceptions to avoid crashing the app.
        }
        finally
        {
            if (hook is not null && source is not null)
            {
                source.RemoveHook(hook);
            }

            if (menu != IntPtr.Zero)
            {
                DestroyMenu(menu);
            }

            if (contextMenuPtr != IntPtr.Zero)
            {
                Marshal.Release(contextMenuPtr);
            }

            if (parentFolder is not null)
            {
                Marshal.ReleaseComObject(parentFolder);
            }

            if (pidl != IntPtr.Zero)
            {
                CoTaskMemFree(pidl);
            }
            Interlocked.Exchange(ref _menuOpen, 0);
        }
    }

    private static void InvokeContextMenuCommand(IContextMenu? contextMenu, IntPtr hwnd, int commandId, System.Windows.Point screenPoint)
    {
        if (contextMenu is null)
        {
            return;
        }

        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
            hwnd = hwnd,
            lpVerb = (IntPtr)commandId,
            lpVerbW = (IntPtr)commandId,
            nShow = SW_SHOWNORMAL,
            ptInvoke = new POINT { x = (int)screenPoint.X, y = (int)screenPoint.Y }
        };

        contextMenu.InvokeCommand(ref info);
    }

    private const uint CMF_NORMAL = 0x00000000;
    private const int SW_SHOWNORMAL = 1;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_DRAWITEM = 0x002B;
    private const int WM_MEASUREITEM = 0x002C;
    private const int WM_MENUCHAR = 0x0120;
    private const int TPM_RETURNCMD = 0x0100;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr pidl,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder ppv,
        out IntPtr ppidlLast);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, int fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        [PreserveSig]
        int EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);

        [PreserveSig]
        int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetAttributesOf(int cidl, IntPtr apidl, ref uint rgfInOut);

        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, int cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

        [PreserveSig]
        int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [Guid("000214e4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(int idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName, int cchMax);
    }

    [ComImport]
    [Guid("000214f4-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(int idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName, int cchMax);

        [PreserveSig]
        int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);

        void InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        void GetCommandString(int idCmd, uint uType, uint pReserved, [MarshalAs(UnmanagedType.LPStr)] StringBuilder pszName, int cchMax);

        [PreserveSig]
        int HandleMenuMsg(int uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(int uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }
}
