using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace FloatCanvas.App;

/// <summary>Selectable native document content. HTML and remote images are never executed.</summary>
internal sealed class MarkdownView : FlowDocumentScrollViewer
{
    private string text = "";
    internal Action<string> OpenLink { get; set; } = ExternalLink.Open;
    internal string Text
    {
        get => text;
        set { text = value ?? ""; Document = Render(text); }
    }

    internal MarkdownView()
    {
        IsToolBarVisible = false; VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetResourceReference(BackgroundProperty, "CanvasInput");
        SetResourceReference(ForegroundProperty, "CanvasText");
        Document = Render("");
    }

    private FlowDocument Render(string source)
    {
        var document = new FlowDocument { FontFamily = PropertyPanelLayout.LabelFontFamily, FontSize = 14, PagePadding = new Thickness(14), ColumnWidth = double.PositiveInfinity };
        document.SetResourceReference(TextElement.ForegroundProperty, "CanvasText");
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        bool code = false;
        var codeLines = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (code) { document.Blocks.Add(Code(string.Join("\n", codeLines))); codeLines.Clear(); }
                code = !code; continue;
            }
            if (code) { codeLines.Add(line); continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (i + 1 < lines.Length && line.TrimStart().StartsWith('|') && IsTableDivider(lines[i + 1]))
            {
                var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 14) };
                string[] headers = Cells(line);
                foreach (var _ in headers) table.Columns.Add(new TableColumn());
                var group = new TableRowGroup(); table.RowGroups.Add(group);
                AddTableRow(headers, true); i++;
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|')) AddTableRow(Cells(lines[++i]), false);
                document.Blocks.Add(table); continue;
                void AddTableRow(string[] values, bool heading)
                {
                    var row = new TableRow(); group.Rows.Add(row);
                    for (int column = 0; column < headers.Length; column++)
                    {
                        var paragraph = Paragraph(column < values.Length ? values[column] : "");
                        paragraph.Margin = new Thickness(0);
                        var cell = new TableCell(paragraph) { Padding = new Thickness(8), BorderThickness = new Thickness(.5) };
                        cell.SetResourceReference(TableCell.BorderBrushProperty, "CanvasBorder");
                        if (heading) { cell.FontWeight = FontWeights.SemiBold; cell.SetResourceReference(TextElement.BackgroundProperty, "CanvasButton"); }
                        row.Cells.Add(cell);
                    }
                }
            }
            string trimmed = line.TrimStart();
            int headingLength = trimmed.TakeWhile(ch => ch == '#').Count();
            if (headingLength is >= 1 and <= 6 && trimmed.Length > headingLength && trimmed[headingLength] == ' ')
            {
                var paragraph = Paragraph(trimmed[(headingLength + 1)..]);
                paragraph.FontSize = headingLength == 1 ? 23 : headingLength == 2 ? 19 : 16;
                paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 12, 0, 8);
                document.Blocks.Add(paragraph);
            }
            else if (trimmed is "---" or "***")
            {
                var divider = new Paragraph { BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) };
                divider.SetResourceReference(Block.BorderBrushProperty, "CanvasBorder"); document.Blocks.Add(divider);
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || Regex.IsMatch(trimmed, @"^\d+\. ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                bool numbered = char.IsDigit(trimmed[0]);
                string content = numbered ? trimmed[(trimmed.IndexOf('.') + 2)..] : trimmed[2..];
                var list = document.Blocks.LastBlock as System.Windows.Documents.List;
                var marker = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;
                if (list is null || list.MarkerStyle != marker)
                { list = new System.Windows.Documents.List { MarkerStyle = marker, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(24, 0, 0, 0) }; document.Blocks.Add(list); }
                list.ListItems.Add(new ListItem(Paragraph(content)));
            }
            else
            {
                var paragraph = Paragraph(trimmed.StartsWith("> ", StringComparison.Ordinal) ? trimmed[2..] : line);
                document.Blocks.Add(paragraph);
            }
        }
        if (codeLines.Count > 0) document.Blocks.Add(Code(string.Join("\n", codeLines)));
        return document;
    }

    private Paragraph Paragraph(string value)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8), LineHeight = 22 };
        const string pattern = @"(``.+?``|`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\(https?://[^\s)]+\))";
        foreach (string part in Regex.Split(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 1)
            {
                int delimiter = part.StartsWith("``", StringComparison.Ordinal) ? 2 : 1;
                string code = part[delimiter..^delimiter];
                if (code.StartsWith(' ') && code.EndsWith(' ') && code.Trim().Length > 0) code = code[1..^1];
                paragraph.Inlines.Add(new InlineUIContainer(CodeAppearance.Selectable(code)) { BaselineAlignment = BaselineAlignment.Center });
            }
            else if (part.StartsWith("**", StringComparison.Ordinal) && part.EndsWith("**", StringComparison.Ordinal) && part.Length > 4)
                paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.StartsWith('[') && part.LastIndexOf("](", StringComparison.Ordinal) is var split && split > 0 && part.EndsWith(')'))
            {
                string url = part[(split + 2)..^1];
                var link = new Hyperlink(new Run(part[1..split])); link.SetResourceReference(TextElement.ForegroundProperty, "CanvasAccent");
                link.Click += (_, _) => OpenLink(url); paragraph.Inlines.Add(link);
            }
            else paragraph.Inlines.Add(new Run(part.Replace("\\|", "|", StringComparison.Ordinal)));
        }
        return paragraph;
    }

    private static Block Code(string value)
    {
        return new BlockUIContainer(CodeAppearance.Selectable(value, multiline: true)) { Margin = new Thickness(0, 4, 0, 12) };
    }
    private static string[] Cells(string line) => Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Select(cell => cell.Trim()).ToArray();
    private static bool IsTableDivider(string line) => Cells(line).All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
}

internal static class ExternalLink
{
    internal static void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return;
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
        try
        {
            if (process is not null && process.MainWindowHandle is var handle && handle != 0)
            { ShowWindow(handle, 9); SetForegroundWindow(handle); }
        }
        catch (InvalidOperationException) { } // Browsers may hand the URL to an existing process and exit.
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}
