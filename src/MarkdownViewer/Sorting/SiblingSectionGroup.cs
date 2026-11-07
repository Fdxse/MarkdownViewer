using System.Text;
using Markdig.Syntax;
using MarkdownViewer.Rendering;

namespace MarkdownViewer.Sorting;

/// <summary>
/// One section of a Markdown document: a heading plus everything below it up to the next heading of the
/// same or a higher level. Deeper headings (subsections) belong to the section and move with it.
/// </summary>
/// <param name="HeadingText">The heading's text without the "#" marks, e.g. "Release 2026-11-07".</param>
/// <param name="SectionText">The whole section as it appears in the file (heading line included), without trailing blank lines.</param>
/// <param name="OriginalPosition">0-based place of the section among its siblings before any sorting.</param>
public sealed record MarkdownSection(string HeadingText, string SectionText, int OriginalPosition)
{
    /// <summary>The first date written in the heading, or null if it has none.</summary>
    public DateTime? HeadingDate { get; } = SectionSortFlavours.FindDateInText(HeadingText);

    /// <summary>Number of unticked task boxes ("- [ ]") in the section, its subsections included.</summary>
    public int OpenTaskCount { get; } = SectionSortFlavours.CountOpenTasks(SectionText);
}

/// <summary>
/// The headings of one level that share the same parent heading (for example all "###" headings under one "##"),
/// with their text blocks. Use <see cref="Find"/> to get the group around a heading, reorder
/// <see cref="Sections"/> any way you like, and <see cref="BuildDocument"/> gives the new document text.
/// <para>
/// Text before the first sibling (the parent's own intro) and everything after the last one stays exactly
/// where it was; only the sibling sections swap places.
/// </para>
/// </summary>
public sealed class SiblingSectionGroup
{
    /// <summary>The full document text the group was found in.</summary>
    public string DocumentText { get; }

    /// <summary>Level of the sorted headings, 1 for "#" up to 6 for "######".</summary>
    public int HeadingLevel { get; }

    /// <summary>Text of the parent heading (one level up), or null when the siblings are at the top of the document.</summary>
    public string? ParentHeadingText { get; }

    /// <summary>The sibling sections in the order they have in the document.</summary>
    public IReadOnlyList<MarkdownSection> Sections { get; }

    /// <summary>Where the block of sibling sections starts in <see cref="DocumentText"/> (the first sibling's heading line).</summary>
    private readonly int blockStartIndex;

    /// <summary>Length of the block of sibling sections in <see cref="DocumentText"/>, trailing blank lines included.</summary>
    private readonly int blockLength;

    /// <summary>Blank lines that followed the last section, put back after the sorted block so the gap to the next part stays the same.</summary>
    private readonly string trailingBlankText;

    /// <summary>The line break the document uses ("\r\n" or "\n").</summary>
    private readonly string lineBreak;

    /// <summary>True when the block runs to the very end of the file and the file has no final line break.</summary>
    private readonly bool blockEndsWithoutLineBreak;

    private SiblingSectionGroup(
        string documentText, int headingLevel, string? parentHeadingText, IReadOnlyList<MarkdownSection> sections,
        int blockStartIndex, int blockLength, string trailingBlankText, string lineBreak, bool blockEndsWithoutLineBreak)
    {
        DocumentText = documentText;
        HeadingLevel = headingLevel;
        ParentHeadingText = parentHeadingText;
        Sections = sections;
        this.blockStartIndex = blockStartIndex;
        this.blockLength = blockLength;
        this.trailingBlankText = trailingBlankText;
        this.lineBreak = lineBreak;
        this.blockEndsWithoutLineBreak = blockEndsWithoutLineBreak;
    }

    /// <summary>One line of the document: its text without the line break, and the break itself ("" on the last line).</summary>
    private readonly record struct DocumentLine(int StartIndex, string Text, string LineBreak);

    /// <summary>A heading found by Markdig: its first source line, level and plain text.</summary>
    private readonly record struct HeadingPosition(int LineIndex, int Level, string Text);

    /// <summary>
    /// Finds the heading on the given source line and all its siblings: the headings of the same level that
    /// are under the same parent heading. Returns null if there is no heading on that line.
    /// </summary>
    /// <param name="documentText">The whole Markdown document.</param>
    /// <param name="headingLineIndex">0-based source line of the heading the user picked.</param>
    public static SiblingSectionGroup? Find(string documentText, int headingLineIndex)
    {
        var documentLines = SplitIntoLines(documentText);
        var allHeadings = FindTopLevelHeadings(documentText, documentLines);

        int pickedHeadingIndex = allHeadings.FindIndex(heading => heading.LineIndex == headingLineIndex);
        if (pickedHeadingIndex < 0)
        {
            return null;
        }
        int siblingLevel = allHeadings[pickedHeadingIndex].Level;

        // The parent is the closest heading above with a lower level number ("##" for "###").
        int parentHeadingIndex = -1;
        for (int searchIndex = pickedHeadingIndex - 1; searchIndex >= 0; searchIndex--)
        {
            if (allHeadings[searchIndex].Level < siblingLevel)
            {
                parentHeadingIndex = searchIndex;
                break;
            }
        }

        // The group ends at the next heading with a lower level number (the parent's next sibling), or at the end of the file.
        int groupEndLineIndex = documentLines.Count;
        for (int searchIndex = pickedHeadingIndex + 1; searchIndex < allHeadings.Count; searchIndex++)
        {
            if (allHeadings[searchIndex].Level < siblingLevel)
            {
                groupEndLineIndex = allHeadings[searchIndex].LineIndex;
                break;
            }
        }

        // Every heading of the same level between the parent and the group end is a sibling.
        var siblingHeadings = allHeadings
            .Skip(parentHeadingIndex + 1)
            .Where(heading => heading.Level == siblingLevel && heading.LineIndex < groupEndLineIndex)
            .ToList();

        // Cut the text into one section per sibling: from its heading line to the next sibling (or the group end).
        var sections = new List<MarkdownSection>();
        string trailingBlankText = string.Empty;
        for (int siblingIndex = 0; siblingIndex < siblingHeadings.Count; siblingIndex++)
        {
            int sectionFirstLine = siblingHeadings[siblingIndex].LineIndex;
            int sectionEndLine = siblingIndex + 1 < siblingHeadings.Count ? siblingHeadings[siblingIndex + 1].LineIndex : groupEndLineIndex;

            // Leave trailing blank lines out of the section, so blank lines are not doubled or lost when it moves.
            int lastContentLine = sectionEndLine - 1;
            while (lastContentLine > sectionFirstLine && string.IsNullOrWhiteSpace(documentLines[lastContentLine].Text))
            {
                lastContentLine--;
            }

            var sectionBuilder = new StringBuilder();
            for (int lineIndex = sectionFirstLine; lineIndex <= lastContentLine; lineIndex++)
            {
                sectionBuilder.Append(documentLines[lineIndex].Text);
                if (lineIndex < lastContentLine)
                {
                    sectionBuilder.Append(documentLines[lineIndex].LineBreak);
                }
            }
            sections.Add(new MarkdownSection(siblingHeadings[siblingIndex].Text, sectionBuilder.ToString(), siblingIndex));

            // The blank lines after the last section are kept in place after the sorted block.
            if (siblingIndex == siblingHeadings.Count - 1)
            {
                var trailingBuilder = new StringBuilder();
                for (int lineIndex = lastContentLine + 1; lineIndex < sectionEndLine; lineIndex++)
                {
                    trailingBuilder.Append(documentLines[lineIndex].Text).Append(documentLines[lineIndex].LineBreak);
                }
                trailingBlankText = trailingBuilder.ToString();
            }
        }

        int blockStartIndex = documentLines[siblingHeadings[0].LineIndex].StartIndex;
        int blockEndIndex = groupEndLineIndex < documentLines.Count ? documentLines[groupEndLineIndex].StartIndex : documentText.Length;
        var lastBlockLine = documentLines[groupEndLineIndex - 1];
        bool blockEndsWithoutLineBreak = lastBlockLine.LineBreak.Length == 0;

        string? parentHeadingText = parentHeadingIndex >= 0 ? allHeadings[parentHeadingIndex].Text : null;
        return new SiblingSectionGroup(
            documentText, siblingLevel, parentHeadingText, sections,
            blockStartIndex, blockEndIndex - blockStartIndex, trailingBlankText,
            DetectLineBreak(documentText), blockEndsWithoutLineBreak);
    }

    /// <summary>
    /// Builds the document text with the sibling sections in the given order. The sections are separated
    /// by one blank line; everything outside the group is left exactly as it was.
    /// </summary>
    /// <param name="orderedSections">The sections of this group (all of them, each once) in their new order.</param>
    public string BuildDocument(IEnumerable<MarkdownSection> orderedSections)
    {
        var sectionTexts = orderedSections.Select(section => section.SectionText).ToList();
        if (sectionTexts.Count != Sections.Count)
        {
            throw new ArgumentException("Every section of the group must be given exactly once.", nameof(orderedSections));
        }

        // "section", blank line, "section", ... and the line break that ends the last section.
        string blockText = string.Join(lineBreak + lineBreak, sectionTexts) + lineBreak + trailingBlankText;
        if (blockEndsWithoutLineBreak && trailingBlankText.Length == 0)
        {
            // The file had no line break at its very end: do not add one.
            blockText = blockText[..^lineBreak.Length];
        }

        return string.Concat(DocumentText.AsSpan(0, blockStartIndex), blockText, DocumentText.AsSpan(blockStartIndex + blockLength));
    }

    /// <summary>
    /// Lists the headings that sit directly in the document (not inside quotes or lists), using the same
    /// Markdig settings as the viewer, so headings inside code blocks are never mistaken for real ones.
    /// </summary>
    private static List<HeadingPosition> FindTopLevelHeadings(string documentText, List<DocumentLine> documentLines)
    {
        var parsedDocument = Markdig.Markdown.Parse(documentText, MarkdownFlowDocumentRenderer.SharedPipeline);
        return parsedDocument
            .OfType<HeadingBlock>()
            .Where(headingBlock => headingBlock.Line < documentLines.Count)
            .Select(headingBlock => new HeadingPosition(
                headingBlock.Line,
                headingBlock.Level,
                CleanHeadingLine(documentLines[headingBlock.Line].Text, headingBlock.IsSetext)))
            .ToList();
    }

    /// <summary>Removes the "#" marks (and closing "#"s) from an ATX heading line; a setext heading line is already plain text.</summary>
    private static string CleanHeadingLine(string headingLineText, bool isSetextHeading)
    {
        string trimmedLine = headingLineText.Trim();
        if (isSetextHeading)
        {
            return trimmedLine;
        }
        return trimmedLine.TrimStart('#').TrimEnd('#').Trim();
    }

    /// <summary>Splits the text into lines, remembering each line's own break so nothing is changed by accident.</summary>
    private static List<DocumentLine> SplitIntoLines(string documentText)
    {
        var documentLines = new List<DocumentLine>();
        int lineStartIndex = 0;
        while (lineStartIndex <= documentText.Length)
        {
            int newlineIndex = documentText.IndexOf('\n', lineStartIndex);
            if (newlineIndex < 0)
            {
                // Last line (empty if the text ends with a line break): only add it if it has text,
                // except for an entirely empty document, which is one empty line.
                if (lineStartIndex < documentText.Length || documentLines.Count == 0)
                {
                    documentLines.Add(new DocumentLine(lineStartIndex, documentText[lineStartIndex..], string.Empty));
                }
                break;
            }

            bool hasCarriageReturn = newlineIndex > lineStartIndex && documentText[newlineIndex - 1] == '\r';
            int textEndIndex = hasCarriageReturn ? newlineIndex - 1 : newlineIndex;
            documentLines.Add(new DocumentLine(
                lineStartIndex,
                documentText[lineStartIndex..textEndIndex],
                hasCarriageReturn ? "\r\n" : "\n"));
            lineStartIndex = newlineIndex + 1;
        }
        return documentLines;
    }

    /// <summary>The line break used in the document: "\r\n" if it has any, otherwise "\n".</summary>
    private static string DetectLineBreak(string documentText) =>
        documentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}
