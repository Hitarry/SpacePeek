using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SpacePeek.Interop;

namespace SpacePeek.Hud;

/// <summary>
/// Renders page 1 of a document through the shell preview handler (what Explorer's preview pane
/// uses), then captures that window into a bitmap. Launching Word takes seconds; prevhost takes
/// a few hundred milliseconds, and a captured bitmap can be scaled to fit — which a live child
/// HWND cannot.
/// </summary>
internal static class HandlerPageCapture
{
    private const int PageWidth = 816;      // A4 at 96 dpi
    private const int PageHeight = 1056;
    private const int SettleMs = 450;
    private const uint PM_REMOVE = 1;

    public static Task<BitmapSource?> CaptureAsync(string path, int width = PageWidth, int height = PageHeight,
        bool cropLetterbox = false)
    {
        BitmapSource? result = null;
        var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { result = Capture(path, width, height, cropLetterbox); }
            catch (Exception ex) { Log.Exception("handler-capture", ex); }
            finally { done.Set(); }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        done.Wait(6000);
        return Task.FromResult(result);
    }

    private static BitmapSource? Capture(string path, int width, int height, bool cropLetterbox)
    {
        string cache = CachePath(path, width, height);
        if (File.Exists(cache))
        {
            try
            {
                var cached = new BitmapImage();
                cached.BeginInit();
                cached.CacheOption = BitmapCacheOption.OnLoad;
                cached.UriSource = new Uri(cache, UriKind.Absolute);
                cached.EndInit();
                cached.Freeze();
                return cached;
            }
            catch { }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        IntPtr hwnd = CreateWindowEx(0, "static", "",
            unchecked((int)0x90000000),   // WS_POPUP | WS_VISIBLE
            -20000, -20000, width, height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return null;

        PreviewHandlerInterop.IPreviewHandler? handler = null;
        try
        {
            handler = PreviewHandlerInterop.Create(path);

            var rect = new PreviewHandlerInterop.RECTI { Left = 0, Top = 0, Right = width, Bottom = height };
            handler.SetWindow(hwnd, ref rect);
            handler.DoPreview();

            // Handlers paint asynchronously; retry until something other than a blank page lands.
            BitmapSource? image = null;
            foreach (int wait in new[] { 250, 350, 600, 900, 1400 })
            {
                Pump(wait);
                image = CaptureWindow(hwnd, cropLetterbox);
                if (image is not null) break;
            }
            if (image is null) return null;

            Log.Write($"handler-capture: {Path.GetFileName(path)} {image.PixelWidth}x{image.PixelHeight} " +
                      $"in {sw.ElapsedMilliseconds}ms");
            SavePng(image, cache);
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"handler-capture: {Path.GetFileName(path)} failed {ex.Message}");
            return null;
        }
        finally
        {
            try { handler?.Unload(); } catch { }
            if (handler is not null && Marshal.IsComObject(handler)) Marshal.ReleaseComObject(handler);
            DestroyWindow(hwnd);
        }
    }

    private static void Pump(int milliseconds)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < end)
        {
            while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            Thread.Sleep(15);
        }
    }

    private static BitmapSource? CaptureWindow(IntPtr hwnd, bool cropLetterbox)
    {
        if (!GetWindowRect(hwnd, out RECT rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr hdc = graphics.GetHdc();
            try
            {
                // PW_RENDERFULLCONTENT: needed for DirectComposition-based handlers
                if (!PrintWindow(hwnd, hdc, 2)) return null;
            }
            finally { graphics.ReleaseHdc(hdc); }
        }

        IntPtr hbitmap = bitmap.GetHbitmap();
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            if (IsBlank(source)) return null;

            // slides are letterboxed in black inside the capture window: crop that away
            if (cropLetterbox) source = CropBlackBorders(source) ?? source;

            // trim the handler's own scrollbar strip and its bottom chrome
            int cropW = Math.Max(1, source.PixelWidth - 20);
            int cropH = Math.Max(1, source.PixelHeight - 46);
            var cropped = new CroppedBitmap(source, new Int32Rect(0, 0, cropW, cropH));
            cropped.Freeze();
            return cropped;
        }
        finally { DeleteObject(hbitmap); }
    }

    /// <summary>A handler that failed to paint leaves a pure-white page; treat that as "no image".</summary>
    private static bool IsBlank(BitmapSource source)
    {
        try
        {
            // Sample the middle band: a page's top margin is legitimately blank.
            int bandHeight = Math.Max(1, source.PixelHeight / 3);
            int offset = (source.PixelHeight - bandHeight) / 2;
            int stride = source.PixelWidth * 4;
            var pixels = new byte[stride * bandHeight];
            source.CopyPixels(new Int32Rect(0, offset, source.PixelWidth, bandHeight), pixels, stride, 0);

            long nonWhite = 0;
            for (int i = 0; i < pixels.Length; i += 4)
                if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240) nonWhite++;

            return nonWhite < pixels.Length / 4 / 1000;   // <0.1% ink in the page body = blank
        }
        catch { return false; }
    }

    /// <summary>Removes the fully-black letterbox a slide handler paints around the page.</summary>
    private static BitmapSource? CropBlackBorders(BitmapSource source)
    {
        try
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            source.CopyPixels(pixels, stride, 0);

            int top = 0, bottom = h - 1, left = 0, right = w - 1;
            bool RowIsBlack(int y)
            {
                for (int x = 0; x < w; x += 2)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] > 18 || pixels[i + 1] > 18 || pixels[i + 2] > 18) return false;
                }
                return true;
            }
            bool ColIsBlack(int x)
            {
                for (int y = 0; y < h; y += 2)
                {
                    int i = y * stride + x * 4;
                    if (pixels[i] > 18 || pixels[i + 1] > 18 || pixels[i + 2] > 18) return false;
                }
                return true;
            }

            while (top < h / 3 && RowIsBlack(top)) top++;
            while (bottom > h * 2 / 3 && RowIsBlack(bottom)) bottom--;
            while (left < w / 3 && ColIsBlack(left)) left++;
            while (right > w * 2 / 3 && ColIsBlack(right)) right--;

            if (right - left < 40 || bottom - top < 40) return null;

            // a fully black capture means the handler never painted: treat it as a failure
            long sum = 0;
            int samples = 0;
            for (int y = top; y <= bottom; y += 6)
            {
                for (int x = left; x <= right; x += 6)
                {
                    int i = y * stride + x * 4;
                    sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
                    samples++;
                }
            }
            if (samples == 0 || sum / samples / 3 < 25) return null;
            if (left == 0 && top == 0 && right == w - 1 && bottom == h - 1) return null;

            // slide renders keep a 1-2 px handler border; pull in a touch more
            bool wide = (right - left) > (bottom - top) * 1.4;
            int pad = wide ? 4 : 0;
            int topPad = wide ? 10 : 0;   // the handler paints its own caption strip on slides
            left += pad; top += topPad; right -= pad; bottom -= pad;

            var cropped = new CroppedBitmap(source,
                new Int32Rect(left, top, Math.Max(1, right - left + 1), Math.Max(1, bottom - top + 1)));
            cropped.Freeze();
            return cropped;
        }
        catch { return null; }
    }

    private static string CachePath(string path, int width, int height)
    {
        var info = new FileInfo(path);
        string stamp = $"v2|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{width}x{height}";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stamp));
        return Path.Combine(OfficePageRenderer.CacheDirectory,
            "cap-" + Convert.ToHexString(hash, 0, 12) + ".png");
    }

    private static void SavePng(BitmapSource image, string path)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(path);
            encoder.Save(fs);
        }
        catch { }
    }

    // ------------------------------------------------------------------ win32
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}














