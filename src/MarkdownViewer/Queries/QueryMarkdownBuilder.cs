using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Queries;

/// <summary>
/// Turns the result of a query run into the Markdown document that the viewer shows:
/// <list type="bullet">
/// <item>Markdown mode: the first column of every row, exactly as returned, one row per line;</item>
/// <item>Table mode without a template: a title, a line saying how many rows there are, and one Markdown table per result set;</item>
/// <item>Table mode with a row template: the template once per row, with {ColumnName} replaced by the row's values.</item>
/// </list>
/// </summary>
public static partial class QueryMarkdownBuilder
{
    /// <summary>Finds {ColumnName} placeholders in a row template.</summary>
    [GeneratedRegex(@"\{([^{}\r\n]+)\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>Builds the Markdown document for a query result.</summary>
    public static string Build(SavedQuery savedQuery, QueryRunResult runResult)
    {
        string documentText = savedQuery.ResultMode switch
        {
            QueryResultMode.Markdown => BuildFromMarkdownColumn(runResult),
            _ when !string.IsNullOrWhiteSpace(savedQuery.RowTemplate) => BuildFromTemplate(savedQuery.RowTemplate, runResult),
            _ => BuildTables(savedQuery, runResult),
        };

        // Never cut rows off silently.
        if (runResult.WasCutShortAtRowLimit)
        {
            documentText = documentText.TrimEnd() + "\n\n---\n\n"
                + $"_Only the first {savedQuery.MaximumRowCount:N0} rows are shown (the row limit of this query). "
                + "Change the limit under Edit query to see more._\n";
        }
        return documentText;
    }

    /// <summary>A short document shown when the query could not be run, so the viewer is not left showing the previous document.</summary>
    public static string BuildErrorDocument(SavedQuery savedQuery, string problemTitle, string problemDetails)
    {
        var errorBuilder = new StringBuilder();
        errorBuilder.AppendLine($"# {EscapeHeadingText(savedQuery.Name)}");
        errorBuilder.AppendLine();
        errorBuilder.AppendLine($"**{problemTitle}**");
        errorBuilder.AppendLine();
        // In a code block, so server messages with * or _ in them are shown as they are.
        errorBuilder.AppendLine("```text");
        errorBuilder.AppendLine(problemDetails.Replace("```", "'''"));
        errorBuilder.AppendLine("```");
        errorBuilder.AppendLine();
        errorBuilder.AppendLine("Right-click the query and choose *Edit query...* (or press Ctrl+E) to change it, and F5 to run it again.");
        return errorBuilder.ToString();
    }

    // ------------------------------------------------------------------
    // The three kinds of document
    // ------------------------------------------------------------------

    /// <summary>Markdown mode: the first column of each row, as is, joined with line breaks. NULL rows become empty lines.</summary>
    private static string BuildFromMarkdownColumn(QueryRunResult runResult)
    {
        var markdownLines = runResult.ResultTables
            .SelectMany(resultTable => resultTable.Rows)
            .Select(rowValues => rowValues.Length > 0 ? FormatValue(rowValues[0]) : string.Empty);
        return string.Join("\n", markdownLines);
    }

    /// <summary>Template mode: the row template once per row, with a blank line between rows.</summary>
    private static string BuildFromTemplate(string rowTemplate, QueryRunResult runResult)
    {
        // Templates are typed in a Windows text box, so normalise line endings first.
        string normalisedTemplate = rowTemplate.Replace("\r\n", "\n");
        var rowTexts = new List<string>();

        foreach (var resultTable in runResult.ResultTables)
        {
            // Column name → position, ignoring upper/lower case (the first column wins if two have the same name).
            var columnIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int columnIndex = 0; columnIndex < resultTable.ColumnNames.Count; columnIndex++)
            {
                columnIndexByName.TryAdd(resultTable.ColumnNames[columnIndex], columnIndex);
            }

            foreach (var rowValues in resultTable.Rows)
            {
                // Unknown placeholders are left as they are, so a typo is easy to spot in the result.
                string rowText = PlaceholderPattern().Replace(normalisedTemplate, placeholderMatch =>
                    columnIndexByName.TryGetValue(placeholderMatch.Groups[1].Value.Trim(), out int columnIndex)
                        ? FormatValue(rowValues[columnIndex])
                        : placeholderMatch.Value);
                rowTexts.Add(rowText.TrimEnd());
            }
        }

        return rowTexts.Count == 0 ? "_The query returned no rows._" : string.Join("\n\n", rowTexts);
    }

    /// <summary>Table mode: a title, a summary line, and a Markdown table for each result set.</summary>
    private static string BuildTables(SavedQuery savedQuery, QueryRunResult runResult)
    {
        var documentBuilder = new StringBuilder();
        documentBuilder.AppendLine($"# {EscapeHeadingText(savedQuery.Name)}");
        documentBuilder.AppendLine();

        string rowCountText = runResult.TotalRowCount == 1 ? "1 row" : $"{runResult.TotalRowCount:N0} rows";
        string sourceText = string.IsNullOrWhiteSpace(savedQuery.DatabaseName)
            ? savedQuery.ServerName
            : $"{savedQuery.ServerName} / {savedQuery.DatabaseName}";
        documentBuilder.AppendLine($"_{rowCountText} · {EscapeTableCell(sourceText)} · run {runResult.RanAt:yyyy-MM-dd HH:mm:ss} in {runResult.Duration.TotalSeconds:0.0} s_");
        documentBuilder.AppendLine();

        if (runResult.ResultTables.Count == 0)
        {
            documentBuilder.AppendLine("_The query returned no result._");
            return documentBuilder.ToString();
        }

        for (int tableIndex = 0; tableIndex < runResult.ResultTables.Count; tableIndex++)
        {
            // Only number the results when there are several.
            if (runResult.ResultTables.Count > 1)
            {
                documentBuilder.AppendLine($"## Result {tableIndex + 1}");
                documentBuilder.AppendLine();
            }
            AppendTable(documentBuilder, runResult.ResultTables[tableIndex]);
            documentBuilder.AppendLine();
        }
        return documentBuilder.ToString();
    }

    /// <summary>Writes one result set as a Markdown table. Columns holding only numbers are right-aligned.</summary>
    private static void AppendTable(StringBuilder documentBuilder, QueryResultTable resultTable)
    {
        if (resultTable.Rows.Count == 0)
        {
            documentBuilder.AppendLine($"_No rows (columns: {EscapeTableCell(string.Join(", ", resultTable.ColumnNames))})._");
            return;
        }

        documentBuilder.AppendLine("| " + string.Join(" | ", resultTable.ColumnNames.Select(EscapeTableCell)) + " |");

        var alignmentMarkers = Enumerable.Range(0, resultTable.ColumnNames.Count)
            .Select(columnIndex => IsNumericColumn(resultTable, columnIndex) ? "---:" : "---");
        documentBuilder.AppendLine("| " + string.Join(" | ", alignmentMarkers) + " |");

        foreach (var rowValues in resultTable.Rows)
        {
            documentBuilder.AppendLine("| " + string.Join(" | ", rowValues.Select(cellValue => EscapeTableCell(FormatValue(cellValue)))) + " |");
        }
    }

    // ------------------------------------------------------------------
    // Formatting values
    // ------------------------------------------------------------------

    /// <summary>True if every non-NULL value in the column is a number (and there is at least one).</summary>
    private static bool IsNumericColumn(QueryResultTable resultTable, int columnIndex)
    {
        var nonNullValues = resultTable.Rows.Select(rowValues => rowValues[columnIndex]).Where(cellValue => cellValue != null).ToList();
        return nonNullValues.Count > 0 && nonNullValues.All(cellValue => cellValue is byte or short or int or long or decimal or double or float);
    }

    /// <summary>
    /// Turns a database value into text: NULL is empty, dates are written as 2026-11-06 (with the time only when
    /// there is one), numbers in the computer's own format, and binary data as a short description.
    /// </summary>
    public static string FormatValue(object? cellValue)
    {
        return cellValue switch
        {
            null => string.Empty,
            string textValue => textValue,
            DateTime dateValue when dateValue.TimeOfDay == TimeSpan.Zero => dateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dateTimeValue => dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffsetValue => dateTimeOffsetValue.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            bool booleanValue => booleanValue ? "Yes" : "No",
            byte[] binaryValue => $"(binary, {binaryValue.Length:N0} bytes)",
            IFormattable formattableValue => formattableValue.ToString(null, CultureInfo.CurrentCulture),
            _ => cellValue.ToString() ?? string.Empty,
        };
    }

    /// <summary>Makes text safe inside a table cell: a "|" would start a new cell and a line break would end the row.</summary>
    private static string EscapeTableCell(string cellText)
    {
        return cellText
            .Replace("\\", "\\\\")
            .Replace("|", "\\|")
            .Replace("\r\n", " ")
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
    }

    /// <summary>Keeps a query name on one heading line.</summary>
    private static string EscapeHeadingText(string headingText)
    {
        string singleLine = headingText.Replace("\r", " ").Replace("\n", " ").Trim();
        return singleLine.Length == 0 ? "Query" : singleLine;
    }
}
