using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Tables;

/// <summary>The kinds of table text the converter can read.</summary>
public enum TableInputFormat
{
    /// <summary>Guess the format from the text (see <see cref="TableTextConverter.DetectFormat"/>).</summary>
    Automatic,

    /// <summary>A Markdown table: pipe-separated cells, usually with a | --- | line under the header.</summary>
    Markdown,

    /// <summary>Comma- or semicolon-separated values (the separator is guessed from the first line).</summary>
    Csv,

    /// <summary>Tab-separated values, which is what Excel puts on the clipboard.</summary>
    TabSeparated,
}

/// <summary>What <see cref="TableTextConverter.DetectFormat"/> found.</summary>
/// <param name="Format">Markdown, CSV or tab-separated (never Automatic).</param>
/// <param name="CsvSeparator">The separator to use if the format is CSV: ',' or ';'.</param>
public readonly record struct DetectedTableFormat(TableInputFormat Format, char CsvSeparator);

/// <summary>
/// Turns pasted table text (Markdown, CSV, Excel or Word cells) into a grid of cells, and the grid back into
/// a Markdown table, CSV, tab-separated text or an HTML table.
/// Ported from the standalone table-converter.html tool; no WPF here, so it is easy to reason about on its own.
/// </summary>
public static partial class TableTextConverter
{
    /// <summary>A Markdown separator line looks like | --- | :---: | ---: |</summary>
    [GeneratedRegex(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$")]
    private static partial Regex MarkdownSeparatorLineRegex();

    /// <summary>A line that starts and ends with a pipe (a Markdown table row with outer pipes).</summary>
    [GeneratedRegex(@"^\s*\|.*\|\s*$")]
    private static partial Regex LineWrappedInPipesRegex();

    /// <summary>&lt;br&gt;, &lt;br/&gt; or &lt;br /&gt;: how a line break inside a Markdown table cell is written.</summary>
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlLineBreakRegex();

    /// <summary>Splits text into lines, whatever line endings it has.</summary>
    private static string[] SplitLines(string rawText) => rawText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    /// <summary>True if the line is a Markdown table separator line (| --- | --- |).</summary>
    public static bool IsMarkdownSeparatorLine(string lineText) =>
        lineText.Contains('-') && MarkdownSeparatorLineRegex().IsMatch(lineText);

    // ------------------------------------------------------------------
    // Reading input
    // ------------------------------------------------------------------

    /// <summary>
    /// Counts how often a separator character appears in a line, ignoring any inside double quotes
    /// (so "Smith, John" counts as no commas).
    /// </summary>
    private static int CountSeparatorOutsideQuotes(string lineText, char separatorCharacter)
    {
        int separatorCount = 0;
        bool insideQuotes = false;
        foreach (char character in lineText)
        {
            if (character == '"')
            {
                insideQuotes = !insideQuotes;
            }
            else if (character == separatorCharacter && !insideQuotes)
            {
                separatorCount++;
            }
        }
        return separatorCount;
    }

    /// <summary>
    /// Guesses the format from the text:
    /// Markdown when lines have pipes and a --- separator line, or every line is wrapped in pipes;
    /// tab-separated when the first line has a tab (Excel's clipboard text);
    /// CSV otherwise, with semicolons winning over commas when the first line has more of them
    /// (Swedish Excel saves CSV with semicolons).
    /// </summary>
    public static DetectedTableFormat DetectFormat(string rawText)
    {
        var nonEmptyLines = SplitLines(rawText).Where(lineText => lineText.Trim().Length > 0).ToList();
        string firstLine = nonEmptyLines.FirstOrDefault() ?? string.Empty;

        int pipeLineCount = nonEmptyLines.Count(lineText => lineText.Contains('|'));
        bool hasSeparatorLine = nonEmptyLines.Any(IsMarkdownSeparatorLine);
        bool allLinesWrappedInPipes = nonEmptyLines.Count > 0 && nonEmptyLines.All(lineText => LineWrappedInPipesRegex().IsMatch(lineText));
        if ((hasSeparatorLine && pipeLineCount >= 2) || allLinesWrappedInPipes)
        {
            return new DetectedTableFormat(TableInputFormat.Markdown, ',');
        }

        if (firstLine.Contains('\t'))
        {
            return new DetectedTableFormat(TableInputFormat.TabSeparated, ',');
        }

        int commaCount = CountSeparatorOutsideQuotes(firstLine, ',');
        int semicolonCount = CountSeparatorOutsideQuotes(firstLine, ';');
        return new DetectedTableFormat(TableInputFormat.Csv, semicolonCount > commaCount ? ';' : ',');
    }

    /// <summary>
    /// Parses delimited text (CSV or tab-separated) into rows of cells.
    /// Follows the usual CSV rules: a cell in double quotes may contain the separator and line breaks,
    /// and "" inside quotes is one quote character. Excel quotes cells with line breaks the same way when copying.
    /// Completely empty rows (blank lines) are dropped.
    /// </summary>
    public static List<List<string>> ParseDelimitedText(string rawText, char separatorCharacter)
    {
        var parsedRows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentCell = new StringBuilder();
        bool insideQuotes = false;

        for (int characterPosition = 0; characterPosition < rawText.Length; characterPosition++)
        {
            char character = rawText[characterPosition];
            bool hasNextCharacter = characterPosition + 1 < rawText.Length;

            if (insideQuotes)
            {
                if (character == '"')
                {
                    if (hasNextCharacter && rawText[characterPosition + 1] == '"')
                    {
                        // Doubled quote inside a quoted cell = one literal quote.
                        currentCell.Append('"');
                        characterPosition++;
                    }
                    else
                    {
                        insideQuotes = false;
                    }
                }
                else
                {
                    currentCell.Append(character);
                }
            }
            else if (character == '"' && currentCell.ToString().Trim().Length == 0)
            {
                // A quote at the start of a cell opens a quoted cell.
                currentCell.Clear();
                insideQuotes = true;
            }
            else if (character == separatorCharacter)
            {
                currentRow.Add(currentCell.ToString());
                currentCell.Clear();
            }
            else if (character == '\r' || character == '\n')
            {
                // End of row; \r\n counts as one line break.
                if (character == '\r' && hasNextCharacter && rawText[characterPosition + 1] == '\n')
                {
                    characterPosition++;
                }
                currentRow.Add(currentCell.ToString());
                parsedRows.Add(currentRow);
                currentRow = [];
                currentCell.Clear();
            }
            else
            {
                currentCell.Append(character);
            }
        }

        // Last row (the text usually does not end with a line break).
        currentRow.Add(currentCell.ToString());
        parsedRows.Add(currentRow);

        return parsedRows.Where(rowCells => rowCells.Any(cellText => cellText.Trim().Length > 0)).ToList();
    }

    /// <summary>
    /// Splits one Markdown table line into cells. Pipes escaped as \| stay in the cell text;
    /// the outer pipes at the start and end are optional. &lt;br&gt; becomes a real line break.
    /// </summary>
    private static List<string> SplitMarkdownLine(string lineText)
    {
        string trimmedLine = lineText.Trim();
        if (trimmedLine.StartsWith('|'))
        {
            trimmedLine = trimmedLine[1..];
        }
        if (trimmedLine.EndsWith('|') && !trimmedLine.EndsWith("\\|", StringComparison.Ordinal))
        {
            trimmedLine = trimmedLine[..^1];
        }

        var lineCells = new List<string>();
        var currentCell = new StringBuilder();
        for (int characterPosition = 0; characterPosition < trimmedLine.Length; characterPosition++)
        {
            char character = trimmedLine[characterPosition];
            if (character == '\\' && characterPosition + 1 < trimmedLine.Length && trimmedLine[characterPosition + 1] == '|')
            {
                currentCell.Append('|');
                characterPosition++;
            }
            else if (character == '|')
            {
                lineCells.Add(currentCell.ToString());
                currentCell.Clear();
            }
            else
            {
                currentCell.Append(character);
            }
        }
        lineCells.Add(currentCell.ToString());

        return lineCells.Select(cellText => HtmlLineBreakRegex().Replace(cellText, "\n")).ToList();
    }

    /// <summary>Parses a Markdown table: every line with a pipe is a row; separator lines are skipped.</summary>
    public static List<List<string>> ParseMarkdown(string rawText) =>
        SplitLines(rawText)
            .Where(lineText => lineText.Contains('|') && !IsMarkdownSeparatorLine(lineText))
            .Select(SplitMarkdownLine)
            .ToList();

    /// <summary>Gives every row the same number of cells by adding empty cells at the end.</summary>
    public static List<List<string>> PadRowsToSameWidth(List<List<string>> tableGrid)
    {
        int columnCount = tableGrid.Count == 0 ? 0 : tableGrid.Max(rowCells => rowCells.Count);
        return tableGrid
            .Select(rowCells => rowCells.Concat(Enumerable.Repeat(string.Empty, columnCount - rowCells.Count)).ToList())
            .ToList();
    }

    /// <summary>Turns rows into columns and columns into rows. Expects rows of the same width.</summary>
    public static List<List<string>> SwapRowsAndColumns(List<List<string>> tableGrid)
    {
        if (tableGrid.Count == 0)
        {
            return [];
        }
        return Enumerable.Range(0, tableGrid[0].Count)
            .Select(columnIndex => tableGrid.Select(rowCells => rowCells[columnIndex]).ToList())
            .ToList();
    }

    // ------------------------------------------------------------------
    // Writing output
    // ------------------------------------------------------------------

    /// <summary>
    /// The header row to use: the first row, or "Column 1, Column 2, ..." when the table has none
    /// (Markdown always needs a header row).
    /// </summary>
    private static (List<string> HeaderCells, List<List<string>> BodyRows) SplitHeaderAndBody(List<List<string>> tableGrid, bool hasHeaderRow)
    {
        if (hasHeaderRow)
        {
            return (tableGrid[0], tableGrid.Skip(1).ToList());
        }
        var generatedHeader = Enumerable.Range(1, tableGrid[0].Count).Select(columnNumber => $"Column {columnNumber}").ToList();
        return (generatedHeader, tableGrid);
    }

    /// <summary>Makes text safe for a Markdown table cell: line breaks become &lt;br&gt; and pipes are escaped.</summary>
    private static string EscapeMarkdownCell(string cellText) =>
        cellText.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>").Replace("|", "\\|");

    /// <summary>
    /// Builds a Markdown table: pipe-separated rows with a --- line under the header.
    /// With <paramref name="lineUpColumns"/>, cells are padded with spaces so the columns line up in the editor.
    /// </summary>
    /// <param name="tableGrid">The cells; all rows the same width.</param>
    /// <param name="hasHeaderRow">True if the first row is the header; otherwise "Column 1", "Column 2"... is generated.</param>
    /// <param name="lineUpColumns">Pad cells so the pipes line up.</param>
    /// <param name="lineEnding">Line ending to use between rows (the editor uses \r\n on Windows).</param>
    public static string ToMarkdown(List<List<string>> tableGrid, bool hasHeaderRow, bool lineUpColumns, string lineEnding = "\n")
    {
        if (tableGrid.Count == 0 || tableGrid[0].Count == 0)
        {
            return string.Empty;
        }

        var (headerCells, bodyRows) = SplitHeaderAndBody(tableGrid, hasHeaderRow);
        var escapedRows = new[] { headerCells }.Concat(bodyRows)
            .Select(rowCells => rowCells.Select(EscapeMarkdownCell).ToList())
            .ToList();

        // Widest cell in each column (at least 3, the length of "---").
        var columnWidths = Enumerable.Range(0, headerCells.Count)
            .Select(columnIndex => lineUpColumns ? Math.Max(3, escapedRows.Max(rowCells => rowCells[columnIndex].Length)) : 3)
            .ToList();

        string ToMarkdownLine(List<string> rowCells) =>
            "| " + string.Join(" | ", rowCells.Select((cellText, columnIndex) =>
                lineUpColumns ? cellText.PadRight(columnWidths[columnIndex]) : cellText)) + " |";

        string separatorLine = "| " + string.Join(" | ", columnWidths.Select(columnWidth => new string('-', columnWidth))) + " |";

        var markdownLines = new List<string> { ToMarkdownLine(escapedRows[0]), separatorLine };
        markdownLines.AddRange(escapedRows.Skip(1).Select(ToMarkdownLine));
        return string.Join(lineEnding, markdownLines);
    }

    /// <summary>
    /// Builds CSV: cells that contain the separator, a quote, a line break or leading/trailing spaces
    /// are put in double quotes, with quotes inside doubled.
    /// </summary>
    public static string ToCsv(List<List<string>> tableGrid, char separatorCharacter)
    {
        string QuoteIfNeeded(string cellText)
        {
            bool needsQuotes = cellText.Contains(separatorCharacter) || cellText.IndexOfAny(['"', '\r', '\n']) >= 0 ||
                cellText != cellText.Trim();
            return needsQuotes ? "\"" + cellText.Replace("\"", "\"\"") + "\"" : cellText;
        }
        return string.Join("\r\n", tableGrid.Select(rowCells => string.Join(separatorCharacter, rowCells.Select(QuoteIfNeeded))));
    }

    /// <summary>
    /// Builds tab-separated text, which Excel pastes as one value per cell.
    /// Cells with line breaks or quotes are quoted the way Excel does it, so they stay in one cell.
    /// </summary>
    public static string ToTabSeparated(List<List<string>> tableGrid)
    {
        static string QuoteIfNeeded(string cellText)
        {
            string cleanText = cellText.Replace('\t', ' ');
            return cleanText.IndexOfAny(['"', '\r', '\n']) >= 0 ? "\"" + cleanText.Replace("\"", "\"\"") + "\"" : cleanText;
        }
        return string.Join("\r\n", tableGrid.Select(rowCells => string.Join('\t', rowCells.Select(QuoteIfNeeded))));
    }

    /// <summary>Escapes text for HTML markup; line breaks become &lt;br&gt;.</summary>
    private static string EscapeHtml(string rawText) =>
        WebUtility.HtmlEncode(rawText).Replace("\r\n", "<br>").Replace("\n", "<br>").Replace("\r", "<br>");

    /// <summary>
    /// Builds an HTML table with simple inline borders. Word and Excel both paste this as a real table.
    /// mso-number-format:"\@" makes Excel keep cells as text, so values like 007 or 1-2 do not become numbers or dates.
    /// </summary>
    public static string ToHtmlTable(List<List<string>> tableGrid, bool hasHeaderRow)
    {
        const string CellStyle = "border:1px solid #999;padding:4px 8px;mso-number-format:'\\@';vertical-align:top;";
        var headerRows = hasHeaderRow ? tableGrid.Take(1) : [];
        var bodyRows = hasHeaderRow ? tableGrid.Skip(1) : tableGrid;

        var htmlBuilder = new StringBuilder("<table style=\"border-collapse:collapse;\"><thead>");
        foreach (var rowCells in headerRows)
        {
            htmlBuilder.Append("<tr>");
            foreach (string cellText in rowCells)
            {
                htmlBuilder.Append($"<th style=\"{CellStyle}background:#eee;text-align:left;\">{EscapeHtml(cellText)}</th>");
            }
            htmlBuilder.Append("</tr>");
        }
        htmlBuilder.Append("</thead><tbody>");
        foreach (var rowCells in bodyRows)
        {
            htmlBuilder.Append("<tr>");
            foreach (string cellText in rowCells)
            {
                htmlBuilder.Append($"<td style=\"{CellStyle}\">{EscapeHtml(cellText)}</td>");
            }
            htmlBuilder.Append("</tr>");
        }
        htmlBuilder.Append("</tbody></table>");
        return htmlBuilder.ToString();
    }
}
