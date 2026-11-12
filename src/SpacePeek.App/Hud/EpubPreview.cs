using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml.Linq;

namespace SpacePeek.Hud;

/// <summary>
/// EPUB preview: opens the package, follows container → OPF → spine and renders the text of the
/// first chapter(s) as a book page. No browser engine, no layout engine — just the reading text.
/// </summary>
internal static class EpubPreview
{
    private static readonly XNamespace Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";

    private const double PageWidth = 560;
    private const double PageHeight = 760;

    public static FrameworkElement? Build(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);
        string? opfPath = FindOpf(zip);
        if (opfPath is null) return Fallback(zip);

        ZipArchiveEntry? opfEntry = zip.GetEntry(opfPath);
        if (opfEntry is null) return null;

        XDocument opf;
        using (Stream s = opfEntry.Open()) opf = XDocument.Load(s);

        string baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
        var manifest = new Dictionary<string, string>();
        foreach (XElement item in opf.Descendants(Opf + "item"))
        {
            string? id = (string?)item.Attribute("id");
            string? href = (string?)item.Attribute("href");
            if (id is not null && href is not null) manifest[id] = href;
        }

        var panel = new StackPanel();
        string? title = opf.Descendants(Opf + "title").FirstOrDefault()?.Value;

        if (!string.IsNullOrWhiteSpace(title))
        {
            panel.Children.Add(new TextBlock
            {
                Text = title.Trim(),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
            });
        }

        int rendered = 0;
        foreach (XElement itemRef in opf.Descendants(Opf + "itemref"))
        {
            if (rendered >= 2) break;
            string? idref = (string?)itemRef.Attribute("idref");
            if (idref is null || !manifest.TryGetValue(idref, out string? href)) continue;

            string entryPath = Join(baseDir, href);
            ZipArchiveEntry? entry = zip.GetEntry(entryPath);
            if (entry is null) continue;

            string html;
            using (Stream s = entry.Open())
            using (var reader = new StreamReader(s)) html = reader.ReadToEnd();

            List<(string text, int level)> blocks = ExtractBlocks(html);
            if (blocks.Count == 0) continue;

            foreach ((string text, int level) in blocks)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = level switch { 1 => 18, 2 => 16, 3 => 14, _ => 13 },
                    FontWeight = level > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = level > 0 ? 26 : 22,
                    Margin = new Thickness(0, level > 0 ? 12 : 6, 0, level > 0 ? 8 : 6),
                });
            }
            rendered++;
        }

        if (rendered == 0) return Fallback(zip);

        if (panel.Children.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "（这本 EPUB 没有可提取的文字）",
                Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Width = PageWidth,
            Height = PageHeight,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new Border { Padding = new Thickness(34, 26, 26, 18), Child = panel },
        };
    }

    /// <summary>Last resort: any XHTML/HTML entry, in spine-less books.</summary>
    private static ZipArchiveEntry? FirstContentEntry(ZipArchive zip) =>
        zip.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));

    /// <summary>Renders the first HTML-ish entry when the package metadata is unusable.</summary>
    private static FrameworkElement? Fallback(ZipArchive zip)
    {
        ZipArchiveEntry? entry = FirstContentEntry(zip);
        if (entry is null) return null;

        string html;
        using (Stream s = entry.Open())
        using (var reader = new StreamReader(s)) html = reader.ReadToEnd();

        var panel = new StackPanel();
        foreach ((string text, int level) in ExtractBlocks(html))
        {
            panel.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = level > 0 ? 16 : 13,
                FontWeight = level > 0 ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                Margin = new Thickness(0, level > 0 ? 10 : 5, 0, level > 0 ? 6 : 5),
            });
        }

        if (panel.Children.Count == 0) return null;

        return new Border
        {
            Width = PageWidth,
            Height = PageHeight,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new Border { Padding = new Thickness(34, 26, 26, 18), Child = panel },
        };
    }

    private static string? FindOpf(ZipArchive zip)
    {
        try
        {
            ZipArchiveEntry? container = zip.GetEntry("META-INF/container.xml");
            if (container is not null)
            {
                using Stream s = container.Open();
                XDocument doc = XDocument.Load(s);
                string? fullPath = doc.Descendants(Container + "rootfile")
                    .Select(x => (string?)x.Attribute("full-path"))
                    .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(fullPath)) return fullPath;
            }
        }
        catch { }

        return zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase))?.FullName;
    }

    private static string Join(string baseDir, string href)
    {
        string combined = string.IsNullOrEmpty(baseDir) ? href : baseDir + "/" + href;
        while (combined.Contains("/../"))
        {
            int i = combined.IndexOf("/../", StringComparison.Ordinal);
            int prev = combined.LastIndexOf('/', i - 1);
            combined = prev < 0 ? combined[(i + 4)..] : combined[..prev] + combined[(i + 3)..];
        }
        return combined.TrimStart('/');
    }

    /// <summary>Turns an XHTML chapter into paragraph-sized chunks with a heading level per chunk.</summary>
    private static List<(string, int)> ExtractBlocks(string html)
    {
        var result = new List<(string, int)>();
        string body = Regex.Replace(html, @"(?is)<(script|style|head).*?</\1>", " ");
        body = Regex.Replace(body, @"(?i)<br\s*/?>", "\n");
        body = Regex.Replace(body, @"(?i)</(p|div|li|tr|h[1-6]|section|article)>", "\n");
        body = Regex.Replace(body, @"(?i)<h([1-6])[^>]*>", "\u0001$1\u0001");

        foreach (string raw in body.Split('\n'))
        {
            string line = raw;
            int level = 0;
            Match heading = Regex.Match(line, "^\u0001([1-6])\u0001");
            if (heading.Success)
            {
                level = int.Parse(heading.Groups[1].Value);
                line = line[heading.Length..];
            }

            string text = WebUtility.HtmlDecode(Regex.Replace(line, "<[^>]+>", " "));
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length < 2) continue;

            result.Add((text, level));
            if (result.Count >= 80) break;
        }

        return result;
    }
}

