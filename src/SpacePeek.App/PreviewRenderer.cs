using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpacePeek.Hud;
using SpacePeek.Interop;

namespace SpacePeek;

/// <summary>
/// Headless preview renderer: builds the HUD off-screen and writes a PNG.
/// Used to check layout without hijacking the user's desktop.
/// Usage: SpacePeek.exe --render &lt;folder-or-file&gt; &lt;out.png&gt; [width]
/// </summary>
internal static class PreviewRenderer
{
    /// <summary>Measures content construction + layout for each file, without any window.</summary>
    public static void Bench(IEnumerable<string> targets)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.Startup += (_, _) =>
        {
            foreach (string target in targets)
            {
                try
                {
                    bool isFolder = Directory.Exists(target);
                    var item = new SelectedItem(target, Path.GetFileName(target.TrimEnd('\\')), isFolder);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    FrameworkElement? content = PreviewBuilder.ForSelection(new[] { item });
                    long build = sw.ElapsedMilliseconds;
                    if (content is null) { Console.WriteLine($"{Path.GetFileName(target)}: no content"); continue; }

                    sw.Restart();
                    content.Measure(new Size(660, 720));
                    content.Arrange(new Rect(0, 0, 660, 720));
                    long layout = sw.ElapsedMilliseconds;

                    Console.WriteLine($"{Path.GetFileName(target),-46} build={build,5}ms layout={layout,4}ms " +
                                      $"size={content.DesiredSize.Width:F0}x{content.DesiredSize.Height:F0} " +
                                      $"type={content.GetType().Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{Path.GetFileName(target)}: FAILED {ex.GetType().Name} {ex.Message}");
                }
            }
            app.Shutdown();
        };

        app.Run();
    }

    /// <summary>Checks whether the shell has a usable preview handler for a file.</summary>
    public static void Probe(IEnumerable<string> targets)
    {
        foreach (string target in targets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var handler = SpacePeek.Interop.PreviewHandlerInterop.Create(target);
                sw.Stop();
                Console.WriteLine($"{Path.GetFileName(target),-50} handler OK  {sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"{Path.GetFileName(target),-50} handler FAILED 0x{ex.HResult:X8} " +
                                  $"{sw.ElapsedMilliseconds}ms {ex.Message}");
            }
        }
    }

    /// <summary>Times the Office page export path (cold start vs warm instance).</summary>
    public static void BenchOffice(IEnumerable<string> targets)
    {
        foreach (string target in targets)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool cached = OfficePageRenderer.IsCached(target);
            var page = OfficePageRenderer.GetPageAsync(target).GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"{Path.GetFileName(target),-42} {(cached ? "cached" : "export"),-7} " +
                              $"{sw.ElapsedMilliseconds,6}ms {(page is null ? "FAILED" : $"{page.PixelWidth}x{page.PixelHeight}")}");
        }
        OfficePageRenderer.Shutdown();
    }

    /// <summary>Dumps every visible text block with its position, to debug layout.</summary>


    public static void Dump(string target)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                bool isFolder = Directory.Exists(target);
                var item = new SelectedItem(target, Path.GetFileName(target.TrimEnd('\\')), isFolder);
                FrameworkElement? content = PreviewBuilder.ForSelection(new[] { item });
                if (content is null) { Console.WriteLine("no content"); app.Shutdown(); return; }
                content.Measure(new Size(1400, 800));
                content.Arrange(new Rect(0, 0, 1400, 800));
                content.UpdateLayout();
                Walk(content, 0);
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "spacepeek-dump.txt"), DumpOut.ToString());
            }
            catch (Exception ex) { Console.WriteLine("dump failed: " + ex.Message); }
            finally { app.Shutdown(); }
        };
        app.Run();
    }

    private static readonly System.Text.StringBuilder DumpOut = new();

    private static void Walk(DependencyObject node, int depth)
    {
        if (node is System.Windows.Controls.TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
        {
            try
            {
                Point p = tb.TransformToAncestor((Visual)System.Windows.Media.VisualTreeHelper.GetParent(tb)).Transform(new Point(0, 0));
                DumpOut.AppendLine($"  text [{tb.Text[..Math.Min(24, tb.Text.Length)]}] " +
                                  $"w={tb.ActualWidth:F0} left={p.X:F0} font={tb.FontSize:F0} align={tb.TextAlignment}");
            }
            catch { }
        }

        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count && depth < 40; i++)
            Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
    }

    public static void RenderThumbnail(string target, string output, int size = 1024)
    {
        BitmapSource? thumb = ShellThumbnail.TryGet(target, size);
        if (thumb is null)
        {
            Console.WriteLine("no thumbnail");
            return;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(thumb));
        using (var fs = File.Create(output)) encoder.Save(fs);
        Console.WriteLine($"thumb {thumb.PixelWidth}x{thumb.PixelHeight} -> {output}");
    }

    public static void Render(string target, string output, double width = 660)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.Startup += async (_, _) =>
        {
            try
            {
                bool isFolder = Directory.Exists(target);
                var item = new SelectedItem(target, System.IO.Path.GetFileName(target), isFolder);
                FrameworkElement? content = PreviewBuilder.ForSelection(new[] { item });
                if (content is null) { Console.WriteLine("no content"); app.Shutdown(); return; }

                var hud = new HudWindow { Left = -6000, Top = -6000, Width = width };
                hud.Show();
                await hud.PresentAsync(content, IntPtr.Zero, null);

                await Task.Delay(700);
                if (Environment.GetEnvironmentVariable("SPACEPEEK_RENDER_EXPAND") == "1"
                    && content is FolderPreview folder)
                {
                    await folder.ExpandFirstAsync();
                }

                // let async previews (Office export, PDF page) finish before capturing
                await Task.Delay(content is IDeferredPreview ? 6000 : 1200);
                hud.Left = -6000;   // Present() re-anchors; keep this window off-screen
                hud.Top = -6000;

                double scale = VisualTreeHelper.GetDpi(hud).DpiScaleX;
                int w = (int)Math.Ceiling(hud.ActualWidth * scale);
                int h = (int)Math.Ceiling(hud.ActualHeight * scale);

                var rtb = new RenderTargetBitmap(Math.Max(w, 1), Math.Max(h, 1), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rtb.Render(hud);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(output)) encoder.Save(fs);

                Console.WriteLine($"rendered {w}x{h} -> {output}");
                hud.Hide();
                hud.Close();
            }
            catch (Exception ex) { Console.WriteLine("render failed: " + ex); }
            finally { app.Shutdown(); }
        };

        app.Run();
    }
}








