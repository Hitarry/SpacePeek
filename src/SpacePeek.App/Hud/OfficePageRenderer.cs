using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SpacePeek.Hud;

/// <summary>
/// Renders page 1 of a Word/PowerPoint document as an image by asking Office itself to export it.
/// A preview handler paints a 100%-scale page into a child HWND, which cannot be scaled and gets
/// clipped; a rendered page image can be fitted to the window, which is what a preview needs.
/// Results are cached per file (path + size + mtime) so only the first preview pays the cost.
/// </summary>
internal static class OfficePageRenderer
{
    private static readonly object WordGate = new();
    private static object? _wordApp;
    private static DateTime _wordLastUsed;
    private static readonly System.Threading.Timer IdleTimer = new(_ => ReleaseIdleWord(), null, 30_000, 30_000);

    /// <summary>Word takes ~1s to start; keep it warm between previews and drop it when idle.</summary>
    private static object? WordApplication()
    {
        lock (WordGate)
        {
            try
            {
                if (_wordApp is not null)
                {
                    _wordLastUsed = DateTime.UtcNow;
                    return _wordApp;
                }

                Type? type = Type.GetTypeFromProgID("Word.Application");
                if (type is null) return null;

                _wordApp = Activator.CreateInstance(type);
                dynamic app = _wordApp!;
                app.Visible = false;
                app.DisplayAlerts = 0;
                _wordLastUsed = DateTime.UtcNow;
                Log.Write("word: application started");
                return _wordApp;
            }
            catch (Exception ex)
            {
                Log.Exception("word-start", ex);
                _wordApp = null;
                return null;
            }
        }
    }

    private static void ReleaseIdleWord()
    {
        lock (WordGate)
        {
            if (_wordApp is null) return;
            if ((DateTime.UtcNow - _wordLastUsed).TotalSeconds < 20) return;

            try
            {
                dynamic app = _wordApp;
                app.Quit(false);
            }
            catch { }
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(_wordApp); } catch { }
            _wordApp = null;
            Log.Write("word: application released after idle");
        }
    }

    public static void Shutdown()
    {
        _wordLastUsed = DateTime.MinValue;
        ReleaseIdleWord();
    }

    public static string CacheDirectory
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpacePeek", "cache");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static bool IsCached(string path, int width = 900, int height = 1400)
    {
        try { return File.Exists(CachePath(path, width, height)); } catch { return false; }
    }

    public static Task<BitmapSource?> GetPageAsync(string path, int captureWidth = 816, int captureHeight = 1056)
        => Task.Run(() => GetPage(path, captureWidth, captureHeight));

    private static BitmapSource? GetPage(string path, int captureWidth, int captureHeight)
    {
        try
        {
            string cache = CachePath(path, captureWidth, captureHeight);
            if (File.Exists(cache)) return LoadPng(cache);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool isSheet = ext is ".xls" or ".xlsm" or ".xlsb";
            bool isSlide = ext is ".ppt" or ".pptx";

            // Spreadsheets and slides never use the capture path: those handlers paint 100% scale
            // silently drops the columns past its viewport, so K-column previews looked "cut".
            if (!isSheet && !isSlide)
            {
                BitmapSource? captured = HandlerPageCapture.CaptureAsync(path, captureWidth, captureHeight, cropLetterbox: isSlide).GetAwaiter().GetResult();
                if (captured is not null)
                {
                    SavePng(captured, cache);
                    Log.Write($"office-page: {Path.GetFileName(path)} -> handler capture");
                    return captured;
                }
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            string? produced = extension switch
            {
                ".doc" or ".docx" => ExportWordPdf(path),
                ".xls" or ".xlsx" or ".xlsm" or ".xlsb" => ExportExcelPdf(path),
                _ => ExportSlidePng(path),
            };
            if (produced is null)
            {
                // slides: fall back to the handler capture so the preview is never empty
                BitmapSource? captured = HandlerPageCapture.CaptureAsync(path, captureWidth, captureHeight)
                    .GetAwaiter().GetResult();
                if (captured is not null)
                {
                    SavePng(captured, cache);
                    Log.Write($"office-page: {Path.GetFileName(path)} -> fallback capture");
                }
                return captured;
            }

            BitmapSource? image = extension is ".doc" or ".docx" or ".xls" or ".xlsx" or ".xlsm" or ".xlsb"
                ? RenderPdfPage(produced, 1500)
                : LoadPng(produced);

            if (image is not null) SavePng(image, cache);
            Log.Write($"office-page: {Path.GetFileName(path)} -> {image?.PixelWidth}x{image?.PixelHeight}");
            TryDelete(produced);
            return image;
        }
        catch (Exception ex)
        {
            Log.Exception("office-page", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ Word
    private static string? ExportWordPdf(string docPath)
    {
        string pdf = Path.Combine(Path.GetTempPath(), "spacepeek-" + Guid.NewGuid().ToString("N") + ".pdf");
        object? application = null;
        object? document = null;

        try
        {
            application = WordApplication();
            if (application is null) return null;

            dynamic app = application;
            document = app.Documents.Open(docPath, false, true, false);
            dynamic doc = document!;
            // ExportFormat=17 (PDF), Range=3 (wdExportFromTo), From=1, To=1
            doc.ExportAsFixedFormat(pdf, 17, false, 0, 3, 1, 1);
            doc.Close(false);
            document = null;

            return File.Exists(pdf) ? pdf : null;
        }
        catch (Exception ex)
        {
            Log.Exception("word-export", ex);
            return null;
        }
        finally
        {
            try
            {
                if (document is not null) { dynamic doc = document; doc.Close(false); }
            }
            catch { }
            // the shared Word instance stays warm; it is released by the idle timer
        }
    }

    // ----------------------------------------------------------------- Excel
    private static string? ExportExcelPdf(string bookPath)
    {
        string pdf = Path.Combine(Path.GetTempPath(), "spacepeek-" + Guid.NewGuid().ToString("N") + ".pdf");
        object? application = null;
        object? workbook = null;

        try
        {
            Type? type = Type.GetTypeFromProgID("Excel.Application");
            if (type is null) return null;

            application = Activator.CreateInstance(type);
            dynamic app = application!;
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;

            workbook = app.Workbooks.Open(bookPath, 0, true);
            dynamic wb = workbook!;
            dynamic sheet = wb.Worksheets[1];
            sheet.ExportAsFixedFormat(0, pdf);   // 0 = xlTypePDF
            wb.Close(false);
            workbook = null;

            return File.Exists(pdf) ? pdf : null;
        }
        catch (Exception ex)
        {
            Log.Exception("excel-export", ex);
            return null;
        }
        finally
        {
            try { if (workbook is not null) { dynamic wb = workbook; wb.Close(false); } } catch { }
            try { if (application is not null) { dynamic app = application; app.Quit(); } } catch { }
            if (application is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(application);
        }
    }

    // ------------------------------------------------------------ PowerPoint
    private static string? ExportSlidePng(string pptPath)
    {
        string png = Path.Combine(Path.GetTempPath(), "spacepeek-" + Guid.NewGuid().ToString("N") + ".png");
        object? application = null;
        object? presentation = null;

        try
        {
            Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
            if (type is null) return null;

            application = Activator.CreateInstance(type);
            dynamic app = application!;
            presentation = app.Presentations.Open(pptPath, true, true, false);   // ReadOnly, Untitled, WithWindow
            dynamic pres = presentation!;
            dynamic slide = pres.Slides[1];
            slide.Export(png, "PNG", 1280, 720);
            pres.Close();
            presentation = null;

            return File.Exists(png) ? png : null;
        }
        catch (Exception ex)
        {
            Log.Exception("ppt-export", ex);
            return null;
        }
        finally
        {
            try
            {
                if (presentation is not null) { dynamic pres = presentation; pres.Close(); }
            }
            catch { }
            try
            {
                if (application is not null) { dynamic app = application; app.Quit(); }
            }
            catch { }
            if (application is not null) System.Runtime.InteropServices.Marshal.ReleaseComObject(application);
        }
    }

    // ---------------------------------------------------------------- render
    private static BitmapSource? RenderPdfPage(string pdfPath, uint width)
    {
        try
        {
            StorageFile file = StorageFile.GetFileFromPathAsync(pdfPath).AsTask().GetAwaiter().GetResult();
            PdfDocument document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            if (document.PageCount == 0) return null;

            using PdfPage page = document.GetPage(0);
            using var stream = new InMemoryRandomAccessStream();
            page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = width })
                .AsTask().GetAwaiter().GetResult();

            stream.Seek(0);
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
            Log.Exception("office-page-pdf", ex);
            return null;
        }
    }

    private static BitmapSource LoadPng(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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
        catch (Exception ex) { Log.Exception("office-page-cache", ex); }
    }

    private static string CachePath(string path, int width, int height)
    {
        var info = new FileInfo(path);
        string stamp = $"v3|{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{width}x{height}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(stamp));
        return Path.Combine(CacheDirectory, Convert.ToHexString(hash, 0, 12) + ".png");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}













