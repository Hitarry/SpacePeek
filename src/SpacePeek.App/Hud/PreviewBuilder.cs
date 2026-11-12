using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SpacePeek.Hud;

internal static class PreviewBuilder
{
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(32, 32, 32));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(120, 120, 120));

    public static FrameworkElement? ForSelection(IReadOnlyList<Interop.SelectedItem> items)
    {
        if (items.Count == 0) return null;
        if (items.Count > 1)
        {
            string names = string.Join("、", System.Linq.Enumerable.Take(
                System.Linq.Enumerable.Select(items, i => i.Name), 3));
            string more = items.Count > 3 ? " …" : "";
            return InfoCard($"{items.Count} 项已选", names + more, "多选预览将在后续版本支持翻页", Dim);
        }

        Interop.SelectedItem item = items[0];
        if (item.IsFolder && Directory.Exists(item.Path)) return new FolderPreview(item.Path);
        if (!File.Exists(item.Path)) return FileCard(item);
        if (string.Equals(Path.GetExtension(item.Path), ".pdf", StringComparison.OrdinalIgnoreCase))
            return new PdfPreview(item.Path);
        if (ImagePreview.Handles(item.Path)) return new ImagePreview(item.Path);
        if (VideoPreview.Handles(item.Path)) return new VideoPreview(item.Path);
        if (Path.GetExtension(item.Path).Equals(".epub", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                FrameworkElement? epub = EpubPreview.Build(item.Path);
                if (epub is not null) return epub;
            }
            catch (Exception ex) { Log.Exception("epub", ex); }
        }
        if (ArchivePreview.Handles(item.Path)) return new ArchivePreview(item.Path);

        // Office files: the shell preview handler renders the real thing (Word page layout,
        // Excel grid, PowerPoint slide). Our own OOXML snapshot is only used when it refuses.
        string officeExt = Path.GetExtension(item.Path).ToLowerInvariant();
        switch (officeExt)
        {
            case ".docx":
            case ".doc":
                // Page image first: a fitted page beats a 100%-scale crop.
                // A4 at 96 dpi is 794x1123 — the capture window must be taller than the page,
                // otherwise the handler crops the bottom of it.
                return new OfficePagePreview(item.Path, 620, 900,
                    () => OoxmlSnapshot.BuildDocx(item.Path), 1100, 1500);
            case ".pptx":
            case ".ppt":
                return new OfficePagePreview(item.Path, 960, 540, () => OoxmlSnapshot.BuildPptx(item.Path), 1600, 900);
            case ".xlsx":
            case ".xls":
            case ".xlsm":
            case ".xlsb":
            {
                // OOXML packages: our own renderer (fast, style-faithful).
                if (LooksLikeZip(item.Path))
                {
                    try
                    {
                        FrameworkElement? snapshot = XlsxSnapshot.Build(item.Path);
                        if (snapshot is not null) return snapshot;
                    }
                    catch (Exception ex)
                    {
                        Log.Exception("xlsx-snapshot", ex);
                    }
                }

                // BIFF (.xls) and anything the snapshot refused: let Excel export page 1 and
                // show the resulting page image. Never the handler — it paints at 100% zoom and
                // silently drops every column past its viewport (the "stops at K" bug).
                return new OfficePagePreview(item.Path, 1200, 750, null, 2400, 1400);
            }
        }

        if (HtmlPreview.Handles(item.Path)) return new HtmlPreview(item.Path);
        string ext = Path.GetExtension(item.Path).ToLowerInvariant();
        if (ext is ".md" or ".markdown")
        {
            try
            {
                string text = File.ReadAllText(item.Path);
                if (text.Length > 512 * 1024) text = text[..(512 * 1024)];
                return MarkdownView.Build(text, 509, 720);
            }
            catch (Exception ex)
            {
                Log.Exception("markdown", ex);
                return new TextPreview(item.Path, pageMode: true);
            }
        }
        if (TextPreview.Handles(item.Path)) return new TextPreview(item.Path);
        return FileCard(item);
    }

    private static bool LooksLikeZip(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            int read = fs.Read(magic);
            return read == 4 && magic[0] == 'P' && magic[1] == 'K' && magic[2] == 3 && magic[3] == 4;
        }
        catch { return false; }
    }

    private static bool TryCreateHandler(string path,
        out Interop.PreviewHandlerInterop.IPreviewHandler? handler, out string error)
    {
        handler = null;
        error = "";
        try
        {
            handler = Interop.PreviewHandlerInterop.Create(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static FrameworkElement FileCard(Interop.SelectedItem item)
    {
        string meta = "文件";
        string? path = item.Path;
        try
        {
            var fi = new FileInfo(item.Path);
            if (fi.Exists) meta = $"{FormatSize(fi.Length)} · {fi.LastWriteTime:yyyy-MM-dd HH:mm}";
        }
        catch { }
        return InfoCard(item.Name, path, meta, Dim);
    }

    public static FrameworkElement InfoCard(string title, string? subtitle, string? meta, Brush dim)
    {
        var panel = new StackPanel { Margin = new Thickness(24), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Fg,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(meta))
            panel.Children.Add(new TextBlock { Text = meta, Foreground = dim, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) });
        if (!string.IsNullOrEmpty(subtitle))
            panel.Children.Add(new TextBlock
            {
                Text = subtitle,
                Foreground = dim,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

internal sealed class Row : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public string ModifiedText { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public int Depth { get; init; }
    public bool IsExpanded { get; set; }
    public bool IsLoading { get; set; }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);

    public string Chevron => IsFolder ? (IsLoading ? "…" : IsExpanded ? "▾" : "▸") : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaiseChevron()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Chevron)));
}

internal sealed class FolderPreview : Grid
{
    private const int RootLimit = 500;
    private const int ChildLimit = 200;
    private const int TotalLimit = 2000;

    private readonly string _path;
    private readonly TextBlock _header;
    private readonly ListView _list;
    private readonly ObservableCollection<Row> _rows = new();

    public FolderPreview(string path)
    {
        _path = path;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _header = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(102, 102, 102)),
            FontSize = 12,
            Margin = new Thickness(14, 10, 14, 8),
            Text = "正在读取…",
        };
        SetRow(_header, 0);
        Children.Add(_header);

        _list = new ListView
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            ItemsSource = _rows,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Standard);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.MaxHeight = 420;
        _list.PreviewMouseLeftButtonUp += OnRowClick;
        _list.Focusable = false;

        var view = new GridView();
        view.Columns.Add(new GridViewColumn
        {
            Header = "名称",
            Width = 380,
            CellTemplate = BuildNameTemplate(),
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "大小",
            Width = 90,
            DisplayMemberBinding = new Binding(nameof(Row.SizeText)),
        });
        view.Columns.Add(new GridViewColumn
        {
            Header = "修改时间",
            Width = 150,
            DisplayMemberBinding = new Binding(nameof(Row.ModifiedText)),
        });
        _list.View = view;

        SetRow(_list, 1);
        Children.Add(_list);

        Loaded += async (_, _) => await LoadRootAsync();
    }

    private static DataTemplate BuildNameTemplate()
    {
        var template = new DataTemplate();

        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        panel.SetBinding(FrameworkElement.MarginProperty, new Binding(nameof(Row.Indent)));

        var chevron = new FrameworkElementFactory(typeof(TextBlock));
        chevron.SetValue(TextBlock.TextProperty, new Binding(nameof(Row.Chevron)));
        chevron.SetValue(FrameworkElement.WidthProperty, 16.0);
        chevron.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(140, 140, 140)));
        panel.AppendChild(chevron);

        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetValue(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
        panel.AppendChild(name);

        template.VisualTree = panel;
        return template;
    }

    private async Task LoadRootAsync()
    {
        List<Row> rows;
        int folders, files;
        long bytes;
        bool truncated;

        try
        {
            (rows, folders, files, bytes, truncated) = await Task.Run(() => ScanRoot(_path));
        }
        catch (Exception ex)
        {
            _header.Text = "无法读取此文件夹：" + ex.Message;
            return;
        }

        foreach (Row r in rows) _rows.Add(r);
        _header.Text = Summary(folders, files, bytes, truncated);
    }

    private static string Summary(int folders, int files, long bytes, bool truncated)
        => $"{folders} 个文件夹 · {files} 个文件 · {PreviewBuilder.FormatSize(bytes)}" +
           (truncated ? " · 已达显示上限" : "");

    private static (List<Row>, int, int, long, bool) ScanRoot(string path)
    {
        var list = new List<Row>();
        int folders = 0, files = 0;
        long bytes = 0;
        bool truncated = false;

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (string dir in Directory.EnumerateDirectories(path, "*", opts))
        {
            folders++;
            if (list.Count < RootLimit) list.Add(DirectoryRow(dir, 0));
            else truncated = true;
        }

        foreach (string file in Directory.EnumerateFiles(path, "*", opts))
        {
            files++;
            try
            {
                var fi = new FileInfo(file);
                bytes += fi.Length;
                if (list.Count < RootLimit) list.Add(FileRow(fi, 0));
            }
            catch { }
            if (list.Count >= RootLimit) truncated = true;
        }

        return (list, folders, files, bytes, truncated);
    }

    private static Row DirectoryRow(string dir, int depth)
    {
        string modified = "";
        try { modified = Directory.GetLastWriteTime(dir).ToString("yyyy-MM-dd HH:mm"); } catch { }
        return new Row
        {
            Name = Path.GetFileName(dir),
            FullPath = dir,
            IsFolder = true,
            Depth = depth,
            ModifiedText = modified,
        };
    }

    private static Row FileRow(FileInfo fi, int depth) => new()
    {
        Name = fi.Name,
        FullPath = fi.FullName,
        SizeText = PreviewBuilder.FormatSize(fi.Length),
        ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
        Depth = depth,
    };

    private static List<Row> ReadChildren(string dir, int depth)
    {
        var rows = new List<Row>();
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir, "*", opts))
            {
                if (rows.Count >= ChildLimit) break;
                rows.Add(DirectoryRow(sub, depth));
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*", opts))
            {
                if (rows.Count >= ChildLimit) break;
                rows.Add(FileRow(new FileInfo(file), depth));
            }
        }
        catch { }
        return rows;
    }

    /// <summary>Test hook: expands the first folder so layout can be checked headlessly.</summary>
    internal async Task ExpandFirstAsync()
    {
        Row? first = null;
        foreach (Row r in _rows)
        {
            if (!r.IsFolder) continue;
            first = r;
            break;
        }
        if (first is not null) await ExpandAsync(first);
    }

    private async void OnRowClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            // Hit-test by container geometry: ListViewItem rows have no background here,
            // so WPF hit tests fall through to the ScrollViewer.
            Point pos = e.GetPosition(_list);
            Row? hit = null;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_list.ItemContainerGenerator.ContainerFromIndex(i) is not ListViewItem c) continue;
                Rect bounds = c.TransformToAncestor(_list)
                    .TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                if (!bounds.Contains(pos)) continue;
                hit = _rows[i];
                break;
            }

            if (hit is null || !hit.IsFolder) return;

            if (hit.IsExpanded) Collapse(hit);
            else await ExpandAsync(hit);
        }
        catch (Exception ex) { Log.Exception("rowclick", ex); }
    }

    private async Task ExpandAsync(Row row)
    {
        row.IsLoading = true;
        row.RaiseChevron();
        int index = _rows.IndexOf(row);
        if (index < 0) { row.IsLoading = false; row.RaiseChevron(); return; }

        List<Row> children = await Task.Run(() => ReadChildren(row.FullPath, row.Depth + 1));

        int budget = TotalLimit - _rows.Count;
        if (budget <= 0) children.Clear();
        else if (children.Count > budget) children.RemoveRange(budget, children.Count - budget);

        int insertAt = index + 1;
        foreach (Row child in children) _rows.Insert(insertAt++, child);

        row.IsExpanded = true;
        row.IsLoading = false;
        row.RaiseChevron();
    }

    private void Collapse(Row row)
    {
        int index = _rows.IndexOf(row);
        if (index < 0) return;
        row.IsExpanded = false;
        row.RaiseChevron();

        int i = index + 1;
        while (i < _rows.Count && _rows[i].Depth > row.Depth) _rows.RemoveAt(i);
    }
}














