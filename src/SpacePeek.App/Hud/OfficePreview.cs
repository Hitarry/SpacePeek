using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePeek.Interop;

namespace SpacePeek.Hud;

/// <summary>
/// Hosts the Windows shell preview handler for Office/RTF documents.
/// The handler renders into a child HWND, so this control is an HwndHost, not a WPF visual.
/// </summary>
internal sealed class OfficePreview : Grid
{
    private static readonly string[] Extensions =
        { ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt", ".rtf" };

    public static bool Handles(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".xlsm" or ".xlsb") return false;   // macro workbooks: never hand them to Office
        return Array.IndexOf(Extensions, ext) >= 0;
    }

    private readonly PreviewHost _host;
    private readonly TextBlock _status;
    private PreviewHandlerInterop.IPreviewHandler? _handler;
    private bool _disposed;
    private IntPtr _parentHwnd;
    private readonly double _boxWidth;
    private readonly double _boxHeight;
    private readonly Func<FrameworkElement?>? _fallback;
    private readonly PreviewHandlerInterop.IPreviewHandler? _prepared;
    private readonly string _path;

    public OfficePreview(string path, double boxWidth = 620, double boxHeight = 465,
        Func<FrameworkElement?>? fallback = null)
        : this(prepare: null, path, boxWidth, boxHeight, fallback)
    {
    }

    /// <summary>Uses an already-created handler; the caller has verified it works, so the
    /// "create → fail → swap content" path (which left a blank window) never runs.</summary>
    public OfficePreview(PreviewHandlerInterop.IPreviewHandler handler, string path,
        double boxWidth, double boxHeight)
        : this(prepare: handler, path, boxWidth, boxHeight, fallback: null)
    {
    }

    private OfficePreview(PreviewHandlerInterop.IPreviewHandler? prepare, string path,
        double boxWidth, double boxHeight, Func<FrameworkElement?>? fallback)
    {
        _prepared = prepare;
        _path = path;
        _boxWidth = boxWidth;
        _boxHeight = boxHeight;
        _fallback = fallback;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Width = boxWidth;
        Height = boxHeight;

        // The Office handler paints its own scrollbars at the edges of the HWND it is given.
        // We give it a larger window than the visible area and clip it, so the scrollbar chrome
        // falls outside the visible page.
        const double ScrollGutter = 22;
        _host = new PreviewHost
        {
            Width = boxWidth + ScrollGutter,
            Height = boxHeight + ScrollGutter,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var clip = new Border
        {
            Width = boxWidth,
            Height = boxHeight,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _host,
        };
        SetRow(clip, 0);
        Children.Add(clip);

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        SetRow(_status, 0);
        Children.Add(_status);

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Start(path)));
        Unloaded += (_, _) => DisposeHandler();
        SizeChanged += (_, _) => ApplyRect();
    }

    private void Start(string path)
    {
        try
        {
            _parentHwnd = _host.Handle;
            if (_parentHwnd == IntPtr.Zero)
            {
                ShowStatus("预览窗口未就绪");
                return;
            }

            _handler = _prepared ?? PreviewHandlerInterop.Create(path);

            PreviewHandlerInterop.RECTI rect = CurrentRect();
            _handler.SetWindow(_parentHwnd, ref rect);
            _handler.DoPreview();
            _host.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Exception("office", ex);
            UseFallback("此文件无法用系统预览组件显示：" + ex.Message);
        }
    }

    /// <summary>Falls back to the self-built page snapshot when the handler refuses the file
    /// (protected documents, unregistered handler, unusual producers).</summary>
    private void UseFallback(string reason)
    {
        try
        {
            FrameworkElement? alternative = _fallback?.Invoke();
            if (alternative is not null)
            {
                Log.Write($"office: fallback for {Path.GetFileName(_path)} ({reason})");
                _host.Visibility = Visibility.Collapsed;
                _status.Visibility = Visibility.Collapsed;
                SetRow(alternative, 0);
                Children.Add(alternative);
                return;
            }
        }
        catch (Exception ex) { Log.Exception("office-fallback", ex); }

        ShowStatus(reason);
    }

    private void ApplyRect()
    {
        if (_handler is null || _parentHwnd == IntPtr.Zero) return;
        try
        {
            PreviewHandlerInterop.RECTI rect = CurrentRect();
            _handler.SetRect(ref rect);
        }
        catch { }
    }

    private PreviewHandlerInterop.RECTI CurrentRect()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = _host.ActualWidth > 0 ? _host.ActualWidth : 600;
        double h = _host.ActualHeight > 0 ? _host.ActualHeight : 320;
        return new PreviewHandlerInterop.RECTI
        {
            Left = 0,
            Top = 0,
            Right = (int)(w * scale),
            Bottom = (int)(h * scale),
        };
    }

    private void ShowStatus(string message)
    {
        _host.Visibility = Visibility.Collapsed;
        _status.Text = message;
        _status.Visibility = Visibility.Visible;
    }

    private void DisposeHandler()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _handler?.Unload();
            if (_handler is not null && Marshal.IsComObject(_handler)) Marshal.ReleaseComObject(_handler);
        }
        catch { }
        _handler = null;
    }
}

/// <summary>Plain child window the preview handler can paint into.</summary>
internal sealed class PreviewHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    public IntPtr Handle { get; private set; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        Handle = CreateWindowEx(0, "static", "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN,
            0, 0, 100, 100, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, Handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        Handle = IntPtr.Zero;
    }
}




