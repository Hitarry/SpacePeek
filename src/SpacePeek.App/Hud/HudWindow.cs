using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePeek.Interop;

namespace SpacePeek.Hud;

internal sealed class HudWindow : Window
{
    private const int Gap = 10;
    private readonly Border _frame;

    private IntPtr _anchorHost;
    private Rect? _anchorRect;

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
        };
        Icon = AppIcon.LoadWpfIcon();
        Content = _frame;
        SizeChanged += OnSizeChanged;
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr h = Handle;
        IntPtr ex = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE,
            (IntPtr)((long)ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));

        int pref = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(h, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    /// <summary>Shows <paramref name="content"/> anchored to the selected item (physical pixels from UIA).</summary>
    public async System.Threading.Tasks.Task PresentAsync(UIElement content, IntPtr explorerHwnd, Rect? anchorPhysical)
    {
        _frame.Child = content;
        _anchorHost = explorerHwnd;
        _anchorRect = anchorPhysical;

        // Wait for async previews (PDF page size, Office page) before deciding the window size:
        // showing an A4 placeholder first and resizing a moment later is what made it feel janky.
        if (content is IDeferredPreview deferred)
        {
            await System.Threading.Tasks.Task.WhenAny(deferred.ReadyTask,
                System.Threading.Tasks.Task.Delay(600));
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Rect work = MonitorWorkAreaDip(explorerHwnd, scale);
        double maxWidth = Math.Min(1450, work.Width * 0.96);

        double desired;
        try
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desired = content.DesiredSize.Width;
        }
        catch { desired = maxWidth; }

        Width = Math.Max(360, Math.Min(desired + 4, maxWidth));

        if (!IsVisible)
        {
            Show();
        }

        // size-to-content needs one layout pass before we can position against real pixels
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => PlacePhysical(explorerHwnd, anchorPhysical)));
    }

    /// <summary>Content loads asynchronously, so the window keeps growing: re-anchor on every size change.</summary>
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsVisible) PlacePhysical(_anchorHost, _anchorRect);
    }

    private void PlacePhysical(IntPtr explorerHwnd, Rect? anchor)
    {
        // Everything below is DIP: the anchor arrives in physical pixels from UIA, the monitor
        // rect arrives in physical pixels from Win32, and WPF positions in DIP.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1.0;

        NativeMethods.MONITORINFO mi = MonitorInfoOf(explorerHwnd);
        double workLeft = mi.rcWork.Left / scale;
        double workTop = mi.rcWork.Top / scale;
        double workRight = mi.rcWork.Right / scale;
        double workBottom = mi.rcWork.Bottom / scale;

        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 0;

        double x, y;
        if (anchor is { } a && a.Width > 0 && a.Height > 0)
        {
            double right = a.Right / scale + Gap;
            double left = a.Left / scale - w - Gap;

            if (right + w <= workRight) x = right;
            else if (left >= workLeft) x = left;
            else x = (workLeft + workRight - w) / 2;

            y = a.Top / scale;
        }
        else
        {
            x = (workLeft + workRight - w) / 2;
            y = (workTop + workBottom - h) / 2;
        }

        if (y + h > workBottom) y = workBottom - h - Gap;
        if (y < workTop) y = workTop + Gap;

        Left = x;
        Top = y;

        Log.Write($"place: ({x:F0},{y:F0}) {w:F0}x{h:F0} dip scale={scale:F2} anchor={(anchor.HasValue ? anchor.Value.ToString() : "null")}");
    }

    private static NativeMethods.MONITORINFO MonitorInfoOf(IntPtr hwnd)
    {
        NativeMethods.MONITORINFO mi = default;
        mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>();
        IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, 2 /*MONITOR_DEFAULTTONEAREST*/);
        if (!NativeMethods.GetMonitorInfo(mon, ref mi))
        {
            mi.rcWork = new NativeMethods.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        }
        return mi;
    }

    private static Rect MonitorWorkAreaDip(IntPtr hwnd, double scale)
    {
        NativeMethods.MONITORINFO mi = MonitorInfoOf(hwnd);
        return new Rect(
            mi.rcWork.Left / scale,
            mi.rcWork.Top / scale,
            mi.rcWork.Width / scale,
            mi.rcWork.Height / scale);
    }
}






