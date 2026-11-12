using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SpacePeek.Hud;

/// <summary>
/// Renders HTML the way a browser does (WebView2 / Edge), not as source code.
/// Pointer and keyboard input are left alone: the preview is read-only in spirit, but the
/// page itself still renders exactly as authored.
/// </summary>
internal sealed class HtmlPreview : Grid
{
    private static readonly string[] Extensions = { ".html", ".htm", ".xhtml", ".mht", ".mhtml" };

    public static bool Handles(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    private readonly string _path;
    private readonly WebView2 _view;
    private readonly TextBlock _footer;
    private readonly TextBlock _status;
    private readonly double _boxWidth;
    private readonly double _boxHeight;



    public HtmlPreview(string path, double boxWidth = 900, double boxHeight = 700)
    {
        _path = path;
        _boxWidth = boxWidth;
        _boxHeight = boxHeight;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var frame = new Border
        {
            Width = boxWidth,
            Height = boxHeight,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };

        try
        {
            _view = new WebView2 { Width = boxWidth - 2, Height = boxHeight - 2 };
            frame.Child = _view;
        }
        catch (Exception ex)
        {
            Log.Exception("html-view", ex);
            _view = new WebView2 { Visibility = Visibility.Collapsed };
        }

        SetRow(frame, 0);
        Children.Add(frame);

        _status = new TextBlock
        {
            Text = "正在渲染网页…",
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            FontSize = 13,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
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

        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await _view.EnsureCoreWebView2Async(await CreateEnvironmentAsync());

            _view.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _view.CoreWebView2.Settings.IsZoomControlEnabled = false;

            _view.CoreWebView2.Navigate(new Uri(_path).AbsoluteUri);

            long size = 0;
            try { size = new FileInfo(_path).Length; } catch { }
            _footer.Text = $"{Path.GetExtension(_path).TrimStart('.').ToUpperInvariant()} 渲染 · " +
                           $"{PreviewBuilder.FormatSize(size)}";
        }
        catch (Exception ex)
        {
            Log.Exception("html", ex);
            Log.Write("html: fallback to source — " + ex.Message);
            ShowSource("无法渲染网页：" + ex.Message);
        }
        finally
        {
        }
    }

    /// <summary>Last-resort fallback: show the markup as text instead of an empty white box.</summary>
    private void ShowSource(string reason)
    {
        try
        {
            string text = File.ReadAllText(_path);
            if (text.Length > 200_000) text = text[..200_000];
            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Background = Brushes.White,
                Foreground = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
                BorderThickness = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Width = _boxWidth,
                Height = _boxHeight,
            };
            _view.Visibility = Visibility.Collapsed;
            SetRow(box, 0);
            Children.Add(box);
            _footer.Text = reason + "（显示源码）";
        }
        catch { }
    }

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SpacePeek", "webview");
        Directory.CreateDirectory(dataDir);
        return await CoreWebView2Environment.CreateAsync(null, dataDir);
    }
}

