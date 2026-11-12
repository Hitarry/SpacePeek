using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SpacePeek.Hud;

/// <summary>
/// Text preview. Two modes:
///  - code/plain files: monospace, scrollable, no horizontal bar (long lines wrap off);
///  - document mode (.md): an A4 page filled with as many lines as fit, no scrollbars at all —
///    the whole first page is shown at once.
/// </summary>
internal sealed class TextPreview : Grid
{
    private const int MaxBytes = 512 * 1024;
    private const int MaxLines = 5000;
    private const double PageWidth = 509;
    private const double PageHeight = 720;
    private const double PagePadding = 14;

    private static readonly string[] Extensions =
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".jsonc", ".xml", ".yml", ".yaml", ".ini",
        ".csv", ".tsv", ".cfg", ".conf", ".properties", ".toml", ".env", ".gitignore", ".editorconfig",
        ".cs", ".py", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".css", ".scss", ".html", ".htm",
        ".sh", ".bash", ".ps1", ".bat", ".cmd", ".sql", ".java", ".kt", ".c", ".h", ".cpp", ".hpp",
        ".go", ".rs", ".rb", ".php", ".lua", ".r", ".swift", ".vue", ".svelte", ".gradle", ".cmake",
        ".diff", ".patch", ".sln", ".csproj", ".props", ".targets", ".xaml", ".axaml", ".resx",
        ".rst", ".adoc", ".asciidoc", ".tex", ".org", ".textile", ".srt", ".vtt", ".ass", ".nfo",
        ".psm1", ".psd1", ".vbs", ".reg", ".jsonl", ".ndjson", ".lock", ".gitattributes", ".gitmodules",
        ".mk", ".make", ".mm", ".m", ".dart", ".groovy", ".scala", ".fs", ".fsx", ".vb", ".cshtml",
        ".razor", ".erb", ".ejs", ".hbs", ".mustache", ".j2", ".jinja", ".tpl", ".tmpl", ".ipynb",
        ".kts", ".tf", ".tfvars", ".hcl", ".proto", ".graphql", ".gql", ".zig", ".nim", ".jl", ".sol",
    };

    static TextPreview()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static bool Handles(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(Extensions, ext) >= 0) return true;
        if (ext.Length == 0)
        {
            string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
            if (name is "dockerfile" or "makefile" or "readme" or "license" or "changelog" or "authors" or "notice")
                return true;
            try { return new FileInfo(path).Length < 2 * 1024 * 1024; } catch { return false; }
        }
        return false;
    }

    private readonly TextBox? _text;
    private readonly TextBlock? _page;
    private readonly TextBlock _footer;

    public TextPreview(string path, bool pageMode = false)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (pageMode)
        {
            _page = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                FontSize = 13,
                LineHeight = 21,
                Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                Padding = new Thickness(PagePadding),
                Width = PageWidth,
                Height = PageHeight,
            };
            SetRow(_page, 0);
            Children.Add(_page);
        }
        else
        {
            _text = new TextBox
            {
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
                FontSize = 12.5,
                Foreground = new SolidColorBrush(Color.FromRgb(34, 34, 34)),
                Background = new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 10, 12, 10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinHeight = 120,
                MaxHeight = 460,
                Width = 620,
            };
            SetRow(_text, 0);
            Children.Add(_text);
        }

        _footer = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            FontSize = 12,
            Margin = new Thickness(14, 8, 14, 10),
            Text = "正在读取…",
        };
        SetRow(_footer, 1);
        Children.Add(_footer);

        Loaded += (_, _) => Load(path, pageMode);
    }

    private void Load(string path, bool pageMode)
    {
        try
        {
            var info = new FileInfo(path);
            byte[] buffer;
            bool truncated;

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int length = (int)Math.Min(info.Length, MaxBytes);
                buffer = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = fs.Read(buffer, read, length - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < buffer.Length) Array.Resize(ref buffer, read);
                truncated = info.Length > buffer.Length;
            }

            (Encoding encoding, bool fromBom) = DetectEncoding(buffer);
            string text = encoding.GetString(buffer, 0, buffer.Length);
            if (text.IndexOf('\0') >= 0) text = text.Replace("\0", "");

            string[] lines = text.Split('\n');
            int totalLines = lines.Length;
            bool linesCut = lines.Length > MaxLines;
            if (linesCut) Array.Resize(ref lines, MaxLines);

            if (pageMode) FillPage(lines);
            else _text!.Text = string.Join("\n", lines);

            string enc = fromBom ? encoding.WebName + " (BOM)" : encoding.WebName;
            _footer.Text = $"{enc} · {totalLines:N0} 行 · {PreviewBuilder.FormatSize(info.Length)}" +
                           (truncated || linesCut ? " · 已截断" : "");
        }
        catch (Exception ex)
        {
            _footer.Text = "无法读取文件：" + ex.Message;
        }
    }

    /// <summary>Fills the page top-down and stops at the page edge, so nothing is half-cut.</summary>
    private void FillPage(string[] lines)
    {
        double available = PageHeight - PagePadding * 2;
        double width = PageWidth - PagePadding * 2;

        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        double used = 0;
        var sb = new StringBuilder();
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r');
            var formatted = new FormattedText(
                line.Length == 0 ? " " : line,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                13,
                Brushes.Black,
                1.0)
            {
                MaxTextWidth = width,
                LineHeight = 21,
            };

            if (used + formatted.Height > available) break;
            used += formatted.Height;
            sb.Append(line).Append('\n');
        }

        _page!.Text = sb.ToString().TrimEnd('\n');
        if (_page.Text.Length == 0) _page.Text = "（空文件）";
    }

    private static (Encoding, bool) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(false), true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, true);

        try
        {
            new UTF8Encoding(false, true).GetString(bytes);
            return (new UTF8Encoding(false), false);
        }
        catch (DecoderFallbackException)
        {
            try { return (Encoding.GetEncoding(936), false); } catch { }
            return (Encoding.Latin1, false);
        }
    }
}



