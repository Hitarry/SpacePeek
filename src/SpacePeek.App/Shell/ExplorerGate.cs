using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SpacePeek.Interop;

namespace SpacePeek.Shell;

/// <summary>
/// Pre-computes "may a bare Space press mean Preview?" so the low-level keyboard hook can
/// answer in a single volatile read. The hook callback must never touch COM or the disk:
/// a slow callback gets the hook silently removed by Windows.
/// </summary>
internal sealed class ExplorerGate
{
    private readonly DispatcherTimer _timer;
    private volatile bool _eligible;
    private bool _lastLogged;

    public ExplorerGate()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _timer.Tick += (_, _) => Sample();
        _timer.Start();
    }

    public bool Eligible => _eligible;

    public void Sample()
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            IntPtr root = ShellSelection.ResolveRoot(fg);
            bool explorer = ShellSelection.IsExplorerWindow(root, out _);

            bool caret = false;
            if (explorer) caret = HasCaret(fg);

            // The selection itself is verified on the UI thread by ToggleAsync. Gating on it here
            // would make Space depend on a 150 ms cached poll and feel unresponsive right after
            // switching windows.
            _eligible = explorer && !caret;

            if (_eligible != _lastLogged)
            {
                Log.Write($"gate: eligible={_eligible} (explorer={explorer} caret={caret})");
                _lastLogged = _eligible;
            }
        }
        catch (Exception ex)
        {
            _eligible = false;
            Log.Exception("gate", ex);
        }
    }

    private static bool HasCaret(IntPtr foreground)
    {
        uint tid = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var info = new NativeMethods.GUITHREADINFO();
        info.cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>();
        if (!NativeMethods.GetGUIThreadInfo(tid, ref info)) return false;

        if (info.hwndCaret != IntPtr.Zero) return true;

        if (info.hwndFocus != IntPtr.Zero)
        {
            string cls = ShellSelection.ClassOf(info.hwndFocus);
            if (cls.Contains("Edit", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
