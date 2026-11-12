using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SpacePeek.Hud;

/// <summary>
/// Renders page 1 of a PDF with the OS PDF engine (the same one Edge uses) and sizes the
/// window to the page's own aspect — portrait for A4/Letter, landscape for slides.
/// Implements IDeferredPreview so the HUD shows nothing until the real page size is known.
/// </summary>
internal sealed class PdfPreview : Grid, IDeferredPreview
{
    private const double MaxPageWidth = 620;
    private const double MaxPageHeight = 720;

    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _text;
    private readonly TextBlock _footer;

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadyTask => _ready.Task;

    public PdfPreview(string path)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _frame.Child = _image;
        SetRow(_frame, 0);
        Children.Add(_frame);

        _text = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            FontSize = 13,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            Text = "正在渲染第 1 页…",
        };
        SetRow(_text, 0);
        Children.Add(_text);

        _footer = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            FontSize = 12,
            Margin = new Thickness(14, 8, 14, 10),
        };
        SetRow(_footer, 1);
        Children.Add(_footer);

        // Starts in the constructor: ReadyTask gates the HUD's Show().
        _ = LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            PdfDocument document;
            try
            {
                document = await PdfDocument.LoadFromFileAsync(file);
            }
            catch (Exception first)
            {
                // Books and exam PDFs very often carry an owner password with an empty user
                // password; retry with "" before giving up on them.
                try
                {
                    document = await PdfDocument.LoadFromFileAsync(file, string.Empty);
                }
                catch
                {
                    throw first;
                }
            }
            if (document.PageCount == 0)
            {
                Fail("PDF 没有页面");
                return;
            }

            using PdfPage page = document.GetPage(0);
            Windows.Foundation.Size pageSize = page.Size;

            double width = pageSize.Width, height = pageSize.Height;
            if (width <= 0 || height <= 0)
            {
                width = 595;
                height = 842;
            }

            double scale = Math.Min(MaxPageWidth / width, MaxPageHeight / height);
            double displayWidth = width * scale;
            double displayHeight = height * scale;

            _frame.Width = displayWidth;
            _frame.Height = displayHeight;
            _image.Width = displayWidth;
            _image.Height = displayHeight;

            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            uint destination = (uint)Math.Clamp(Math.Ceiling(displayWidth * dpi * 2.5), 800, 3200);

            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = destination };
            await page.RenderToStreamAsync(stream, options);

            stream.Seek(0);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream.AsStreamForRead();
            bitmap.EndInit();
            bitmap.Freeze();

            _image.Source = bitmap;
            _text.Visibility = Visibility.Collapsed;
            _frame.Visibility = Visibility.Visible;

            long bytes = 0;
            try { bytes = new FileInfo(path).Length; } catch { }
            Log.Write($"pdf: page1 {bitmap.PixelWidth}x{bitmap.PixelHeight} in {sw.ElapsedMilliseconds}ms");

            _footer.Text = $"第 1 / {document.PageCount} 页 · " +
                           $"{bitmap.PixelWidth}×{bitmap.PixelHeight} · {PreviewBuilder.FormatSize(bytes)}";
        }
        catch (Exception ex)
        {
            Log.Write($"pdf: failed 0x{ex.HResult:X8} {ex.GetType().Name} {ex.Message}");
            if (ex.HResult == unchecked((int)0x80048040))
                Fail("此 PDF 已加密，需要密码才能预览");
            else
                Fail("无法渲染 PDF：" + ex.Message);
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    private void Fail(string message)
    {
        _frame.Visibility = Visibility.Collapsed;
        _text.Text = message;
        _text.Visibility = Visibility.Visible;
        _footer.Text = "";
    }
}


