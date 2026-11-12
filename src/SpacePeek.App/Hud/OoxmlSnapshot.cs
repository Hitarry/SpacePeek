using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace SpacePeek.Hud;

/// <summary>
/// Builds an instant "page 1" snapshot of OOXML documents by reading the package directly.
/// No Office, no preview handler, no full-document layout — this is the fast path.
/// </summary>
internal static class OoxmlSnapshot
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly Brush PageBackground = new SolidColorBrush(Color.FromRgb(250, 250, 250));
    private static readonly Brush PageForeground = new SolidColorBrush(Color.FromRgb(32, 32, 32));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(120, 120, 120));

    public static bool Handles(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".docx" or ".xlsx" or ".pptx";
    }

    public static FrameworkElement? Build(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            FrameworkElement? result = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".docx" => BuildDocx(path),
                ".xlsx" => XlsxSnapshot.Build(path),
                ".pptx" => BuildPptx(path),
                _ => null,
            };
            sw.Stop();
            Log.Write($"ooxml: {Path.GetFileName(path)} parsed in {sw.Elapsed.TotalMilliseconds:F0} ms");
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log.Write($"ooxml: {Path.GetFileName(path)} failed after {sw.Elapsed.TotalMilliseconds:F0} ms");
            Log.Exception("ooxml", ex);
            return null;
        }
    }

    // ------------------------------------------------------------------ Word
    internal static FrameworkElement? BuildDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        ZipArchiveEntry? document = zip.GetEntry("word/document.xml");
        if (document is null) return null;

        using Stream stream = document.Open();
        XDocument xml = XDocument.Load(stream);

        var panel = new StackPanel { Margin = new Thickness(36, 30, 36, 30) };
        int paragraphs = 0;

        foreach (XElement paragraph in xml.Descendants(W + "p"))
        {
            if (paragraphs++ >= 70) break;
            string text = string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value)).Trim();
            if (text.Length == 0) continue;

            string? style = paragraph.Descendants(W + "pStyle")
                .Select(s => (string?)s.Attribute(W + "val"))
                .FirstOrDefault();

            bool heading = style is not null && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase);
            panel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Foreground = PageForeground,
                FontSize = heading ? 18 : 13,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, heading ? 12 : 6, 0, heading ? 10 : 6),
                LineHeight = heading ? 26 : 21,
            });
        }

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（文档首页没有可提取的文字）",
                Foreground = Dim,
                FontSize = 13,
            });
        }

        return Page(panel, 509, 720);
    }

    // ---------------------------------------------------------------- Excel
    private static FrameworkElement? BuildXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);

        var shared = new List<string>();
        ZipArchiveEntry? strings = zip.GetEntry("xl/sharedStrings.xml");
        if (strings is not null)
        {
            using Stream s = strings.Open();
            foreach (XElement si in XDocument.Load(s).Descendants(S + "si"))
                shared.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        }

        ZipArchiveEntry? sheet = zip.GetEntry("xl/worksheets/sheet1.xml");
        if (sheet is null) return null;

        var rows = new List<List<string>>();
        using (Stream s = sheet.Open())
        {
            XDocument xml = XDocument.Load(s);
            foreach (XElement row in xml.Descendants(S + "row").Take(16))
            {
                var cells = new List<string>();
                foreach (XElement cell in row.Elements(S + "c").Take(12))
                {
                    int column = ColumnIndex((string?)cell.Attribute("r"));
                    while (cells.Count < column) cells.Add(string.Empty);

                    string type = (string?)cell.Attribute("t") ?? "";
                    string value;
                    switch (type)
                    {
                        case "s":   // shared string table index
                            value = int.TryParse(cell.Element(S + "v")?.Value, out int idx) && idx < shared.Count
                                ? shared[idx]
                                : string.Empty;
                            break;
                        case "inlineStr":   // <is><t>text</t></is>
                            value = string.Concat(cell.Descendants(S + "t").Select(t => t.Value));
                            break;
                        case "str":         // cached formula string
                            value = cell.Element(S + "v")?.Value ?? string.Empty;
                            break;
                        default:
                            value = cell.Element(S + "v")?.Value ?? string.Empty;
                            break;
                    }
                    cells.Add(value);
                }
                rows.Add(cells);
            }
        }

        if (rows.Count == 0) return null;

        int columns = rows.Max(r => r.Count);
        var grid = new Grid { Margin = new Thickness(18) };
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = rows[r][c],
                        FontSize = 12,
                        Foreground = PageForeground,
                        FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    },
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        return Page(grid, 620, 465);
    }

    // ----------------------------------------------------------- PowerPoint
    internal static FrameworkElement? BuildPptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        ZipArchiveEntry? slide = zip.GetEntry("ppt/slides/slide1.xml");
        if (slide is null) return null;

        using Stream s = slide.Open();
        var lines = XDocument.Load(s).Descendants(A + "t")
            .Select(t => t.Value.Trim())
            .Where(v => v.Length > 0)
            .Take(24)
            .ToList();

        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 30, 40, 30),
        };

        for (int i = 0; i < lines.Count; i++)
        {
            panel.Children.Add(new TextBlock
            {
                Text = lines[i],
                TextWrapping = TextWrapping.Wrap,
                Foreground = PageForeground,
                FontSize = i == 0 ? 26 : 15,
                FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, i == 0 ? 0 : 8, 0, 0),
            });
        }

        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "（第 1 张幻灯片没有文字）", Foreground = Dim, FontSize = 13 });

        return Page(panel, 620, 465);
    }

    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return 0;
        int index = 0;
        foreach (char ch in reference)
        {
            if (ch is >= 'A' and <= 'Z') index = index * 26 + (ch - 'A' + 1);
            else break;
        }
        return Math.Max(0, index - 1);
    }

    private static FrameworkElement Page(FrameworkElement content, double width, double height)
    {
        // No ScrollViewer on purpose: a document preview shows page 1, clipped at the page edge.
        return new Border
        {
            Width = width,
            Height = height,
            Background = PageBackground,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Child = content,
            ClipToBounds = true,
        };
    }
}



