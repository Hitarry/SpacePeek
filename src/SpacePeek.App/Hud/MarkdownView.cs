using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SpacePeek.Hud;

/// <summary>
/// Small Markdown renderer: headings, paragraphs, bullets, numbers, quotes, code fences,
/// inline bold/italic/code and rules. No HTML engine — the preview shows the rendered page.
/// </summary>
internal static class MarkdownView
{
    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(30, 30, 30));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(110, 110, 110));
    private static readonly Brush CodeBackground = new SolidColorBrush(Color.FromRgb(244, 244, 246));
    private static readonly FontFamily BodyFont = new FontFamily("Microsoft YaHei UI, Segoe UI");
    private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");

    public static FrameworkElement Build(string markdown, double width, double height)
    {
        var panel = new StackPanel();

        List<Block> blocks = Parse(markdown);
        foreach (Block block in blocks) panel.Children.Add(Render(block));

        var page = new Border
        {
            Width = width,
            Height = height,
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            ClipToBounds = true,
            Child = new Border
            {
                Padding = new Thickness(42, 34, 30, 20),
                Child = panel,
            },
        };
        return page;
    }

    // ------------------------------------------------------------------ model
    private abstract class Block { }
    private sealed class Heading : Block { public int Level; public string Text = ""; }
    private sealed class Paragraph : Block { public string Text = ""; }
    private sealed class Bullet : Block { public string Text = ""; public int Indent; public bool Ordered; public string Marker = "•"; }
    private sealed class Quote : Block { public string Text = ""; }
    private sealed class Code : Block { public string Text = ""; }
    private sealed class Rule : Block { }
    private sealed class Spacer : Block { }
    private sealed class Table : Block { public readonly List<string[]> Rows = new(); }

    private static List<Block> Parse(string markdown)
    {
        var blocks = new List<Block>();
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');

        var paragraph = new StringBuilder();
        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new Paragraph { Text = paragraph.ToString().Trim() });
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    code.AppendLine(lines[i++]);
                blocks.Add(new Code { Text = code.ToString().TrimEnd() });
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^#{1,6}\s+"))
            {
                FlushParagraph();
                int level = trimmed.TakeWhile(c => c == '#').Count();
                blocks.Add(new Heading { Level = level, Text = trimmed[level..].Trim() });
                continue;
            }

            if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))
            {
                FlushParagraph();
                blocks.Add(new Rule());
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                FlushParagraph();
                var quote = new StringBuilder(trimmed.TrimStart('>').Trim());
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith(">"))
                    quote.Append(' ').Append(lines[++i].TrimStart().TrimStart('>').Trim());
                blocks.Add(new Quote { Text = quote.ToString() });
                continue;
            }

            // pipe table: header row followed by a separator row
            if (trimmed.StartsWith("|") && i + 1 < lines.Length &&
                Regex.IsMatch(lines[i + 1].Trim(), @"^\|[\s:\-|]+\|$"))
            {
                FlushParagraph();
                var table = new Table();
                table.Rows.Add(SplitRow(trimmed));
                i++;   // separator
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("|"))
                    table.Rows.Add(SplitRow(lines[++i].Trim()));
                blocks.Add(table);
                continue;
            }

            Match bullet = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new Bullet
                {
                    Text = bullet.Groups[2].Value,
                    Indent = bullet.Groups[1].Value.Length / 2,
                    Ordered = false,
                    Marker = "•",
                });
                continue;
            }

            Match numbered = Regex.Match(line, @"^(\s*)(\d+)[.)]\s+(.*)$");
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new Bullet
                {
                    Text = numbered.Groups[3].Value,
                    Indent = numbered.Groups[1].Value.Length / 2,
                    Ordered = true,
                    Marker = numbered.Groups[2].Value + ".",
                });
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }

        FlushParagraph();
        return blocks;
    }

    // ----------------------------------------------------------------- render
    private static FrameworkElement Render(Block block)
    {
        switch (block)
        {
            case Heading h:
                return new TextBlock
                {
                    Text = StripInline(h.Text),
                    FontFamily = BodyFont,
                    FontSize = h.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 },
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Ink,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, h.Level <= 2 ? 16 : 12, 0, 6),
                };

            case Paragraph p:
            {
                var text = new TextBlock
                {
                    FontFamily = BodyFont,
                    FontSize = 13,
                    Foreground = Ink,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 21,
                    Margin = new Thickness(0, 3, 0, 7),
                };
                AddInlines(text, p.Text);
                return text;
            }

            case Bullet b:
            {
                var row = new Grid { Margin = new Thickness(14 + b.Indent * 16, 0, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var marker = new TextBlock
                {
                    Text = b.Marker,
                    FontFamily = BodyFont,
                    FontSize = 13,
                    Foreground = Muted,
                    Margin = new Thickness(0, 0, 7, 0),
                };
                Grid.SetColumn(marker, 0);
                row.Children.Add(marker);

                var body = new TextBlock
                {
                    FontFamily = BodyFont,
                    FontSize = 13,
                    Foreground = Ink,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 20,
                };
                AddInlines(body, b.Text);
                Grid.SetColumn(body, 1);
                row.Children.Add(body);
                return row;
            }

            case Quote q:
                return new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 4),
                    Margin = new Thickness(0, 3, 0, 7),
                    Child = new TextBlock
                    {
                        Text = StripInline(q.Text),
                        FontFamily = BodyFont,
                        FontSize = 12.5,
                        Foreground = Muted,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 20,
                    },
                };

            case Code c:
                return new Border
                {
                    Background = CodeBackground,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 4, 0, 8),
                    Child = new TextBlock
                    {
                        Text = c.Text,
                        FontFamily = MonoFont,
                        FontSize = 11.5,
                        Foreground = Ink,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };

            case Rule:
                return new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromRgb(225, 225, 225)),
                    Margin = new Thickness(0, 10, 0, 10),
                };

            case Table t:
            {
                int columns = t.Rows.Max(r => r.Length);
                var grid = new Grid { Margin = new Thickness(0, 6, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
                for (int c = 0; c < columns; c++)
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int r = 0; r < t.Rows.Count; r++)
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int r = 0; r < t.Rows.Count; r++)
                {
                    for (int c = 0; c < t.Rows[r].Length; c++)
                    {
                        var cell = new Border
                        {
                            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
                            BorderThickness = new Thickness(0, 0, 0.6, 0.6),
                            Padding = new Thickness(6, 4, 6, 4),
                            Background = r == 0 ? new SolidColorBrush(Color.FromRgb(247, 247, 249)) : Brushes.White,
                            Child = new TextBlock
                            {
                                Text = StripInline(t.Rows[r][c]),
                                FontFamily = BodyFont,
                                FontSize = 11.5,
                                Foreground = Ink,
                                TextWrapping = TextWrapping.Wrap,
                                FontWeight = r == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                            },
                        };
                        Grid.SetRow(cell, r);
                        Grid.SetColumn(cell, c);
                        grid.Children.Add(cell);
                    }
                }
                return grid;
            }

            default:
                return new Border { Height = 6 };
        }
    }

    private static void AddInlines(TextBlock target, string text)
    {
        // **bold**, *italic*, `code`, [text](url)
        var pattern = new Regex(@"(\*\*.+?\*\*|`[^`]+`|\*[^*\n]+\*|\[[^\]]+\]\([^)]+\))");
        int index = 0;
        foreach (Match m in pattern.Matches(text))
        {
            if (m.Index > index) target.Inlines.Add(new Run(text[index..m.Index]));
            string token = m.Value;

            if (token.StartsWith("**"))
                target.Inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith("`"))
                target.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = MonoFont,
                    Background = CodeBackground,
                    FontSize = 12,
                });
            else if (token.StartsWith("["))
            {
                Match link = Regex.Match(token, @"\[([^\]]+)\]\(");
                target.Inlines.Add(new Run(link.Success ? link.Groups[1].Value : token) { Foreground = Muted });
            }
            else
                target.Inlines.Add(new Italic(new Run(token[1..^1])));

            index = m.Index + m.Length;
        }
        if (index < text.Length) target.Inlines.Add(new Run(text[index..]));
    }

    private static string[] SplitRow(string row)
    {
        string trimmed = row.Trim().Trim('|');
        return trimmed.Split('|').Select(c => c.Trim()).ToArray();
    }

    private static string StripInline(string text) => Regex.Replace(text, @"[*`]|\[|\]\([^)]*\)", "");
}

