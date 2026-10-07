using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace KspFlightPlanner.App.Controls;

/// <summary>
/// Minimal Markdown → FlowDocument renderer for the in-app guide. Supports headings (#, ##, ###),
/// paragraphs, bullet and numbered lists, pipe tables, fenced code blocks, block quotes,
/// and inline **bold**, *italic*, `code` and [links](url).
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x90, 0xA8)));
    private static readonly Brush QuoteBorder = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)));
    private static readonly Brush RuleBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)));

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14, PagePadding = new Thickness(24, 12, 24, 24) };
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length;)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            if (line.StartsWith("```"))
            {
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("```"); i++) code.Add(lines[i]);
                i++; // closing fence
                doc.Blocks.Add(new Paragraph(new Run(string.Join("\n", code)))
                {
                    FontFamily = Mono, FontSize = 13, Background = CodeBackground,
                    Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 4, 0, 10),
                });
                continue;
            }

            if (HeadingRegex().Match(line) is { Success: true } h)
            {
                var level = h.Groups[1].Value.Length;
                var heading = new Paragraph
                {
                    FontSize = level switch { 1 => 28, 2 => 20, _ => 16 },
                    FontWeight = level == 1 ? FontWeights.Bold : FontWeights.SemiBold,
                    Margin = new Thickness(0, level == 1 ? 0 : 22, 0, 6),
                };
                AddInlines(heading.Inlines, h.Groups[2].Value);
                doc.Blocks.Add(heading);
                if (level == 2) doc.Blocks.Add(new Paragraph { BorderBrush = RuleBrush, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 0, 0, 6), FontSize = 1 });
                i++;
                continue;
            }

            if (line.TrimStart().StartsWith('|'))
            {
                var rows = new List<string>();
                for (; i < lines.Length && lines[i].TrimStart().StartsWith('|'); i++) rows.Add(lines[i]);
                doc.Blocks.Add(BuildTable(rows));
                continue;
            }

            if (line.StartsWith("> "))
            {
                var quote = new Paragraph
                {
                    BorderBrush = QuoteBorder, BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 4, 4, 4), Margin = new Thickness(0, 4, 0, 10), Background = CodeBackground,
                };
                var text = new List<string>();
                for (; i < lines.Length && lines[i].StartsWith("> "); i++) text.Add(lines[i][2..]);
                AddInlines(quote.Inlines, string.Join(" ", text));
                doc.Blocks.Add(quote);
                continue;
            }

            if (ListItemRegex().IsMatch(line))
            {
                var numbered = char.IsDigit(line.TrimStart()[0]);
                var list = new List
                {
                    MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 2, 0, 10), Padding = new Thickness(24, 0, 0, 0),
                };
                for (; i < lines.Length && ListItemRegex().Match(lines[i]) is { Success: true } m; i++)
                {
                    var item = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                    AddInlines(item.Inlines, m.Groups[2].Value);
                    list.ListItems.Add(new ListItem(item));
                }
                doc.Blocks.Add(list);
                continue;
            }

            // Plain paragraph: gather until a blank line or another block starts.
            var paragraphLines = new List<string>();
            for (; i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines[i]); i++) paragraphLines.Add(lines[i].Trim());
            var paragraph = new Paragraph { Margin = new Thickness(0, 2, 0, 10), LineHeight = 21 };
            AddInlines(paragraph.Inlines, string.Join(" ", paragraphLines));
            doc.Blocks.Add(paragraph);
        }

        return doc;
    }

    private static bool StartsBlock(string line) =>
        line.StartsWith("```") || line.StartsWith('#') || line.StartsWith("> ")
        || line.TrimStart().StartsWith('|') || ListItemRegex().IsMatch(line);

    private static Table BuildTable(List<string> rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

        var data = rows.Where(r => !SeparatorRegex().IsMatch(r)).Select(Cells).ToList();
        var columns = data.Max(r => r.Length);
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12) };
        for (var c = 0; c < columns; c++) table.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        for (var r = 0; r < data.Count; r++)
        {
            var row = new TableRow();
            for (var c = 0; c < columns; c++)
            {
                var p = new Paragraph { Margin = new Thickness(0) };
                AddInlines(p.Inlines, c < data[r].Length ? data[r][c] : "");
                if (r == 0) p.FontWeight = FontWeights.SemiBold;
                row.Cells.Add(new TableCell(p)
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Background = r == 0 ? CodeBackground : null,
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var position = 0;
        foreach (Match m in InlineRegex().Matches(text))
        {
            if (m.Index > position) inlines.Add(new Run(text[position..m.Index]));
            var token = m.Value;
            if (token.StartsWith("**")) inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('`')) inlines.Add(new Run(token[1..^1]) { FontFamily = Mono, Background = CodeBackground });
            else if (token.StartsWith('['))
            {
                var label = m.Groups["label"].Value;
                var url = m.Groups["url"].Value;
                var link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url), ToolTip = url };
                link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                inlines.Add(link);
            }
            else inlines.Add(new Italic(new Run(token[1..^1])));
            position = m.Index + m.Length;
        }
        if (position < text.Length) inlines.Add(new Run(text[position..]));
    }

    [GeneratedRegex(@"^(#{1,3})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*(-|\d+\.)\s+(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}")]
    private static partial Regex SeparatorRegex();

    // Link URLs may contain one level of balanced parentheses, e.g. Wikipedia's "..._(astrodynamics)".
    [GeneratedRegex(@"\*\*[^*]+\*\*|`[^`]+`|\[(?<label>[^\]]+)\]\((?<url>(?:[^()\s]|\([^()\s]*\))+)\)|\*[^*\s][^*]*\*")]
    private static partial Regex InlineRegex();

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
