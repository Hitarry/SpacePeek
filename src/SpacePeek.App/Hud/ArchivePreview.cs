using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SpacePeek.Interop;

namespace SpacePeek.Hud;

/// <summary>Archive listing. Entry names are decoded by ZipListing, not by System.IO.Compression,
/// so GBK-era archives show readable names instead of mojibake.</summary>
internal sealed class ArchivePreview : Grid
{
    private const int MaxEntries = 500;

    private static readonly string[] Extensions = { ".zip", ".jar", ".apk", ".nupkg", ".vsix", ".xpi", ".epub" };

    public static bool Handles(string path) =>
        Array.IndexOf(Extensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

    private readonly TextBlock _header;
    private readonly ListView _list;

    public ArchivePreview(string path)
    {
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
            MaxHeight = 420,
            MinHeight = 120,
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Standard);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "名称", Width = 400, DisplayMemberBinding = new Binding(nameof(EntryRow.Name)) });
        view.Columns.Add(new GridViewColumn { Header = "原始大小", Width = 100, DisplayMemberBinding = new Binding(nameof(EntryRow.SizeText)) });
        view.Columns.Add(new GridViewColumn { Header = "压缩后", Width = 100, DisplayMemberBinding = new Binding(nameof(EntryRow.PackedText)) });
        _list.View = view;
        SetRow(_list, 1);
        Children.Add(_list);

        Loaded += (_, _) => Load(path);
    }

    private void Load(string path)
    {
        try
        {
            List<ZipListing.Entry> entries = ZipListing.Read(path, MaxEntries + 200);
            if (entries.Count == 0)
            {
                _header.Text = "无法解析此压缩包（可能加密、损坏或为 ZIP64）";
                return;
            }

            var rows = new List<EntryRow>();
            long totalRaw = 0, totalPacked = 0;
            int folders = 0, files = 0;
            bool truncated = false;

            foreach (ZipListing.Entry e in entries)
            {
                if (e.IsDirectory) folders++;
                else files++;

                totalRaw += e.Size;
                totalPacked += e.PackedSize;

                if (rows.Count >= MaxEntries) { truncated = true; continue; }

                rows.Add(new EntryRow
                {
                    Name = e.Name,
                    SizeText = e.Size > 0 ? PreviewBuilder.FormatSize(e.Size) : "",
                    PackedText = e.PackedSize > 0 ? PreviewBuilder.FormatSize(e.PackedSize) : "",
                });
            }

            _list.ItemsSource = rows;
            _list.SelectedIndex = -1;
            _list.Focusable = false;

            string ratio = totalRaw > 0 ? $" · 压缩率 {(double)totalPacked / totalRaw:P0}" : "";
            _header.Text = $"{files} 个文件 · {folders} 个文件夹 · 解压后 {PreviewBuilder.FormatSize(totalRaw)}{ratio}" +
                           (truncated ? $" · 仅显示前 {MaxEntries} 项" : "");
        }
        catch (Exception ex)
        {
            Log.Exception("archive", ex);
            _header.Text = "无法读取压缩包：" + ex.Message;
        }
    }
}

internal sealed class EntryRow
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string PackedText { get; set; } = "";
}

