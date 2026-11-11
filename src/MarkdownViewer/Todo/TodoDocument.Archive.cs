using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Todo;

/// <summary>
/// Done tasks taken out of the todo list, grouped by the section (heading) they were under.
/// </summary>
/// <param name="SectionHeadingText">Text of the heading above the tasks (e.g. "Todo"), or null for tasks above the first heading.</param>
/// <param name="TaskLines">The task lines, each with the lines indented below it (sub-tasks, notes), exactly as they were.</param>
public sealed record ArchivedTaskGroup(string? SectionHeadingText, List<string> TaskLines);

/// <summary>
/// "Todo → Archive done tasks": takes the ticked tasks out of todo.md and adds them to todo-archive.md in the Archive
/// folder, newest batch first, under a dated heading and the section they came from.
/// </summary>
public static partial class TodoDocument
{
    /// <summary>Title line at the top of todo-archive.md.</summary>
    public const string ArchiveTitleLine = "# Todo archive";

    /// <summary>A ticked task line: optional indent, a bullet (-, * or +), then "[x]" or "[X]".</summary>
    private static readonly Regex CompletedTaskLineRegex = new(@"^(?<indent>[ \t]*)[-*+][ \t]+\[[xX]\]");

    /// <summary>A tab counts as this many spaces when comparing how far lines are indented.</summary>
    private const int TabWidth = 4;

    /// <summary>
    /// Takes every ticked task out of the document, together with the lines indented below it (its sub-tasks and notes,
    /// which belong to it, also unticked ones). A ticked sub-task below an unticked task is taken out on its own; the
    /// unticked parent stays. Tasks in code blocks are left alone. All other lines stay exactly as they were.
    /// </summary>
    /// <returns>The document without the done tasks, the tasks grouped by section, and how many tasks were taken out.</returns>
    public static (string RemainingText, List<ArchivedTaskGroup> TaskGroups, int TaskCount) TakeOutCompletedTasks(string documentText)
    {
        var documentLines = SplitIntoLines(documentText);
        var remainingText = new StringBuilder(documentText.Length);
        var taskGroups = new List<ArchivedTaskGroup>();
        int completedTaskCount = 0;
        string? currentSectionHeadingText = null;
        bool isInsideCodeFence = false;

        int lineIndex = 0;
        while (lineIndex < documentLines.Count)
        {
            var documentLine = documentLines[lineIndex];

            // Code blocks are kept as they are, whatever they contain.
            if (IsCodeFenceLine(documentLine.Text))
            {
                isInsideCodeFence = !isInsideCodeFence;
            }
            else if (!isInsideCodeFence)
            {
                var headingMatch = HeadingLineRegex.Match(documentLine.Text);
                if (headingMatch.Success)
                {
                    currentSectionHeadingText = headingMatch.Groups["text"].Value.Trim();
                }

                var completedTaskMatch = CompletedTaskLineRegex.Match(documentLine.Text);
                if (completedTaskMatch.Success)
                {
                    // The task plus every following non-empty line that is indented further than it.
                    // A sub-task is moved left by its own indent, so it starts at the left edge in the archive.
                    string taskIndentText = completedTaskMatch.Groups["indent"].Value;
                    int taskIndentWidth = MeasureIndentWidth(taskIndentText);
                    var taskLines = new List<string> { RemoveIndent(documentLine.Text, taskIndentText) };
                    int followingLineIndex = lineIndex + 1;
                    while (followingLineIndex < documentLines.Count
                        && documentLines[followingLineIndex].Text.Trim().Length > 0
                        && MeasureIndentWidth(GetLeadingWhitespace(documentLines[followingLineIndex].Text)) > taskIndentWidth)
                    {
                        taskLines.Add(RemoveIndent(documentLines[followingLineIndex].Text, taskIndentText));
                        followingLineIndex++;
                    }

                    AddToTaskGroups(taskGroups, currentSectionHeadingText, taskLines);
                    completedTaskCount++;
                    lineIndex = followingLineIndex;
                    continue;
                }
            }

            // Any other line is kept, with its own line break.
            remainingText.Append(documentLine.Text).Append(documentLine.LineBreak);
            lineIndex++;
        }

        return (remainingText.ToString(), taskGroups, completedTaskCount);
    }

    /// <summary>
    /// Adds a batch of done tasks to the text of todo-archive.md (which may be empty) and returns the new text.
    /// The batch goes right below the "# Todo archive" title, so the newest batch is always at the top:
    /// <code>
    /// # Todo archive
    ///
    /// ## Archived 2026-11-11 16:30
    ///
    /// ### Todo
    ///
    /// - [x] Write the report
    /// </code>
    /// </summary>
    public static string AddToArchive(string archiveText, IReadOnlyList<ArchivedTaskGroup> taskGroups, DateTime archivedOn)
    {
        string lineBreak = archiveText.Length > 0 ? DetectLineBreak(archiveText) : "\r\n";

        // The new batch: a dated heading, then each group under the heading of the section it came from.
        var batchText = new StringBuilder();
        batchText.Append($"## Archived {archivedOn:yyyy-MM-dd HH:mm}").Append(lineBreak).Append(lineBreak);
        foreach (var taskGroup in taskGroups)
        {
            if (!string.IsNullOrWhiteSpace(taskGroup.SectionHeadingText))
            {
                batchText.Append("### ").Append(taskGroup.SectionHeadingText).Append(lineBreak).Append(lineBreak);
            }
            foreach (string taskLine in taskGroup.TaskLines)
            {
                batchText.Append(taskLine).Append(lineBreak);
            }
            batchText.Append(lineBreak);
        }

        // Keep the title on top and put the batch right below it; the older batches follow.
        var archiveLines = SplitIntoLines(archiveText);
        bool startsWithTitle = archiveLines.Count > 0
            && string.Equals(archiveLines[0].Text.Trim(), ArchiveTitleLine, StringComparison.OrdinalIgnoreCase);
        string olderBatchesText = startsWithTitle
            ? archiveText[(archiveLines[0].StartIndex + archiveLines[0].Text.Length + archiveLines[0].LineBreak.Length)..]
            : archiveText;
        string titleLine = startsWithTitle ? archiveLines[0].Text : ArchiveTitleLine;

        return titleLine + lineBreak + lineBreak + batchText + olderBatchesText.TrimStart('\r', '\n');
    }

    /// <summary>Adds task lines to the last group if they come from the same section, otherwise starts a new group.</summary>
    private static void AddToTaskGroups(List<ArchivedTaskGroup> taskGroups, string? sectionHeadingText, List<string> taskLines)
    {
        var lastGroup = taskGroups.Count > 0 ? taskGroups[^1] : null;
        if (lastGroup != null && string.Equals(lastGroup.SectionHeadingText, sectionHeadingText, StringComparison.Ordinal))
        {
            lastGroup.TaskLines.AddRange(taskLines);
        }
        else
        {
            taskGroups.Add(new ArchivedTaskGroup(sectionHeadingText, taskLines));
        }
    }

    /// <summary>The line without the given indent at its start (unchanged if it does not start with exactly that indent).</summary>
    private static string RemoveIndent(string lineText, string indentText)
    {
        return indentText.Length > 0 && lineText.StartsWith(indentText, StringComparison.Ordinal) ? lineText[indentText.Length..] : lineText;
    }

    /// <summary>The spaces and tabs at the start of a line.</summary>
    private static string GetLeadingWhitespace(string lineText)
    {
        int firstTextIndex = 0;
        while (firstTextIndex < lineText.Length && (lineText[firstTextIndex] == ' ' || lineText[firstTextIndex] == '\t'))
        {
            firstTextIndex++;
        }
        return lineText[..firstTextIndex];
    }

    /// <summary>How far an indent reaches, counting a tab as <see cref="TabWidth"/> spaces.</summary>
    private static int MeasureIndentWidth(string indentText)
    {
        return indentText.Sum(indentCharacter => indentCharacter == '\t' ? TabWidth : 1);
    }
}
