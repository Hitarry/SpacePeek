using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;

namespace SpacePeek.Hud;

/// <summary>
/// Renders the first worksheet the way Excel shows it on open: the used range (dimension),
/// declared column widths and row heights, merged cells, bold headers, dates and percentages.
/// Everything here is defensive — real-world workbooks use a small, annoying subset of OOXML.
/// </summary>
internal static class XlsxSnapshot
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly Brush GridLine = new SolidColorBrush(Color.FromRgb(214, 214, 214));
    private static readonly Brush Foreground = new SolidColorBrush(Color.FromRgb(32, 32, 32));

    private const double MaxWidth = 880;
    private const double HeaderWidth = 38;
    private const double HeaderHeight = 20;
    private const double MaxHeight = 660;
    private const int MaxRows = 200;
    private const int MaxColumns = 26;      // cap at column Z
    private const double FrameWidth = 1400;     // fixed 16:9 preview frame
    private const double FrameHeight = 790;
    private const double FramePadding = 10;   // gutter so the last column is never flush with the edge

    public static FrameworkElement? Build(string path)
    {
        using ZipArchive zip = ZipFile.OpenRead(path);

        string? sheetPath = ResolveFirstSheet(zip);
        if (sheetPath is null) return null;
        ZipArchiveEntry? sheetEntry = zip.GetEntry(sheetPath);
        if (sheetEntry is null) return null;

        List<CellStyle> styles = ReadStyles(zip);
        (List<string> shared, bool _) = ReadSharedStrings(zip);

        XDocument sheet;
        using (Stream s = sheetEntry.Open()) sheet = XDocument.Load(s);

        var (firstRow, firstCol, lastRow, lastCol) = ReadDimension(sheet);
        var cells = ReadCells(sheet, shared);
        var merges = ReadMerges(sheet);
        var columnWidths = ReadColumnWidths(sheet);

        // The <dimension> element is often far larger than the real content (formatting applied
        // to whole empty columns), which used to leave big white margins on the right. Trim to
        // the last cell that actually renders something.
        int contentLastRow = firstRow, contentLastCol = firstCol;
        foreach (var pair in cells)
        {
            CellStyle style = pair.Value.StyleIndex >= 0 && pair.Value.StyleIndex < styles.Count
                ? styles[pair.Value.StyleIndex]
                : CellStyle.Default;
            if (string.IsNullOrWhiteSpace(FormatValue(pair.Value, style))) continue;
            contentLastRow = Math.Max(contentLastRow, pair.Key.Item1);
            contentLastCol = Math.Max(contentLastCol, pair.Key.Item2);
        }
        foreach (var (r1, c1, r2, c2) in merges)
        {
            if (!cells.TryGetValue((r1, c1), out Cell? topLeft)) continue;
            CellStyle style = topLeft.StyleIndex >= 0 && topLeft.StyleIndex < styles.Count
                ? styles[topLeft.StyleIndex]
                : CellStyle.Default;
            if (string.IsNullOrWhiteSpace(FormatValue(topLeft, style))) continue;
            contentLastRow = Math.Max(contentLastRow, r2);
            contentLastCol = Math.Max(contentLastCol, c2);
        }
        lastRow = Math.Max(firstRow, contentLastRow);
        lastCol = Math.Max(firstCol, contentLastCol);
        var rowHeights = new Dictionary<int, double>();

        foreach (XElement row in sheet.Descendants(S + "row"))
        {
            int r = (int?)row.Attribute("r") ?? rowHeights.Count + 1;
            if (double.TryParse((string?)row.Attribute("ht"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ht))
                rowHeights[r] = ht;
        }

        int rows = Math.Min(lastRow - firstRow + 1, MaxRows);
        int cols = Math.Min(lastCol - firstCol + 1, MaxColumns);
        Log.Write($"xlsx: dim=({firstRow},{firstCol})-({lastRow},{lastCol}) cells={cells.Count} " +
                  $"rows={rows} cols={cols} merges={merges.Count} sheet={sheetPath}");
        if (rows <= 0 || cols <= 0) return null;

        // pixel geometry, Excel-ish defaults
        const double CharWidth = 7.0;
        const double DefaultColWidth = 8.43;
        var widths = new double[cols];
        for (int i = 0; i < cols; i++)
        {
            int colIndex = firstCol + i;
            double chars = columnWidths.TryGetValue(colIndex, out double w) ? w : DefaultColWidth;
            widths[i] = Math.Clamp(chars * CharWidth + 5, 24, 220);
        }

        var heights = new double[rows];
        for (int i = 0; i < rows; i++)
        {
            int rowIndex = firstRow + i;
            heights[i] = rowHeights.TryGetValue(rowIndex, out double h) ? Math.Clamp(h * 4 / 3, 16, 120) : 20;
        }

        // Excel auto-fits rows to wrapped text; without this every long cell collapses to "…".
        for (int i = 0; i < rows; i++)
        {
            int rowIndex = firstRow + i;
            double needed = heights[i];
            for (int j = 0; j < cols; j++)
            {
                if (!cells.TryGetValue((rowIndex, firstCol + j), out Cell? cell)) continue;
                CellStyle style = cell.StyleIndex >= 0 && cell.StyleIndex < styles.Count
                    ? styles[cell.StyleIndex]
                    : CellStyle.Default;
                string text = FormatValue(cell, style);
                if (text.Length == 0) continue;

                double usable = Math.Max(24, widths[j] - 8);
                int perLine = Math.Max(1, (int)(usable / 7.2));
                int lineCount = (text.Length + perLine - 1) / perLine;
                if (lineCount > 1) needed = Math.Max(needed, Math.Min(lineCount, 6) * 16 + 6);
            }
            heights[i] = needed;
        }

        // Fixed 4:3 frame for every workbook: same behaviour, same readable size.
        // The sheet is scaled uniformly to fill the frame width, then rows are added until the
        // height budget is used up. Columns stop at Z.
        double naturalWidth = HeaderWidth;
        for (int i = 0; i < cols; i++) naturalWidth += widths[i];

        // Legibility beats "everything at once": never shrink below 0.75, otherwise the text is
        // unreadable. Columns that no longer fit at that scale are simply not shown.
        const double MinScale = 0.75;
        double usableWidth = FrameWidth - FramePadding * 2;
        double usableHeight = FrameHeight - FramePadding * 2;
        double scale = Math.Clamp(usableWidth / naturalWidth, MinScale, 2.2);
        if (naturalWidth * scale > usableWidth)
        {
            double used = HeaderWidth;
            int fit = 0;
            while (fit < cols && used + widths[fit] <= usableWidth / scale)
            {
                used += widths[fit];
                fit++;
            }
            cols = Math.Max(1, fit);
            naturalWidth = HeaderWidth;
            for (int i = 0; i < cols; i++) naturalWidth += widths[i];
            // keep the scale used for the fit test: recomputing could push the last column
            // past the frame edge and cut it in half
        }

        while (rows > 1)
        {
            double visible = (HeaderHeight + heights.Take(rows).Sum()) * scale;
            if (visible <= usableHeight) break;
            rows--;
        }

        double canvasWidth = naturalWidth;
        double canvasHeight = HeaderHeight;
        for (int i = 0; i < rows; i++) canvasHeight += heights[i];
        Log.Write($"xlsx: canvas={canvasWidth:F0}x{canvasHeight:F0} scale={scale:F2} cols={cols} rows={rows}");

        // Excel opens workbooks at 100%: show the top-left of the sheet with row/column
        // headers. Canvas (absolute placement) instead of Grid: a Grid with hundreds of cells
        // spends seconds in measure/arrange on real workbooks.
        var canvas = new Canvas
        {
            Background = Brushes.White,
            Width = canvasWidth,
            Height = canvasHeight,
            ClipToBounds = true,
        };

        var xs = new double[cols + 1];
        xs[0] = HeaderWidth;
        for (int i = 0; i < cols; i++) xs[i + 1] = xs[i] + widths[i];

        var ys = new double[rows + 1];
        ys[0] = HeaderHeight;
        for (int i = 0; i < rows; i++) ys[i + 1] = ys[i] + heights[i];

        // column letters (clipped at the page edge)
        for (int i = 0; i < cols; i++)
        {
            Border header = HeaderCell(ColumnName(firstCol + i), Math.Min(widths[i], canvasWidth - xs[i]));
            Canvas.SetLeft(header, xs[i]);
            Canvas.SetTop(header, 0);
            canvas.Children.Add(header);
        }

        // row numbers
        for (int i = 0; i < rows; i++)
        {
            Border header = HeaderCell((firstRow + i).ToString(CultureInfo.InvariantCulture), HeaderWidth);
            Canvas.SetLeft(header, 0);
            Canvas.SetTop(header, ys[i]);
            canvas.Children.Add(header);
        }

        var merged = new HashSet<(int, int)>();
        var spans = new Dictionary<(int, int), (int rows, int cols)>();
        foreach (var (r1, c1, r2, c2) in merges)
        {
            int rowSpan = Math.Max(1, Math.Min(r2, lastRow) - r1 + 1);
            int colSpan = Math.Max(1, Math.Min(c2, lastCol) - c1 + 1);
            rowSpan = Math.Min(rowSpan, rows - (r1 - firstRow));
            colSpan = Math.Min(colSpan, cols - (c1 - firstCol));
            if (rowSpan <= 0 || colSpan <= 0) continue;

            spans[(r1, c1)] = (rowSpan, colSpan);
            for (int r = r1; r < r1 + rowSpan; r++)
                for (int c = c1; c < c1 + colSpan; c++)
                    if (r != r1 || c != c1) merged.Add((r, c));
        }

        var mergedBorders = new List<(double x, double y, Border border)>();
        foreach (var pair in cells)
        {
            (int row, int col) = pair.Key;
            if (row < firstRow || col < firstCol || row > lastRow || col > lastCol) continue;
            if (merged.Contains((row, col))) continue;

            int rowIndex = row - firstRow;
            int colIndex = col - firstCol;
            if (rowIndex >= rows || colIndex >= cols) continue;
            if (xs[colIndex] >= canvasWidth || ys[rowIndex] >= canvasHeight) continue;

            (int rowSpan, int colSpan) = spans.TryGetValue((row, col), out var span) ? span : (1, 1);

            Cell cell = pair.Value;
            CellStyle style = cell.StyleIndex >= 0 && cell.StyleIndex < styles.Count
                ? styles[cell.StyleIndex]
                : CellStyle.Default;

            double cellWidth = Math.Min(xs[Math.Min(colIndex + colSpan, cols)] - xs[colIndex], canvasWidth - xs[colIndex]);
            double cellHeight = Math.Min(ys[Math.Min(rowIndex + rowSpan, rows)] - ys[rowIndex], canvasHeight - ys[rowIndex]);
            if (cellWidth <= 0 || cellHeight <= 0) continue;

            var border = new Border
            {
                Width = cellWidth,
                Height = cellHeight,
                Background = style.Background,
                BorderBrush = GridLine,
                BorderThickness = new Thickness(0, 0, 0.6, 0.6),
                Padding = new Thickness(3, 1, 3, 1),
                Child = new TextBlock
                {
                    Text = FormatValue(cell, style),
                    Width = Math.Max(1, cellWidth - 6),
                    FontSize = style.FontSize,
                    FontWeight = style.Bold ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = style.Foreground ?? Foreground,
                    TextWrapping = style.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                    TextTrimming = style.Wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
                    TextAlignment = style.Alignment,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            if (colSpan > 1)
                Log.Write($"xlsx-title: row={row} col={col} span={rowSpan}x{colSpan} x={xs[colIndex]:F0} w={cellWidth:F0} font={style.FontSize:F1} align={style.Alignment} text={FormatValue(cell, style)}");

            if (colSpan > 1 || rowSpan > 1)
            {
                // merged cells are added after everything else so nothing paints over them
                mergedBorders.Add((xs[colIndex], ys[rowIndex], border));
                continue;
            }

            Canvas.SetLeft(border, xs[colIndex]);
            Canvas.SetTop(border, ys[rowIndex]);
            canvas.Children.Add(border);
        }

        foreach (var (mx, my, mborder) in mergedBorders)
        {
            Canvas.SetLeft(mborder, mx);
            Canvas.SetTop(mborder, my);
            canvas.Children.Add(mborder);
        }

        return new Border
        {
            Width = FrameWidth,
            Height = FrameHeight,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                LayoutTransform = new ScaleTransform(scale, scale),
                Child = canvas,
            },
        };
    }

    private static Border HeaderCell(string text, double width) => new()
    {
        Width = Math.Max(0, width),
        Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)),
        BorderBrush = GridLine,
        BorderThickness = new Thickness(0, 0, 0.6, 0.6),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private static string ColumnName(int index)
    {
        var chars = new Stack<char>();
        int value = index;
        while (value > 0)
        {
            int rem = (value - 1) % 26;
            chars.Push((char)('A' + rem));
            value = (value - 1) / 26;
        }
        return new string(chars.ToArray());
    }

    // ------------------------------------------------------------------ parts
    private static string? ResolveFirstSheet(ZipArchive zip)
    {
        try
        {
            ZipArchiveEntry? workbook = zip.GetEntry("xl/workbook.xml");
            ZipArchiveEntry? rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbook is null) return zip.GetEntry("xl/worksheets/sheet1.xml") is not null ? "xl/worksheets/sheet1.xml" : null;

            using Stream s = workbook.Open();
            XDocument wb = XDocument.Load(s);
            XElement? first = wb.Descendants(S + "sheet").FirstOrDefault();
            string? rid = (string?)first?.Attribute(R + "id");

            if (rid is not null && rels is not null)
            {
                using Stream rs = rels.Open();
                XDocument relDoc = XDocument.Load(rs);
                XElement? target = relDoc.Descendants(PR + "Relationship")
                    .FirstOrDefault(x => (string?)x.Attribute("Id") == rid);
                string? path = (string?)target?.Attribute("Target");
                if (!string.IsNullOrWhiteSpace(path))
                {
                    path = path.Replace('\\', '/');
                    string full = path.StartsWith("/") ? path.TrimStart('/') : "xl/" + path;
                    if (zip.GetEntry(full) is not null) return full;
                }
            }
        }
        catch { }

        return zip.GetEntry("xl/worksheets/sheet1.xml") is not null ? "xl/worksheets/sheet1.xml" : null;
    }

    private static (List<string>, bool) ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        try
        {
            ZipArchiveEntry? entry = zip.GetEntry("xl/sharedStrings.xml");
            if (entry is null) return (list, false);
            using Stream s = entry.Open();
            foreach (XElement si in XDocument.Load(s).Descendants(S + "si"))
                list.Add(string.Concat(si.Descendants(S + "t").Select(t => t.Value)));
        }
        catch { }
        return (list, true);
    }

    private sealed class CellStyle
    {
        public static readonly CellStyle Default = new();
        public bool Bold;
        public bool IsDate;
        public bool IsPercent;
        public bool IsNumeric;
        public Brush? Background;
        public Brush? Foreground;
        public double FontSize = 11.5;
        public TextAlignment Alignment = TextAlignment.Left;
        public bool Wrap;
    }

    private static List<CellStyle> ReadStyles(ZipArchive zip)
    {
        var result = new List<CellStyle>();
        try
        {
            ZipArchiveEntry? entry = zip.GetEntry("xl/styles.xml");
            if (entry is null) return result;

            using Stream s = entry.Open();
            XDocument xml = XDocument.Load(s);

            var fontBold = new List<bool>();
            var fontColor = new List<Brush?>();
            var fontSize = new List<double>();
            foreach (XElement font in xml.Descendants(S + "fonts").Elements(S + "font"))
            {
                fontBold.Add(font.Element(S + "b") is not null);
                FontSize(font, fontSize);
                fontColor.Add(ColorOf(font.Element(S + "color")));
            }

            var fillColors = new List<Brush?>();
            foreach (XElement fill in xml.Descendants(S + "fills").Elements(S + "fill"))
                fillColors.Add(ColorOf(fill.Descendants(S + "fgColor").FirstOrDefault()));

            var customFormats = new Dictionary<int, string>();
            foreach (XElement fmt in xml.Descendants(S + "numFmt"))
            {
                int id = (int?)fmt.Attribute("numFmtId") ?? -1;
                string? code = (string?)fmt.Attribute("formatCode");
                if (id >= 0 && code is not null) customFormats[id] = code;
            }

            foreach (XElement xf in xml.Descendants(S + "cellXfs").Elements(S + "xf"))
            {
                int numFmtId = (int?)xf.Attribute("numFmtId") ?? 0;
                int fontId = (int?)xf.Attribute("fontId") ?? 0;
                int fillId = (int?)xf.Attribute("fillId") ?? 0;
                string code = customFormats.TryGetValue(numFmtId, out string? c) ? c : BuiltInFormat(numFmtId);

                var style = new CellStyle
                {
                    Bold = fontId >= 0 && fontId < fontBold.Count && fontBold[fontId],
                    Foreground = fontId >= 0 && fontId < fontColor.Count ? fontColor[fontId] : null,
                    FontSize = fontId >= 0 && fontId < fontSize.Count && fontSize[fontId] > 0 ? fontSize[fontId] : 11.5,
                    Background = fillId > 1 && fillId < fillColors.Count ? fillColors[fillId] : null,
                    IsDate = LooksLikeDate(code),
                    IsPercent = code.Contains('%'),
                    IsNumeric = true,
                };

                // alignment lives on the <alignment> child, not on <xf> attributes
                XElement? alignment = xf.Element(S + "alignment");
                string? horizontal = (string?)alignment?.Attribute("horizontal");
                style.Wrap = (string?)alignment?.Attribute("wrapText") is "1" or "true";
                style.Alignment = horizontal switch
                {
                    "center" or "centerContinuous" => TextAlignment.Center,
                    "right" => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                result.Add(style);
            }
        }
        catch { }
        return result;
    }

    private static void FontSize(XElement font, List<double> sink)
    {
        string? size = (string?)font.Element(S + "sz")?.Attribute("val");
        sink.Add(double.TryParse(size, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0
            ? Math.Clamp(v * 4.0 / 3.0, 8, 48)
            : 11.5);
    }

    private static Brush? ColorOf(XElement? element)
    {
        if (element is null) return null;
        string? rgb = (string?)element.Attribute("rgb");
        if (rgb is { Length: 8 } && uint.TryParse(rgb, System.Globalization.NumberStyles.HexNumber, null, out uint argb))
        {
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            return brush;
        }

        int theme = (int?)element.Attribute("theme") ?? -1;
        if (theme >= 0)
        {
            // theme palette approximation: keep it readable instead of resolving theme XML
            var brush = theme switch
            {
                0 => Color.FromRgb(255, 255, 255),
                1 => Color.FromRgb(0, 0, 0),
                2 => Color.FromRgb(238, 236, 225),
                3 => Color.FromRgb(31, 73, 125),
                4 => Color.FromRgb(79, 129, 189),
                _ => Colors.Transparent,
            };
            if (brush == Colors.Transparent) return null;
            var solid = new SolidColorBrush(brush);
            solid.Freeze();
            return solid;
        }
        return null;
    }

    private static string BuiltInFormat(int id) => id switch
    {
        14 or 15 or 16 or 17 or 22 => "yyyy-mm-dd",
        9 or 10 => "0%",
        11 => "0.00E+00",
        4 => "#,##0.00",
        _ => "General",
    };

    private static bool LooksLikeDate(string code)
    {
        if (string.IsNullOrEmpty(code) || code == "General") return false;
        string lower = code.ToLowerInvariant();
        return lower.Contains('y') || lower.Contains('d') || lower.Contains("m/") || lower.Contains("h:");
    }

    // ------------------------------------------------------------------ sheet
    private static (int, int, int, int) ReadDimension(XDocument sheet)
    {
        string? reference = (string?)sheet.Descendants(S + "dimension").FirstOrDefault()?.Attribute("ref");
        if (!string.IsNullOrEmpty(reference))
        {
            string[] parts = reference!.Split(':');
            if (parts.Length == 2)
            {
                (int r1, int c1) = ParseAddress(parts[0]);
                (int r2, int c2) = ParseAddress(parts[1]);
                if (r2 >= r1 && c2 >= c1) return (r1, c1, r2, c2);
            }
        }

        int maxRow = 1, maxCol = 1;
        foreach (XElement c in sheet.Descendants(S + "c"))
        {
            (int r, int col) = ParseAddress((string?)c.Attribute("r") ?? "A1");
            maxRow = Math.Max(maxRow, r);
            maxCol = Math.Max(maxCol, col);
        }
        return (1, 1, maxRow, maxCol);
    }

    private sealed class Cell
    {
        public string Text = "";
        public bool IsNumeric;
        public int StyleIndex = -1;
    }

    private static Dictionary<(int, int), Cell> ReadCells(XDocument sheet, List<string> shared)
    {
        var map = new Dictionary<(int, int), Cell>();
        foreach (XElement c in sheet.Descendants(S + "c"))
        {
            (int row, int col) = ParseAddress((string?)c.Attribute("r") ?? "A1");
            string type = (string?)c.Attribute("t") ?? "n";

            string text;
            bool numeric = false;
            switch (type)
            {
                case "s":
                    string raw = c.Element(S + "v")?.Value ?? "";
                    text = int.TryParse(raw, out int idx) && idx >= 0 && idx < shared.Count ? shared[idx] : "";
                    break;
                case "inlineStr":
                    text = string.Concat(c.Descendants(S + "t").Select(t => t.Value));
                    break;
                case "str":
                case "e":
                case "b":
                    text = c.Element(S + "v")?.Value ?? "";
                    break;
                default:
                    text = c.Element(S + "v")?.Value ?? "";
                    numeric = true;
                    break;
            }

            map[(row, col)] = new Cell
            {
                Text = text,
                IsNumeric = numeric,
                StyleIndex = (int?)c.Attribute("s") ?? -1,
            };
        }
        return map;
    }

    private static string FormatValue(Cell cell, CellStyle style)
    {
        if (!cell.IsNumeric) return cell.Text;
        if (!double.TryParse(cell.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return cell.Text;

        if (style.IsDate && number > 0 && number < 2958466)
            return DateTime.FromOADate(number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (style.IsPercent) return number.ToString("0.##%", CultureInfo.InvariantCulture);
        if (Math.Abs(number - Math.Round(number)) < 1e-9)
            return ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
        return number.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static List<(int, int, int, int)> ReadMerges(XDocument sheet)
    {
        var list = new List<(int, int, int, int)>();
        foreach (XElement m in sheet.Descendants(S + "mergeCell"))
        {
            string? reference = (string?)m.Attribute("ref");
            if (string.IsNullOrEmpty(reference)) continue;
            string[] parts = reference!.Split(':');
            if (parts.Length != 2) continue;
            (int r1, int c1) = ParseAddress(parts[0]);
            (int r2, int c2) = ParseAddress(parts[1]);
            list.Add((r1, c1, r2, c2));
        }
        return list;
    }

    private static Dictionary<int, double> ReadColumnWidths(XDocument sheet)
    {
        var map = new Dictionary<int, double>();
        foreach (XElement col in sheet.Descendants(S + "col"))
        {
            int min = (int?)col.Attribute("min") ?? 0;
            int max = (int?)col.Attribute("max") ?? 0;
            double width = double.TryParse((string?)col.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 0;
            if (width <= 0) continue;
            for (int i = min; i <= max && i - min < 256; i++) map[i] = width;
        }
        return map;
    }

    private static (int, int) ParseAddress(string reference)
    {
        int col = 0, row = 0, i = 0;
        for (; i < reference.Length; i++)
        {
            char ch = reference[i];
            if (ch is >= 'A' and <= 'Z') col = col * 26 + (ch - 'A' + 1);
            else if (ch is >= 'a' and <= 'z') col = col * 26 + (ch - 'a' + 1);
            else break;
        }
        for (; i < reference.Length; i++)
        {
            char ch = reference[i];
            if (ch is >= '0' and <= '9') row = row * 10 + (ch - '0');
            else break;
        }
        return (Math.Max(1, row), Math.Max(1, col));
    }
}


























