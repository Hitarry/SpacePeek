using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SpacePeek.Interop;

internal sealed record SelectedItem(string Path, string Name, bool IsFolder);

/// <summary>
/// Reads the Explorer selection. v0 uses Shell.Application (dynamic COM) because it is
/// 2 ms warm; the direct IShellWindows/IFolderView path can come later if profiling demands it.
/// </summary>
internal static class ShellSelection
{
    private static readonly string[] DeniedClasses =
    {
        "Progman",                    // desktop
        "WorkerW",
        "Shell_TrayWnd",              // taskbar
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", // start menu / search / flyouts
    };

    private static object? _shell;

    /// <summary>Explorer may host content in XAML islands; the shell view lives on the root window.</summary>
    public static IntPtr ResolveRoot(IntPtr hwnd)
    {
        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
    }

    /// <summary>
    /// Deliberately process-based rather than class-name based: Win11's Explorer mixes
    /// CabinetWClass with XamlExplorerHostIslandWindow, and the class list keeps changing.
    /// </summary>
    public static bool IsExplorerWindow(IntPtr hwnd, out string className)
    {
        className = ClassOf(hwnd);
        if (Array.IndexOf(DeniedClasses, className) >= 0) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Shell.Application is expensive to create (~100 ms) and cheap to reuse.</summary>
    private static object? Shell()
    {
        if (_shell is not null) return _shell;
        try
        {
            Type? t = Type.GetTypeFromProgID("Shell.Application");
            if (t is not null) _shell = Activator.CreateInstance(t);
        }
        catch { _shell = null; }
        return _shell;
    }

    public static void ResetShell() => _shell = null;

    public static bool HasSelection(IntPtr hwnd) => Read(hwnd).Count > 0;

    public static IReadOnlyList<SelectedItem> Read(IntPtr hwnd)
    {
        var result = new List<SelectedItem>();
        try
        {
            object? shellObject = Shell();
            if (shellObject is null) return result;
            dynamic shell = shellObject;
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic w = windows.Item(i);
                IntPtr h;
                try { h = (IntPtr)(int)w.HWND; } catch { continue; }
                if (h != hwnd) continue;
                ReadFrom(w, result);
            }
        }
        catch { /* unsupported location -> silent */ }
        return result;
    }

    private static void ReadFrom(dynamic w, List<SelectedItem> sink)
    {
        try
        {
            dynamic doc = w.Document;
            dynamic items = doc.SelectedItems();
            int n = (int)items.Count;
            for (int i = 0; i < n; i++)
            {
                dynamic it = items.Item(i);
                string path = (string)it.Path;
                string name = (string)it.Name;
                bool isFolder = false;
                try { isFolder = (bool)it.IsFolder; } catch { }
                sink.Add(new SelectedItem(path, name, isFolder));
            }
        }
        catch { }
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
