using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePeek.Interop;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SpacePeek.Hud;

/// <summary>
/// Video preview: the first frame. Uses the OS media stack (MediaComposition) so no decoder
/// ships with the app, then falls back to the shell thumbnail.
/// </summary>
internal sealed class VideoPreview : Grid, IDeferredPreview
{
    private static readonly string[] Extensions =
        { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".mpg", ".mpeg", ".ts", ".flv" };

    public static bool Handles(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    private const double BoxWidth = 960;
    private const double BoxHeight = 540;

    private readonly string _path;
    private readonly Border _frame;
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly TextBlock _footer;

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ReadyTask => _ready.Task;

    public VideoPreview(string path)
    {
        _path = path;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _frame = new Border
        {
            Width = BoxWidth,
            Height = BoxHeight,
            Background = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _image = new Image { Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        _frame.Child = _image;
        SetRow(_frame, 0);
        Children.Add(_frame);

        _status = new TextBlock
        {
            Text = "正在取第一帧…",
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

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            BitmapSource? frame = await Task.Run(() => FirstFrame(_path));
            Log.Write($"video: {Path.GetFileName(_path)} frame in {sw.ElapsedMilliseconds}ms");
            frame ??= await Task.Run(() => ShellThumbnail.TryGet(_path, 1024));

            if (frame is null)
            {
                _status.Text = "无法解码此视频";
                _status.Visibility = Visibility.Visible;
                _frame.Visibility = Visibility.Collapsed;
                return;
            }

            _image.Source = frame;
            _footer.Text = $"{frame.PixelWidth}×{frame.PixelHeight} · " +
                           $"{PreviewBuilder.FormatSize(new FileInfo(_path).Length)}";
        }
        catch (Exception ex)
        {
            Log.Exception("video", ex);
            _status.Text = "无法预览视频：" + ex.Message;
            _status.Visibility = Visibility.Visible;
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    private static BitmapSource? FirstFrame(string path)
    {
        try
        {
            StorageFile file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            MediaClip clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            using IRandomAccessStreamWithContentType stream = composition
                .GetThumbnailAsync(TimeSpan.Zero, (int)BoxWidth * 2, (int)BoxHeight * 2,
                    VideoFramePrecision.NearestFrame)
                .AsTask().GetAwaiter().GetResult();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream.AsStreamForRead();
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Write($"video: first frame failed {ex.Message}");
            return null;
        }
    }
}

