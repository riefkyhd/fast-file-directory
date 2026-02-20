using System.Diagnostics;
using System.IO;
using System.Windows;
using FastFileExplorer.Services;

namespace FastFileExplorer.ShellHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!TryParseArgs(args, out var path, out var x, out var y))
        {
            return 1;
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return 2;
        }

        try
        {
            var app = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };

            var owner = new Window
            {
                Width = 0,
                Height = 0,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                AllowsTransparency = true,
                Opacity = 0,
                ShowActivated = false,
                Left = -10000,
                Top = -10000
            };

            owner.Show();

            var menuItems = new[]
            {
                new ShellContextMenu.CustomMenuItem(ShellContextMenu.CommandIds.OpenContainingFolder, "Open Containing Folder"),
                new ShellContextMenu.CustomMenuItem(ShellContextMenu.CommandIds.CopyPath, "Copy Path to Clipboard"),
                new ShellContextMenu.CustomMenuItem(ShellContextMenu.CommandIds.EditWithVsCode, "Edit with VS Code")
            };

            ShellContextMenu.Show(owner, path, menuItems, new System.Windows.Point(x, y), commandId => ExecuteCustomCommand(path, commandId));

            owner.Close();
            app.Shutdown();
            return 0;
        }
        catch
        {
            return 3;
        }
    }

    private static bool TryParseArgs(string[] args, out string path, out int x, out int y)
    {
        path = string.Empty;
        x = 0;
        y = 0;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                path = args[++i];
                continue;
            }

            if (string.Equals(arg, "--x", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], out x);
                continue;
            }

            if (string.Equals(arg, "--y", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                _ = int.TryParse(args[++i], out y);
            }
        }

        return !string.IsNullOrWhiteSpace(path);
    }

    private static void ExecuteCustomCommand(string path, int commandId)
    {
        switch (commandId)
        {
            case ShellContextMenu.CommandIds.OpenContainingFolder:
                OpenContainingFolder(path);
                break;
            case ShellContextMenu.CommandIds.CopyPath:
                try
                {
                    System.Windows.Clipboard.SetText(path);
                }
                catch
                {
                    // Ignore clipboard failures in helper process.
                }
                break;
            case ShellContextMenu.CommandIds.EditWithVsCode:
                EditWithVsCode(path);
                break;
        }
    }

    private static void OpenContainingFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                _ = Process.Start(new ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
                return;
            }

            _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore launch failures in helper process.
        }
    }

    private static void EditWithVsCode(string path)
    {
        try
        {
            var argument = Directory.Exists(path)
                ? $"\"{path}\""
                : $"-g \"{path}\"";

            _ = Process.Start(new ProcessStartInfo("code", argument)
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore launch failures in helper process.
        }
    }
}
