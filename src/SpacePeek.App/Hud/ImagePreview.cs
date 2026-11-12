using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePeek.Hud;

/// <summary>
/// Static image preview. The HUD adopts the image's own aspect ratio and decodes only the
/// pixels actually shown — the goal is "opens instantly and is sharp enough", not pixel-perfect.
/// Animated GIF/WebP show the first frame only.
/// </summary>
internal sealed class ImagePreview : Grid
{
    private const int MaxDecodeWidth = 1200;
    private const long MaxBytes = 200L * 1024 * 1024;
    private const double MaxImageWidth = 660;
    private const double MaxImageHeight = 720;
    private const double MaxUpscale = 2.0;

    private static readonly string[] Extensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp" };

    public static bool Handles(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    private readonly Image _image;
    private readonly TextBlock _footer;
    private readonly double _displayWidth = MaxImageWidth;
    private readonly double _displayHeight = MaxImageHeight;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly ushort _orientation;

    public ImagePreview(string path)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(242, 242, 242)),
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.LowQuality);
        frame.Child = _image;
        SetRow(frame, 0);
        Children.Add(frame);

        _footer = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            FontSize = 12,
            Margin = new Thickness(14, 8, 14, 10),
            Text = "正在读取…",
        };
        SetRow(_footer, 1);
        Children.Add(_footer);

        // Probe the header synchronously: the HUD needs the aspect ratio before the decode finishes.
        try
        {
            var probe = BitmapFrame.Create(new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            _sourceWidth = probe.PixelWidth;
            _sourceHeight = probe.PixelHeight;
        }
        catch
        {
            _sourceWidth = 0;
            _sourceHeight = 0;
        }

        _orientation = ReadOrientation(path);
        int effectiveWidth = _sourceWidth;
        int effectiveHeight = _sourceHeight;
        if (_orientation is 5 or 6 or 7 or 8)
        {
            effectiveWidth = _sourceHeight;
            effectiveHeight = _sourceWidth;
        }

        if (effectiveWidth > 0 && effectiveHeight > 0)
        {
            double fit = Math.Min(MaxImageWidth / effectiveWidth, MaxImageHeight / effectiveHeight);
            fit = Math.Min(fit, MaxUpscale);
            _displayWidth = effectiveWidth * fit;
            _displayHeight = effectiveHeight * fit;
            _image.Width = _displayWidth;
            _image.Height = _displayHeight;
        }

        Loaded += (_, _) => Load(path);
    }

    private void Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
            {
                _footer.Text = $"文件过大（{PreviewBuilder.FormatSize(info.Length)}），未预览";
                return;
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);

            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int decodeWidth = (int)Math.Clamp(Math.Ceiling(_displayWidth * dpi), 128, MaxDecodeWidth);
            if (_sourceWidth == 0 || decodeWidth < _sourceWidth) bmp.DecodePixelWidth = decodeWidth;

            bmp.EndInit();
            bmp.Freeze();

            BitmapSource oriented = ApplyOrientation(bmp, path);
            _image.Source = oriented;

            string dims = $"{oriented.PixelWidth}×{oriented.PixelHeight}";
            _footer.Text = $"{dims} · {PreviewBuilder.FormatSize(info.Length)}";
        }
        catch (Exception ex)
        {
            _footer.Text = "无法读取图片：" + ex.Message;
        }
    }

    /// <summary>
    /// Phone photos carry the rotation in EXIF rather than in the pixels; WPF ignores it, which
    /// is why an upright photo could preview upside down.
    /// </summary>
    private static BitmapSource ApplyOrientation(BitmapSource source, string path)
    {
        try
        {
            ushort orientation = ReadOrientation(path);
            if (orientation is 0 or 1) return source;

            Transform transform = orientation switch
            {
                2 => new ScaleTransform(-1, 1),
                3 => new RotateTransform(180),
                4 => new ScaleTransform(1, -1),
                5 => FlipThenRotate(270),
                6 => new RotateTransform(270),
                7 => FlipThenRotate(90),
                8 => new RotateTransform(90),
                _ => Transform.Identity,
            };

            if (transform == Transform.Identity) return source;

            var result = new TransformedBitmap(source, transform);
            result.Freeze();
            return result;
        }
        catch
        {
            return source;
        }
    }

    private static Transform FlipThenRotate(double degrees)
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(-1, 1));
        group.Children.Add(new RotateTransform(degrees));
        return group;
    }

    private static ushort ReadOrientation(string path)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(path, UriKind.Absolute),
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            if (frame.Metadata is not BitmapMetadata metadata) return 1;
            object? value = metadata.GetQuery("/app1/ifd/{ushort=274}");
            return value is ushort u ? u : (ushort)1;
        }
        catch { return 1; }
    }
}



