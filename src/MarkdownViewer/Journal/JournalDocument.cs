using System.Globalization;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Journal;

/// <summary>
/// Text operations on the journal: one plain Markdown file (journal.md next to the exe) where every entry is a
/// "### yyyy-MM-dd HH:mm" heading with the note below it, newest entry at the top.
/// Only the new entry is added: everything else in the file (the user's own edits, order and line endings) is kept.
/// </summary>
public static class JournalDocument
{
    /// <summary>File name of the journal, stored in the same folder as the exe (like todo.md).</summary>
    public const string FileName = "journal.md";

    /// <summary>Title written at the top when the journal is created.</summary>
    public const string DocumentTitle = "# Journal";

    /// <summary>How the date and time are written in an entry's heading, e.g. "2026-11-11 18:07" (sorts correctly as text too).</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm";

    /// <summary>
    /// An entry heading: a level 3 heading whose text starts with a date, e.g. "### 2026-11-11 18:07".
    /// Other ### headings (for example inside a note) are not mistaken for entries.
    /// </summary>
    private static readonly Regex EntryHeadingRegex = new(@"^ {0,3}###[ \t]+\d{4}-\d{2}-\d{2}");

    /// <summary>
    /// Adds a new entry and returns the new document text. The entry goes directly above the first existing entry,
    /// so the newest is always on top and any text the user put above the entries (such as the title) stays there.
    /// If the journal has no entries yet, the entry is added at the end (after a "# Journal" title if the file is empty).
    /// </summary>
    /// <param name="documentText">Current text of journal.md (may be empty).</param>
    /// <param name="entryTime">Date and time stamped in the entry's heading.</param>
    /// <param name="entryText">The note. It may span several lines; it is written below the heading as typed.</param>
    public static string InsertEntry(string documentText, DateTime entryTime, string entryText)
    {
        string lineBreak = DetectLineBreak(documentText);
        string headingLine = "### " + entryTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        // Write the note with the document's own line breaks, without blank lines dangling at its start or end.
        string[] noteLines = entryText.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n').Split('\n');
        string noteText = string.Join(lineBreak, noteLines.Select(noteLine => noteLine.TrimEnd()));

        // Heading, empty line, note: the entry block without anything after it.
        string entryBlock = headingLine + lineBreak + lineBreak + noteText;

        int firstEntryIndex = FindFirstEntryHeadingIndex(documentText);
        if (firstEntryIndex >= 0)
        {
            // Above the newest existing entry, with one empty line between the two entries.
            return documentText.Insert(firstEntryIndex, entryBlock + lineBreak + lineBreak);
        }

        if (documentText.Trim().Length == 0)
        {
            // A new (or empty) journal: start it with a title.
            return DocumentTitle + lineBreak + lineBreak + entryBlock + lineBreak;
        }

        // Text but no entries yet: add the entry at the end, separated from the earlier text by an empty line.
        bool endsWithLineBreak = documentText.EndsWith('\n') || documentText.EndsWith('\r');
        string separator = (endsWithLineBreak ? string.Empty : lineBreak) + lineBreak;
        return documentText + separator + entryBlock + lineBreak;
    }

    /// <summary>
    /// Character index where the first entry heading line starts, or -1 if there is none.
    /// Lines inside fenced code blocks (``` or ~~~) are skipped, because a heading there is just text.
    /// </summary>
    private static int FindFirstEntryHeadingIndex(string documentText)
    {
        bool isInsideCodeFence = false;
        int lineStartIndex = 0;
        while (lineStartIndex < documentText.Length)
        {
            // The line runs to the next "\n" or "\r" (or to the end of the text).
            int lineEndIndex = documentText.IndexOfAny(new[] { '\r', '\n' }, lineStartIndex);
            if (lineEndIndex < 0)
            {
                lineEndIndex = documentText.Length;
            }
            string lineText = documentText[lineStartIndex..lineEndIndex];

            string trimmedLine = lineText.TrimStart();
            if (trimmedLine.StartsWith("```") || trimmedLine.StartsWith("~~~"))
            {
                isInsideCodeFence = !isInsideCodeFence;
            }
            else if (!isInsideCodeFence && EntryHeadingRegex.IsMatch(lineText))
            {
                return lineStartIndex;
            }

            // Skip the line break: "\r\n" counts as one break, a lone "\r" or "\n" as one too.
            int nextLineStartIndex = lineEndIndex + 1;
            if (lineEndIndex < documentText.Length && documentText[lineEndIndex] == '\r'
                && nextLineStartIndex < documentText.Length && documentText[nextLineStartIndex] == '\n')
            {
                nextLineStartIndex++;
            }
            lineStartIndex = nextLineStartIndex;
        }
        return -1;
    }

    /// <summary>The document's own line break ("\r\n" or "\n"), or the Windows default for an empty document.</summary>
    private static string DetectLineBreak(string documentText)
    {
        int firstNewlineIndex = documentText.IndexOf('\n');
        if (firstNewlineIndex < 0)
        {
            return documentText.Contains('\r') ? "\r" : Environment.NewLine;
        }
        return firstNewlineIndex > 0 && documentText[firstNewlineIndex - 1] == '\r' ? "\r\n" : "\n";
    }
}
