using System.Text;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownViewer.Rendering;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// Counts and facts about the text of a Markdown document (characters, words, headings,
/// links and so on), shown in the properties panel. Contains no file system information:
/// that is read separately, because unsaved documents have text but no file.
/// </summary>
public sealed class DocumentStatistics
{
    /// <summary>Average silent reading speed used for the reading time estimate.</summary>
    private const int WordsReadPerMinute = 230;

    /// <summary>Title of the document: the first level 1 heading, or else the first heading of any level.</summary>
    public string? Title { get; private init; }

    /// <summary>Number of characters, not counting line breaks (an emoji counts as one character).</summary>
    public int CharacterCount { get; private init; }

    /// <summary>Number of characters that are not spaces, tabs or line breaks.</summary>
    public int CharacterCountWithoutSpaces { get; private init; }

    /// <summary>Number of words, see <see cref="CountWords"/>.</summary>
    public int WordCount { get; private init; }

    /// <summary>Number of lines in the text.</summary>
    public int LineCount { get; private init; }

    /// <summary>Number of lines that contain only whitespace.</summary>
    public int BlankLineCount { get; private init; }

    /// <summary>Number of text paragraphs (including paragraphs inside lists and quotes, but not table cells).</summary>
    public int ParagraphCount { get; private init; }

    /// <summary>Number of headings per level: index 0 is level 1 (#), index 5 is level 6 (######).</summary>
    public int[] HeadingCountsByLevel { get; private init; } = new int[6];

    /// <summary>Total number of headings of all levels.</summary>
    public int HeadingCount => HeadingCountsByLevel.Sum();

    /// <summary>Number of links (not counting images).</summary>
    public int LinkCount { get; private init; }

    /// <summary>Number of images.</summary>
    public int ImageCount { get; private init; }

    /// <summary>Number of code blocks (fenced ``` blocks and indented blocks).</summary>
    public int CodeBlockCount { get; private init; }

    /// <summary>Number of tables.</summary>
    public int TableCount { get; private init; }

    /// <summary>Number of task list items ("- [ ]" and "- [x]").</summary>
    public int TaskCount { get; private init; }

    /// <summary>Number of task list items that are ticked ("- [x]").</summary>
    public int CompletedTaskCount { get; private init; }

    /// <summary>Line ending style: "Windows (CRLF)", "Unix (LF)", "Mixed" or "None" for single-line text.</summary>
    public string LineEndingStyle { get; private init; } = "None";

    /// <summary>Estimated time to read the document at <see cref="WordsReadPerMinute"/> words per minute.</summary>
    public TimeSpan EstimatedReadingTime => TimeSpan.FromMinutes((double)WordCount / WordsReadPerMinute);

    /// <summary>
    /// Counts words the way a reader would: runs of non-whitespace that contain at least one
    /// letter or digit. Markdown symbols on their own ("#", "-", "|", "```") are not words.
    /// </summary>
    public static int CountWords(string text)
    {
        int wordCount = 0;
        bool isInsideToken = false;
        bool tokenHasLetterOrDigit = false;

        foreach (char currentCharacter in text)
        {
            if (char.IsWhiteSpace(currentCharacter))
            {
                // End of a token: count it if it contained real text.
                if (isInsideToken && tokenHasLetterOrDigit)
                {
                    wordCount++;
                }
                isInsideToken = false;
                tokenHasLetterOrDigit = false;
            }
            else
            {
                isInsideToken = true;
                tokenHasLetterOrDigit |= char.IsLetterOrDigit(currentCharacter);
            }
        }

        // The last token has no whitespace after it.
        if (isInsideToken && tokenHasLetterOrDigit)
        {
            wordCount++;
        }
        return wordCount;
    }

    /// <summary>Analyses the Markdown text and returns its statistics.</summary>
    public static DocumentStatistics Calculate(string markdownText)
    {
        // Plain text counts.
        int characterCount = 0;
        int characterCountWithoutSpaces = 0;
        foreach (Rune textCharacter in markdownText.EnumerateRunes())
        {
            if (textCharacter.Value is '\r' or '\n')
            {
                continue;
            }
            characterCount++;
            if (!Rune.IsWhiteSpace(textCharacter))
            {
                characterCountWithoutSpaces++;
            }
        }

        // Line counts. A trailing line break does not start an extra (empty) line.
        string[] textLines = markdownText.Split('\n');
        int lineCount = markdownText.Length == 0 ? 0 : textLines.Length - (markdownText.EndsWith('\n') ? 1 : 0);
        int blankLineCount = textLines.Take(lineCount).Count(lineText => string.IsNullOrWhiteSpace(lineText));

        // Markdown structure, from the same parser settings the viewer uses.
        MarkdownDocument parsedDocument = Markdig.Markdown.Parse(markdownText, MarkdownFlowDocumentRenderer.SharedPipeline);

        var headingCountsByLevel = new int[6];
        string? firstHeadingText = null;
        string? firstLevelOneHeadingText = null;
        foreach (HeadingBlock headingBlock in parsedDocument.Descendants<HeadingBlock>())
        {
            int levelIndex = Math.Clamp(headingBlock.Level, 1, 6) - 1;
            headingCountsByLevel[levelIndex]++;

            string headingText = GetPlainText(headingBlock.Inline).Trim();
            if (headingText.Length == 0)
            {
                continue;
            }
            firstHeadingText ??= headingText;
            if (headingBlock.Level == 1)
            {
                firstLevelOneHeadingText ??= headingText;
            }
        }

        // Links and images are both LinkInline in Markdig; auto-links (<https://...> and bare URLs) are counted as links too.
        var allLinkInlines = parsedDocument.Descendants<LinkInline>().ToList();
        int imageCount = allLinkInlines.Count(linkInline => linkInline.IsImage);
        int linkCount = allLinkInlines.Count - imageCount + parsedDocument.Descendants<AutolinkInline>().Count();

        var taskItems = parsedDocument.Descendants<TaskList>().ToList();

        return new DocumentStatistics
        {
            Title = firstLevelOneHeadingText ?? firstHeadingText,
            CharacterCount = characterCount,
            CharacterCountWithoutSpaces = characterCountWithoutSpaces,
            WordCount = CountWords(markdownText),
            LineCount = lineCount,
            BlankLineCount = blankLineCount,
            // Table cells are stored as paragraphs by Markdig, but nobody would call them paragraphs.
            ParagraphCount = parsedDocument.Descendants<ParagraphBlock>().Count(paragraphBlock => paragraphBlock.Parent is not TableCell),
            HeadingCountsByLevel = headingCountsByLevel,
            LinkCount = linkCount,
            ImageCount = imageCount,
            CodeBlockCount = parsedDocument.Descendants<CodeBlock>().Count(),
            TableCount = parsedDocument.Descendants<Table>().Count(),
            TaskCount = taskItems.Count,
            CompletedTaskCount = taskItems.Count(taskItem => taskItem.Checked),
            LineEndingStyle = DetectLineEndingStyle(markdownText),
        };
    }

    /// <summary>Collects the visible text of an inline container (e.g. a heading), without Markdown syntax.</summary>
    private static string GetPlainText(ContainerInline? inlineContainer)
    {
        if (inlineContainer == null)
        {
            return string.Empty;
        }

        var textBuilder = new StringBuilder();
        AppendPlainText(inlineContainer, textBuilder);
        return textBuilder.ToString();
    }

    /// <summary>Walks the inline tree in reading order and appends literal text and inline code.</summary>
    private static void AppendPlainText(ContainerInline inlineContainer, StringBuilder textBuilder)
    {
        foreach (Inline childInline in inlineContainer)
        {
            switch (childInline)
            {
                case LiteralInline literalInline:
                    textBuilder.Append(literalInline.Content.ToString());
                    break;
                case CodeInline codeInline:
                    textBuilder.Append(codeInline.Content);
                    break;
                case ContainerInline nestedContainer:
                    // Emphasis, links etc. contain more text.
                    AppendPlainText(nestedContainer, textBuilder);
                    break;
            }
        }
    }

    /// <summary>Works out whether the text uses Windows (CRLF) or Unix (LF) line endings, or both.</summary>
    private static string DetectLineEndingStyle(string text)
    {
        int windowsLineEndingCount = 0;
        int unixLineEndingCount = 0;
        for (int characterIndex = 0; characterIndex < text.Length; characterIndex++)
        {
            if (text[characterIndex] != '\n')
            {
                continue;
            }

            bool isPrecededByCarriageReturn = characterIndex > 0 && text[characterIndex - 1] == '\r';
            if (isPrecededByCarriageReturn)
            {
                windowsLineEndingCount++;
            }
            else
            {
                unixLineEndingCount++;
            }
        }

        return (windowsLineEndingCount, unixLineEndingCount) switch
        {
            (0, 0) => "None",
            (_, 0) => "Windows (CRLF)",
            (0, _) => "Unix (LF)",
            _ => "Mixed",
        };
    }
}
