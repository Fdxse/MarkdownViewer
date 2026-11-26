using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Tables;

/// <summary>
/// Reads and writes tables in the Windows clipboard's HTML format ("CF_HTML").
/// Word and web pages put an HTML table on the clipboard; its plain-text version loses the cell boundaries
/// when a cell has a line break, so the cells are read from the HTML instead.
/// Going the other way, an HTML table next to tab-separated text makes Word and Excel paste real cells.
/// </summary>
public static partial class HtmlTableClipboard
{
    /// <summary>The start of a &lt;table&gt; tag (to count the tables).</summary>
    [GeneratedRegex(@"<table[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex TableStartTagRegex();

    /// <summary>One table row, with its inner HTML in group "rowHtml".</summary>
    [GeneratedRegex(@"<tr[^>]*>(?<rowHtml>.*?)</tr\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableRowRegex();

    /// <summary>One cell (td or th), with its inner HTML in group "cellHtml". A missing end tag ends at the next cell.</summary>
    [GeneratedRegex(@"<t[dh](\s[^>]*)?>(?<cellHtml>.*?)(</t[dh]\s*>|(?=<t[dh][\s>])|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableCellRegex();

    /// <summary>Tags that mean "new line" inside a cell: &lt;br&gt; and the end of a paragraph or div.</summary>
    [GeneratedRegex(@"<br\s*/?>|</p\s*>|</div\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    /// <summary>Word's conditional comments, ordinary comments, and style/script blocks: none of it is cell text.</summary>
    [GeneratedRegex(@"<!--.*?-->|<style[^>]*>.*?</style\s*>|<script[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonTextBlockRegex();

    /// <summary>Any other tag.</summary>
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    /// <summary>Runs of spaces, tabs and non-breaking spaces.</summary>
    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpaceRunRegex();

    /// <summary>Spaces around a line break.</summary>
    [GeneratedRegex(@" *\n *")]
    private static partial Regex SpacesAroundLineBreakRegex();

    /// <summary>
    /// Reads the cells of the one table in a clipboard HTML fragment.
    /// Returns null when the HTML has no table or more than one (also nested tables), so the caller can fall back to plain text.
    /// </summary>
    public static List<List<string>>? ReadSingleTable(string clipboardHtml)
    {
        if (TableStartTagRegex().Matches(clipboardHtml).Count != 1)
        {
            return null;
        }

        var pastedRows = new List<List<string>>();
        foreach (Match rowMatch in TableRowRegex().Matches(clipboardHtml))
        {
            var rowCells = TableCellRegex().Matches(rowMatch.Groups["rowHtml"].Value)
                .Select(cellMatch => CellHtmlToText(cellMatch.Groups["cellHtml"].Value))
                .ToList();
            if (rowCells.Count > 0)
            {
                pastedRows.Add(rowCells);
            }
        }
        return pastedRows.Count > 0 ? pastedRows : null;
    }

    /// <summary>
    /// Turns the HTML of one cell into plain text: line breaks and paragraphs become \n,
    /// other tags are dropped, entities such as &amp;amp; are decoded and extra whitespace is collapsed.
    /// </summary>
    private static string CellHtmlToText(string cellHtml)
    {
        // In HTML a raw line break is just a space; only tags make real line breaks.
        string cellText = cellHtml.Replace("\r", " ").Replace("\n", " ");
        cellText = NonTextBlockRegex().Replace(cellText, string.Empty);
        cellText = LineBreakTagRegex().Replace(cellText, "\n");
        cellText = AnyTagRegex().Replace(cellText, string.Empty);
        cellText = WebUtility.HtmlDecode(cellText);
        cellText = SpaceRunRegex().Replace(cellText, " ");
        cellText = SpacesAroundLineBreakRegex().Replace(cellText, "\n");
        return cellText.Trim();
    }

    /// <summary>True if the clipboard HTML came from Excel (whose own tab-separated text is already right).</summary>
    public static bool IsFromExcel(string clipboardHtml) =>
        clipboardHtml.Contains("urn:schemas-microsoft-com:office:excel", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Wraps an HTML fragment in the CF_HTML header Windows expects on the clipboard.
    /// The header gives byte offsets (in UTF-8) of the HTML and of the fragment inside it.
    /// </summary>
    public static string WrapForClipboard(string htmlFragment)
    {
        // The header has fixed-width numbers, so its length is known before the numbers are filled in.
        const string HeaderTemplate =
            "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string HtmlStart = "<html><body>\r\n<!--StartFragment-->";
        const string HtmlEnd = "<!--EndFragment-->\r\n</body></html>";

        int headerByteCount = Encoding.UTF8.GetByteCount(string.Format(HeaderTemplate, 0, 0, 0, 0));
        int startHtmlOffset = headerByteCount;
        int startFragmentOffset = startHtmlOffset + Encoding.UTF8.GetByteCount(HtmlStart);
        int endFragmentOffset = startFragmentOffset + Encoding.UTF8.GetByteCount(htmlFragment);
        int endHtmlOffset = endFragmentOffset + Encoding.UTF8.GetByteCount(HtmlEnd);

        return string.Format(HeaderTemplate, startHtmlOffset, endHtmlOffset, startFragmentOffset, endFragmentOffset)
            + HtmlStart + htmlFragment + HtmlEnd;
    }
}
