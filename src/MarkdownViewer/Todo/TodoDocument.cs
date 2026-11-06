using System.Text.RegularExpressions;

namespace MarkdownViewer.Todo;

/// <summary>
/// Text operations on a plain Markdown todo document (todo.md next to the exe).
/// Only the line that is added or the single character that is ticked changes: everything else in the
/// document (the user's own text, order and line endings) is left exactly as it was.
/// </summary>
public static class TodoDocument
{
    /// <summary>File name of the todo document, stored in the same folder as the exe.</summary>
    public const string FileName = "todo.md";

    /// <summary>Section that new tasks go into when the user has not chosen another one.</summary>
    public const string DefaultSectionHeading = "## Todo";

    /// <summary>A task list line: optional indent, a bullet (-, * or +), then "[ ]" or "[x]".</summary>
    private static readonly Regex TaskLineRegex = new(@"^(?<indent>[ \t]*)(?<bullet>[-*+])[ \t]+\[[ xX]\]");

    /// <summary>The check box part of a task line, "[ ]" or "[x]", with the state character as a group.</summary>
    private static readonly Regex TaskBoxRegex = new(@"\[(?<state>[ xX])\]");

    /// <summary>An ATX heading such as "## Todo" or "## Todo ##" (at most 3 spaces of indent, as in CommonMark).</summary>
    private static readonly Regex HeadingLineRegex = new(@"^ {0,3}(?<hashes>#{1,6})(?:[ \t]+(?<text>.*?))?(?:[ \t]+#+)?[ \t]*$");

    /// <summary>One line of the document: where it starts, its text without the line break, and the break itself.</summary>
    private readonly record struct DocumentLine(int StartIndex, string Text, string LineBreak);

    /// <summary>
    /// Adds an unticked task at the top of the given section and returns the new document text.
    /// <list type="bullet">
    /// <item>If the section already has tasks, the new one goes right above the first of them (same indent and bullet),
    /// so any text the user wrote between the heading and the tasks stays above the list.</item>
    /// <item>If the section has no tasks yet, the new task goes directly below the heading.</item>
    /// <item>If the section does not exist, it is added at the end of the document.</item>
    /// </list>
    /// The section runs from its heading to the next heading of any level.
    /// </summary>
    /// <param name="documentText">Current text of the todo document (may be empty).</param>
    /// <param name="sectionHeading">Heading of the section, e.g. "## Todo" (the "#" count is only used when creating it).</param>
    /// <param name="taskText">Text of the new task. Line breaks are turned into spaces.</param>
    public static string InsertTask(string documentText, string sectionHeading, string taskText)
    {
        string singleLineTaskText = string.Join(" ", taskText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        string lineBreak = DetectLineBreak(documentText);
        var (headingLevel, headingText) = ParseSectionHeading(sectionHeading);
        var documentLines = SplitIntoLines(documentText);

        int headingLineIndex = FindSectionHeadingLine(documentLines, headingText);
        if (headingLineIndex < 0)
        {
            return AppendNewSection(documentText, lineBreak, headingLevel, headingText, singleLineTaskText);
        }

        // Look for the first task in the section, i.e. before the next heading.
        bool isInsideCodeFence = false;
        for (int lineIndex = headingLineIndex + 1; lineIndex < documentLines.Count; lineIndex++)
        {
            string lineText = documentLines[lineIndex].Text;
            if (IsCodeFenceLine(lineText))
            {
                isInsideCodeFence = !isInsideCodeFence;
                continue;
            }
            if (isInsideCodeFence)
            {
                continue;
            }
            if (HeadingLineRegex.IsMatch(lineText))
            {
                break; // end of the section
            }

            var taskLineMatch = TaskLineRegex.Match(lineText);
            if (taskLineMatch.Success)
            {
                // Copy the indent and bullet style of the existing list so the new task joins it.
                string newTaskLine = $"{taskLineMatch.Groups["indent"].Value}{taskLineMatch.Groups["bullet"].Value} [ ] {singleLineTaskText}";
                return documentText.Insert(documentLines[lineIndex].StartIndex, newTaskLine + lineBreak);
            }
        }

        // The section has no tasks yet: put the task directly below the heading.
        var headingLine = documentLines[headingLineIndex];
        bool isNextLineBlank = headingLineIndex + 1 >= documentLines.Count || string.IsNullOrWhiteSpace(documentLines[headingLineIndex + 1].Text);
        // A heading on the very last line has no line break after it yet.
        string breakAfterHeading = headingLine.LineBreak.Length == 0 ? lineBreak : string.Empty;
        // An empty line before the task, and one after it when text follows directly (otherwise that text
        // would become part of the task in Markdown).
        string insertedText = breakAfterHeading + lineBreak + $"- [ ] {singleLineTaskText}" + lineBreak + (isNextLineBlank ? string.Empty : lineBreak);
        int insertAtIndex = headingLine.StartIndex + headingLine.Text.Length + headingLine.LineBreak.Length;
        return documentText.Insert(insertAtIndex, insertedText);
    }

    /// <summary>
    /// Ticks or unticks the task on the given line (0-based, counted like Markdig does) and returns the new text,
    /// or null if that line holds no task check box (e.g. the document changed in the meantime).
    /// Only the one character between the brackets changes.
    /// </summary>
    public static string? SetTaskChecked(string documentText, int lineIndex, bool isChecked)
    {
        var documentLines = SplitIntoLines(documentText);
        if (lineIndex < 0 || lineIndex >= documentLines.Count)
        {
            return null;
        }

        var taskLine = documentLines[lineIndex];
        // The first "[ ]" / "[x]" on the line is the check box: the list bullet (and any "> ") come before it.
        var taskBoxMatch = TaskBoxRegex.Match(taskLine.Text);
        if (!taskBoxMatch.Success)
        {
            return null;
        }

        int stateCharacterIndex = taskLine.StartIndex + taskBoxMatch.Groups["state"].Index;
        string newStateCharacter = isChecked ? "x" : " ";
        return documentText.Remove(stateCharacterIndex, 1).Insert(stateCharacterIndex, newStateCharacter);
    }

    /// <summary>
    /// Turns the user's setting into a heading level and text: "## Todo" → (2, "Todo").
    /// Text without "#" counts as a level 2 heading.
    /// </summary>
    public static (int HeadingLevel, string HeadingText) ParseSectionHeading(string sectionHeading)
    {
        string trimmedHeading = (sectionHeading ?? string.Empty).Trim();
        int hashCount = trimmedHeading.TakeWhile(headingCharacter => headingCharacter == '#').Count();
        string headingText = trimmedHeading[hashCount..].Trim().TrimEnd('#').Trim();
        if (headingText.Length == 0)
        {
            headingText = "Todo";
        }

        int headingLevel = hashCount is >= 1 and <= 6 ? hashCount : 2;
        return (headingLevel, headingText);
    }

    /// <summary>Formats a heading setting the same way it would be written into the document, e.g. "## Todo".</summary>
    public static string NormaliseSectionHeading(string sectionHeading)
    {
        var (headingLevel, headingText) = ParseSectionHeading(sectionHeading);
        return new string('#', headingLevel) + " " + headingText;
    }

    /// <summary>Index of the first heading (outside code blocks) whose text matches, ignoring case and level; -1 if none.</summary>
    private static int FindSectionHeadingLine(List<DocumentLine> documentLines, string headingText)
    {
        bool isInsideCodeFence = false;
        for (int lineIndex = 0; lineIndex < documentLines.Count; lineIndex++)
        {
            string lineText = documentLines[lineIndex].Text;
            if (IsCodeFenceLine(lineText))
            {
                isInsideCodeFence = !isInsideCodeFence;
                continue;
            }
            if (isInsideCodeFence)
            {
                continue;
            }

            var headingMatch = HeadingLineRegex.Match(lineText);
            if (headingMatch.Success && string.Equals(headingMatch.Groups["text"].Value.Trim(), headingText, StringComparison.OrdinalIgnoreCase))
            {
                return lineIndex;
            }
        }
        return -1;
    }

    /// <summary>Adds "## Todo" plus the task at the end of the document, separated from earlier text by an empty line.</summary>
    private static string AppendNewSection(string documentText, string lineBreak, int headingLevel, string headingText, string taskText)
    {
        string separator = string.Empty;
        if (documentText.Trim().Length > 0)
        {
            // Make sure the existing text ends with a line break, then leave one empty line.
            bool endsWithLineBreak = documentText.EndsWith('\n') || documentText.EndsWith('\r');
            separator = (endsWithLineBreak ? string.Empty : lineBreak) + lineBreak;
        }

        return documentText + separator
            + new string('#', headingLevel) + " " + headingText + lineBreak
            + lineBreak
            + $"- [ ] {taskText}" + lineBreak;
    }

    /// <summary>True for a line that opens or closes a fenced code block (``` or ~~~), where headings and tasks do not count.</summary>
    private static bool IsCodeFenceLine(string lineText)
    {
        string trimmedLine = lineText.TrimStart();
        return trimmedLine.StartsWith("```") || trimmedLine.StartsWith("~~~");
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

    /// <summary>
    /// Splits the text into lines, remembering where each starts and which line break ends it.
    /// "\r\n", "\n" and a lone "\r" all end a line, the same way Markdig numbers lines.
    /// </summary>
    private static List<DocumentLine> SplitIntoLines(string documentText)
    {
        var documentLines = new List<DocumentLine>();
        int lineStartIndex = 0;
        int characterIndex = 0;
        while (characterIndex < documentText.Length)
        {
            char currentCharacter = documentText[characterIndex];
            if (currentCharacter == '\r' || currentCharacter == '\n')
            {
                bool isWindowsLineBreak = currentCharacter == '\r' && characterIndex + 1 < documentText.Length && documentText[characterIndex + 1] == '\n';
                int lineBreakLength = isWindowsLineBreak ? 2 : 1;
                documentLines.Add(new DocumentLine(
                    lineStartIndex,
                    documentText[lineStartIndex..characterIndex],
                    documentText.Substring(characterIndex, lineBreakLength)));
                characterIndex += lineBreakLength;
                lineStartIndex = characterIndex;
            }
            else
            {
                characterIndex++;
            }
        }

        // The last line, unless the text ended with a line break.
        if (lineStartIndex < documentText.Length)
        {
            documentLines.Add(new DocumentLine(lineStartIndex, documentText[lineStartIndex..], string.Empty));
        }
        return documentLines;
    }
}
