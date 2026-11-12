using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePeek.Hud;

/// <summary>
/// Shows page 1 of a Word/PowerPoint file as a fitted page image, so the whole page is visible
/// instead of a 100%-scale crop. Falls back to the shell handler (then to the text snapshot)
/// when Office cannot export the file.
/// </summary>
internal sealed class OfficePagePreview : Grid, IDeferredPreview
{
    private readonly string _path;
    private readonly double _boxWidth;
    private readonly double _boxHeight;
    private readonly int _captureWidth;
    private readonly int _captureHeight;
    private readonly bool _provisionalFirst;
    private readonly Func<FrameworkElement?>? _fallback;

    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly TextBlock _footer;

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadyTask => _ready.Task;

    public OfficePagePreview(string path, double boxWidth, double boxHeight,
        Func<FrameworkElement?>? fallback = null, int captureWidth = 816, int captureHeight = 1056,
        bool provisionalFirst = false)
    {
        _provisionalFirst = provisionalFirst;
        _captureWidth = captureWidth;
        _captureHeight = captureHeight;
        _path = path;
        _boxWidth = boxWidth;
        _boxHeight = boxHeight;
        _fallback = fallback;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _frame = new Border
        {
            Width = boxWidth,
            Height = boxHeight,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        _frame.Child = _image;
        SetRow(_frame, 0);
        Children.Add(_frame);

        _status = new TextBlock
        {
            Text = "正在生成第 1 页…",
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            FontSize = 13,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        SetRow(_status, 0);
        Children.Add(_status);

        _footer = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            FontSize = 12,
            Margin = new Thickness(14, 8, 14, 10),
        };
        SetRow(_footer, 1);
        Children.Add(_footer);

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        FrameworkElement? quick = null;
        try
        {
            // First preview of a file pays the Office export. Show the cheap text snapshot
            // immediately and swap in the real page when it lands.
            if (!OfficePageRenderer.IsCached(_path))
            {
                _footer.Text = "正在生成第 1 页…";
                if (_provisionalFirst && _fallback is not null)
                {
                    try
                    {
                        FrameworkElement? provisional = _fallback();
                        if (provisional is not null)
                        {
                            SetRow(provisional, 0);
                            Children.Add(provisional);
                            _frame.Visibility = Visibility.Collapsed;
                            _status.Visibility = Visibility.Collapsed;
                            _footer.Text = "正在生成整页预览…";
                        }
                    }
                    catch (Exception ex) { Log.Exception("office-provisional", ex); }
                }
            }

            BitmapSource? page = await OfficePageRenderer.GetPageAsync(_path, _captureWidth, _captureHeight);
            if (page is null)
            {
                if (quick is null) Swap(fallbackReason: "Office 无法导出此文件");
                return;
            }

            if (quick is not null) Children.Remove(quick);

            for (int i = Children.Count - 1; i >= 0; i--)
            {
                UIElement child = Children[i];
                if (child != _frame && child != _status && child != _footer) Children.RemoveAt(i);
            }
            // size the frame to the page's own aspect (within the box) so no bars appear
            double scale = Math.Min(_boxWidth / page.PixelWidth, _boxHeight / page.PixelHeight);
            if (scale > 1.0) scale = 1.0;
            _frame.Width = Math.Max(1, page.PixelWidth * scale);
            _frame.Height = Math.Max(1, page.PixelHeight * scale);
            Width = _frame.Width;
            Height = _frame.Height;

            _image.Source = page;
            _status.Visibility = Visibility.Collapsed;
            _frame.Visibility = Visibility.Visible;

            long size = 0;
            try { size = new FileInfo(_path).Length; } catch { }
            _footer.Text = $"第 1 页 · {page.PixelWidth}×{page.PixelHeight} · {PreviewBuilder.FormatSize(size)}";
        }
        catch (Exception ex)
        {
            Log.Exception("office-page-view", ex);
            if (quick is null) Swap("无法预览：" + ex.Message);
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    /// <summary>Falls back to the shell handler, then to the self-built page snapshot.</summary>
    private void Swap(string fallbackReason)
    {
        try
        {
            if (Interop.PreviewHandlerInterop.ResolveClsid(Path.GetExtension(_path).ToLowerInvariant()) is not null)
            {
                var handler = new OfficePreview(_path, _boxWidth, _boxHeight);
                SetRow(handler, 0);
                Children.Add(handler);
                _status.Visibility = Visibility.Collapsed;
                _footer.Text = "第 1 页（系统预览组件）";
                Log.Write($"office-page: {Path.GetFileName(_path)} -> handler ({fallbackReason})");
                return;
            }
        }
        catch (Exception ex) { Log.Exception("office-page-handler", ex); }

        FrameworkElement? alternative = null;
        try { alternative = _fallback?.Invoke(); } catch (Exception ex) { Log.Exception("office-page-fallback", ex); }

        if (alternative is not null)
        {
            SetRow(alternative, 0);
            Children.Add(alternative);
            _status.Visibility = Visibility.Collapsed;
            Log.Write($"office-page: {Path.GetFileName(_path)} -> snapshot ({fallbackReason})");
            return;
        }

        _status.Text = fallbackReason;
    }
}






