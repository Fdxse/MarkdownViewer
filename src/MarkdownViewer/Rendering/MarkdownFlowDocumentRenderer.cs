using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

// Aliases so the WPF and Markdig types with identical names do not collide.
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfList = System.Windows.Documents.List;
using WpfListItem = System.Windows.Documents.ListItem;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using MarkdownTable = Markdig.Extensions.Tables.Table;
using MarkdownTableCell = Markdig.Extensions.Tables.TableCell;
using MarkdownTableRow = Markdig.Extensions.Tables.TableRow;

namespace MarkdownViewer.Rendering;

/// <summary>
/// Converts Markdown text into a native WPF <see cref="FlowDocument"/>.
/// Markdig parses the text into a syntax tree; this class walks that tree and
/// builds the matching WPF paragraphs, lists, tables, hyperlinks and images.
/// </summary>
public sealed class MarkdownFlowDocumentRenderer
{
    /// <summary>Shared Markdig pipeline with the common GitHub-style extensions enabled.</summary>
    internal static readonly MarkdownPipeline SharedPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tables, task lists, footnotes, auto heading ids, etc.
        .UseEmojiAndSmiley()
        .Build();

    /// <summary>Font sizes for heading levels 1 to 6.</summary>
    private static readonly double[] HeadingFontSizes = { 30, 24, 20, 17, 15, 14 };

    /// <summary>Monospace font used for code.</summary>
    private static readonly FontFamily CodeFontFamily = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>Folder of the Markdown file, used to resolve relative image and link paths.</summary>
    private readonly string? documentFolderPath;

    /// <summary>Headings that have an id (e.g. "#getting-started"), so in-document links can jump to them.</summary>
    private readonly Dictionary<string, TextElement> headingElementsById = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Invoked when the user clicks a hyperlink. The argument is the raw link target.</summary>
    private readonly Action<string, MarkdownFlowDocumentRenderer> linkClickHandler;

    /// <summary>
    /// Invoked when the user ticks or unticks a task check box: (source line index, now checked).
    /// When null (e.g. for printing), task check boxes are drawn as plain symbols that cannot be clicked.
    /// </summary>
    private readonly Action<int, bool>? taskToggleHandler;

    /// <summary>Source line (0-based) of the paragraph being rendered, so a task check box knows which line it belongs to.</summary>
    private int currentParagraphLineIndex;

    /// <summary>
    /// Creates a renderer for one document.
    /// </summary>
    /// <param name="documentFolderPath">Folder of the Markdown file, or null for unsaved text.</param>
    /// <param name="linkClickHandler">Called when a link is clicked in the rendered document.</param>
    /// <param name="taskToggleHandler">Called when a task check box is clicked; null makes check boxes read-only symbols.</param>
    public MarkdownFlowDocumentRenderer(
        string? documentFolderPath,
        Action<string, MarkdownFlowDocumentRenderer> linkClickHandler,
        Action<int, bool>? taskToggleHandler = null)
    {
        this.documentFolderPath = documentFolderPath;
        this.linkClickHandler = linkClickHandler;
        this.taskToggleHandler = taskToggleHandler;
    }

    /// <summary>
    /// Brings the heading with the given id into view. Returns false if no such heading exists.
    /// </summary>
    public bool ScrollToHeading(string headingId)
    {
        if (headingElementsById.TryGetValue(headingId, out var headingElement))
        {
            headingElement.BringIntoView();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parses the Markdown text and returns a ready-to-display FlowDocument.
    /// </summary>
    public FlowDocument Render(string markdownText)
    {
        headingElementsById.Clear();

        var parsedDocument = Markdig.Markdown.Parse(markdownText, SharedPipeline);

        var flowDocument = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            LineHeight = 22,
            PagePadding = new Thickness(40, 24, 40, 40),
            Foreground = DocumentTheme.TextBrush,
            Background = DocumentTheme.PageBrush,
            TextAlignment = TextAlignment.Left,
            // A very wide column keeps the page-based viewing modes to a single column.
            ColumnWidth = 100000,
        };

        AddBlocks(parsedDocument, flowDocument.Blocks);
        return flowDocument;
    }

    // ------------------------------------------------------------------
    // Block level rendering
    // ------------------------------------------------------------------

    /// <summary>Renders every child block of a Markdig container into a WPF block collection.</summary>
    private void AddBlocks(ContainerBlock markdownContainer, BlockCollection targetBlocks)
    {
        foreach (var markdownBlock in markdownContainer)
        {
            var renderedBlock = RenderBlock(markdownBlock);
            if (renderedBlock != null)
            {
                targetBlocks.Add(renderedBlock);
            }
        }
    }

    /// <summary>Converts a single Markdig block to its WPF counterpart, or null if it has no visual output.</summary>
    private WpfBlock? RenderBlock(MarkdownBlock markdownBlock)
    {
        switch (markdownBlock)
        {
            case HeadingBlock headingBlock:
                return RenderHeading(headingBlock);

            case ParagraphBlock paragraphBlock:
                return RenderParagraph(paragraphBlock);

            // Must come before CodeBlock: math blocks are a special kind of fenced code block.
            case MathBlock mathBlock:
                return RenderCodeBlock(mathBlock);

            // ```mermaid blocks become diagrams; they fall back to the plain code if drawing fails.
            case FencedCodeBlock fencedCodeBlock when IsMermaidBlock(fencedCodeBlock):
                return RenderMermaidBlock(fencedCodeBlock);

            case CodeBlock codeBlock:
                return RenderCodeBlock(codeBlock);

            case QuoteBlock quoteBlock:
                return RenderQuote(quoteBlock);

            case ListBlock listBlock:
                return RenderList(listBlock);

            case MarkdownTable markdownTable:
                return RenderTable(markdownTable);

            case ThematicBreakBlock:
                return RenderThematicBreak();

            case HtmlBlock htmlBlock:
                return RenderRawHtmlBlock(htmlBlock);

            case FootnoteGroup footnoteGroup:
                return RenderFootnotes(footnoteGroup);

            case LinkReferenceDefinitionGroup:
            case LinkReferenceDefinition:
                // Reference definitions ([id]: url) are not displayed.
                return null;

            case ContainerBlock otherContainer:
                // Unknown container (e.g. custom containers, definition lists): render its children.
                var fallbackSection = new Section();
                AddBlocks(otherContainer, fallbackSection.Blocks);
                return fallbackSection;

            case LeafBlock otherLeaf when otherLeaf.Inline != null:
                // Unknown leaf block with inline content: render it like a paragraph.
                var fallbackParagraph = new Paragraph();
                AddInlines(otherLeaf.Inline, fallbackParagraph.Inlines);
                return fallbackParagraph;

            default:
                return null;
        }
    }

    /// <summary>Renders a heading (# to ######).</summary>
    private Paragraph RenderHeading(HeadingBlock headingBlock)
    {
        // Clamp the level so malformed input never indexes outside the size table.
        int headingLevel = Math.Clamp(headingBlock.Level, 1, HeadingFontSizes.Length);

        var headingParagraph = new Paragraph
        {
            FontSize = HeadingFontSizes[headingLevel - 1],
            FontWeight = FontWeights.SemiBold,
            LineHeight = double.NaN, // let WPF size the line from the larger font
            Margin = new Thickness(0, headingLevel <= 2 ? 24 : 18, 0, 10),
        };

        // Level 1 and 2 headings get an underline like on GitHub.
        if (headingLevel <= 2)
        {
            headingParagraph.BorderBrush = DocumentTheme.BorderBrush;
            headingParagraph.BorderThickness = new Thickness(0, 0, 0, 1);
            headingParagraph.Padding = new Thickness(0, 0, 0, 6);
        }

        if (headingBlock.Inline != null)
        {
            AddInlines(headingBlock.Inline, headingParagraph.Inlines);
        }

        // Remember the auto-generated id so "[link](#heading)" can scroll here.
        var headingId = headingBlock.TryGetAttributes()?.Id;
        if (!string.IsNullOrEmpty(headingId))
        {
            headingElementsById.TryAdd(headingId, headingParagraph);
        }

        return headingParagraph;
    }

    /// <summary>Renders a normal text paragraph.</summary>
    private Paragraph RenderParagraph(ParagraphBlock paragraphBlock)
    {
        var textParagraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        // A task check box ("- [ ] ...") is the first inline of its paragraph; remember the line it sits on.
        currentParagraphLineIndex = paragraphBlock.Line;
        if (paragraphBlock.Inline != null)
        {
            AddInlines(paragraphBlock.Inline, textParagraph.Inlines);
        }
        return textParagraph;
    }

    /// <summary>
    /// Renders an indented or fenced code block (``` ... ```) with a grey background.
    /// Fenced blocks with a known language (```csharp, ```sql, ```json ...) get syntax colouring.
    /// </summary>
    private Paragraph RenderCodeBlock(CodeBlock codeBlock)
    {
        var codeParagraph = new Paragraph
        {
            FontFamily = CodeFontFamily,
            FontSize = 13,
            LineHeight = 19,
            Background = DocumentTheme.CodeBackgroundBrush,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 14),
            BorderBrush = DocumentTheme.BorderBrush,
            BorderThickness = new Thickness(1),
        };

        var codeLines = codeBlock.Lines;

        // Fenced block with a language ColorCode knows: colour it. Math blocks are never coloured.
        if (codeBlock is FencedCodeBlock fencedCodeBlock && codeBlock is not MathBlock)
        {
            var codeLanguage = CodeSyntaxHighlighter.FindLanguage(fencedCodeBlock.Info);
            if (codeLanguage != null)
            {
                string codeText = string.Join("\n", Enumerable.Range(0, codeLines.Count)
                    .Select(lineIndex => codeLines.Lines[lineIndex].Slice.ToString()));
                if (CodeSyntaxHighlighter.TryAppendHighlightedCode(codeParagraph, codeText, codeLanguage))
                {
                    return codeParagraph;
                }
            }
        }

        // Plain code: each source line becomes a Run, separated by explicit line breaks.
        for (int lineIndex = 0; lineIndex < codeLines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                codeParagraph.Inlines.Add(new LineBreak());
            }
            codeParagraph.Inlines.Add(new Run(codeLines.Lines[lineIndex].Slice.ToString()));
        }

        return codeParagraph;
    }

    /// <summary>True for fenced code blocks whose language is "mermaid" (```mermaid).</summary>
    private static bool IsMermaidBlock(FencedCodeBlock fencedCodeBlock)
    {
        return string.Equals(fencedCodeBlock.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders a ```mermaid block. The block starts out as normal code inside a section; once the diagram has been
    /// drawn (see <see cref="MermaidDiagramRenderer"/>), the code is swapped for the picture. If drawing fails,
    /// the code stays and a short note under it explains why.
    /// </summary>
    private Section RenderMermaidBlock(FencedCodeBlock mermaidBlock)
    {
        var codeParagraph = RenderCodeBlock(mermaidBlock);
        var diagramSection = new Section();
        diagramSection.Blocks.Add(codeParagraph);

        string diagramText = string.Join("\n", mermaidBlock.Lines.Lines
            .Take(mermaidBlock.Lines.Count)
            .Select(codeLine => codeLine.Slice.ToString()));
        if (string.IsNullOrWhiteSpace(diagramText))
        {
            return diagramSection; // Nothing to draw.
        }

        // Diagrams drawn before (e.g. on the previous keystroke, or when printing) are shown immediately.
        var finishedOutcome = MermaidDiagramRenderer.Shared.TryGetFinishedRender(diagramText);
        if (finishedOutcome != null)
        {
            ShowMermaidOutcome(diagramSection, codeParagraph, finishedOutcome);
        }
        else
        {
            _ = ShowMermaidDiagramWhenReadyAsync(diagramSection, codeParagraph, diagramText);
        }

        return diagramSection;
    }

    /// <summary>Waits for a diagram to be drawn, then shows it in place of its code.</summary>
    private static async Task ShowMermaidDiagramWhenReadyAsync(Section diagramSection, Paragraph codeParagraph, string diagramText)
    {
        // Started on the UI thread, so the code after await also runs on the UI thread.
        var renderOutcome = await MermaidDiagramRenderer.Shared.RenderAsync(diagramText);
        ShowMermaidOutcome(diagramSection, codeParagraph, renderOutcome);
    }

    /// <summary>Replaces the code with the diagram on success, or adds an explanation under the code on failure.</summary>
    private static void ShowMermaidOutcome(Section diagramSection, Paragraph codeParagraph, MermaidRenderOutcome renderOutcome)
    {
        // The section may have been filled already (or the document re-rendered); only act on the untouched block.
        if (diagramSection.Blocks.Count != 1 || diagramSection.Blocks.FirstBlock != codeParagraph)
        {
            return;
        }

        if (renderOutcome.DiagramImage is { } diagramImage)
        {
            var diagramControl = new Image
            {
                Source = diagramImage.DiagramBitmap,
                // The bitmap is drawn at double size for sharpness; MaxWidth brings it back to its natural size,
                // and DownOnly lets wide diagrams shrink further to fit a narrow window (keeping proportions).
                MaxWidth = diagramImage.DisplayWidth,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            diagramSection.Blocks.Clear();
            diagramSection.Blocks.Add(new BlockUIContainer(diagramControl) { Margin = new Thickness(0, 0, 0, 14) });
            return;
        }

        // Keep the code visible and say why it is not a diagram (syntax error, no WebView2 runtime, ...).
        string firstErrorLine = (renderOutcome.ErrorMessage ?? "Unknown error.").Split('\n')[0].Trim();
        codeParagraph.Margin = new Thickness(0, 0, 0, 4);
        diagramSection.Blocks.Add(new Paragraph(new Run($"Mermaid diagram could not be drawn: {firstErrorLine}"))
        {
            FontSize = 12,
            FontStyle = FontStyles.Italic,
            Foreground = DocumentTheme.MutedTextBrush,
            Margin = new Thickness(0, 0, 0, 14),
        });
    }

    /// <summary>Renders a block quote (&gt; text) with a coloured bar on the left.</summary>
    private Section RenderQuote(QuoteBlock quoteBlock)
    {
        var quoteSection = new Section
        {
            BorderBrush = DocumentTheme.QuoteBarBrush,
            BorderThickness = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(14, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = DocumentTheme.MutedTextBrush,
        };
        AddBlocks(quoteBlock, quoteSection.Blocks);
        return quoteSection;
    }

    /// <summary>Renders a bulleted, numbered or task list.</summary>
    private WpfList RenderList(ListBlock listBlock)
    {
        var renderedList = new WpfList
        {
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(26, 0, 0, 0),
            MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
        };

        // Ordered lists may start at another number than 1 (e.g. "3. item").
        if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out int startNumber) && startNumber > 0)
        {
            renderedList.StartIndex = startNumber;
        }

        // Task lists ("- [ ] todo") show check boxes instead of bullets.
        if (!listBlock.IsOrdered && listBlock.All(IsTaskListItem))
        {
            renderedList.MarkerStyle = TextMarkerStyle.None;
            renderedList.Padding = new Thickness(6, 0, 0, 0);
        }

        foreach (var listChild in listBlock)
        {
            if (listChild is not ListItemBlock listItemBlock)
            {
                continue;
            }

            var renderedItem = new WpfListItem();
            AddBlocks(listItemBlock, renderedItem.Blocks);

            // Paragraphs inside list items should sit closer together than top-level paragraphs.
            foreach (var itemParagraph in renderedItem.Blocks.OfType<Paragraph>())
            {
                itemParagraph.Margin = new Thickness(0, 0, 0, listBlock.IsLoose ? 8 : 2);
            }
            // Nested lists should not add a large gap after themselves.
            foreach (var nestedList in renderedItem.Blocks.OfType<WpfList>())
            {
                nestedList.Margin = new Thickness(0, 0, 0, 2);
            }

            renderedList.ListItems.Add(renderedItem);
        }

        return renderedList;
    }

    /// <summary>True if the list item begins with a task list check box ("[ ]" or "[x]").</summary>
    private static bool IsTaskListItem(MarkdownBlock listChild)
    {
        return listChild is ListItemBlock listItemBlock
            && listItemBlock.FirstOrDefault() is ParagraphBlock firstParagraph
            && firstParagraph.Inline?.FirstChild is TaskList;
    }

    /// <summary>Renders a pipe table (| a | b |) with a header row and cell borders.</summary>
    private WpfTable RenderTable(MarkdownTable markdownTable)
    {
        var renderedTable = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 0, 0, 14),
            BorderBrush = DocumentTheme.BorderBrush,
            BorderThickness = new Thickness(1, 1, 0, 0),
        };

        // One WPF column per Markdown column definition.
        int columnCount = Math.Max(1, markdownTable.ColumnDefinitions.Count);
        for (int columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            renderedTable.Columns.Add(new TableColumn { Width = GridLength.Auto });
        }

        var rowGroup = new TableRowGroup();
        renderedTable.RowGroups.Add(rowGroup);

        int bodyRowNumber = 0;
        foreach (var tableChild in markdownTable)
        {
            if (tableChild is not MarkdownTableRow markdownRow)
            {
                continue;
            }

            var renderedRow = new WpfTableRow();

            if (markdownRow.IsHeader)
            {
                renderedRow.FontWeight = FontWeights.SemiBold;
                renderedRow.Background = DocumentTheme.TableHeaderBrush;
            }
            else
            {
                // Zebra striping makes wide tables easier to read.
                if (bodyRowNumber % 2 == 1)
                {
                    renderedRow.Background = DocumentTheme.TableStripeBrush;
                }
                bodyRowNumber++;
            }

            foreach (var rowChild in markdownRow)
            {
                if (rowChild is not MarkdownTableCell markdownCell)
                {
                    continue;
                }
                renderedRow.Cells.Add(RenderTableCell(markdownTable, markdownCell));
            }

            rowGroup.Rows.Add(renderedRow);
        }

        return renderedTable;
    }

    /// <summary>Renders one table cell, honouring the column's alignment (:--, :-:, --:).</summary>
    private WpfTableCell RenderTableCell(MarkdownTable markdownTable, MarkdownTableCell markdownCell)
    {
        var renderedCell = new WpfTableCell
        {
            BorderBrush = DocumentTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(10, 5, 10, 5),
            ColumnSpan = Math.Max(1, markdownCell.ColumnSpan),
            RowSpan = Math.Max(1, markdownCell.RowSpan),
        };

        AddBlocks(markdownCell, renderedCell.Blocks);

        // Cell paragraphs should not carry the normal paragraph spacing.
        foreach (var cellParagraph in renderedCell.Blocks.OfType<Paragraph>())
        {
            cellParagraph.Margin = new Thickness(0);
        }

        // Apply the column alignment if the table header specified one.
        int columnIndex = markdownCell.ColumnIndex;
        if (columnIndex >= 0 && columnIndex < markdownTable.ColumnDefinitions.Count)
        {
            renderedCell.TextAlignment = markdownTable.ColumnDefinitions[columnIndex].Alignment switch
            {
                TableColumnAlign.Center => TextAlignment.Center,
                TableColumnAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };
        }

        return renderedCell;
    }

    /// <summary>Renders a horizontal rule (---).</summary>
    private static Paragraph RenderThematicBreak()
    {
        return new Paragraph
        {
            FontSize = 1,
            LineHeight = 1,
            Margin = new Thickness(0, 12, 0, 20),
            BorderBrush = DocumentTheme.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 2),
        };
    }

    /// <summary>
    /// Raw HTML cannot be rendered natively in WPF, so it is shown as dimmed source text.
    /// </summary>
    private static Paragraph RenderRawHtmlBlock(HtmlBlock htmlBlock)
    {
        var htmlParagraph = new Paragraph
        {
            FontFamily = CodeFontFamily,
            FontSize = 12,
            Foreground = DocumentTheme.MutedTextBrush,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var htmlLines = htmlBlock.Lines;
        for (int lineIndex = 0; lineIndex < htmlLines.Count; lineIndex++)
        {
            if (lineIndex > 0)
            {
                htmlParagraph.Inlines.Add(new LineBreak());
            }
            htmlParagraph.Inlines.Add(new Run(htmlLines.Lines[lineIndex].Slice.ToString()));
        }

        return htmlParagraph;
    }

    /// <summary>Renders the footnote list that Markdig places at the end of the document.</summary>
    private Section RenderFootnotes(FootnoteGroup footnoteGroup)
    {
        var footnoteSection = new Section
        {
            FontSize = 13,
            Foreground = DocumentTheme.MutedTextBrush,
            BorderBrush = DocumentTheme.BorderBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 10, 0, 0),
            Margin = new Thickness(0, 20, 0, 0),
        };

        foreach (var footnoteBlock in footnoteGroup.OfType<Footnote>())
        {
            var footnoteContent = new Section();
            AddBlocks(footnoteBlock, footnoteContent.Blocks);

            // Prefix the first paragraph with the footnote number.
            if (footnoteContent.Blocks.FirstBlock is Paragraph firstParagraph)
            {
                var numberPrefix = new Run($"{footnoteBlock.Order}. ") { FontWeight = FontWeights.SemiBold };
                if (firstParagraph.Inlines.FirstInline != null)
                {
                    firstParagraph.Inlines.InsertBefore(firstParagraph.Inlines.FirstInline, numberPrefix);
                }
                else
                {
                    firstParagraph.Inlines.Add(numberPrefix);
                }
                firstParagraph.Margin = new Thickness(0, 0, 0, 4);
            }

            footnoteSection.Blocks.Add(footnoteContent);
        }

        return footnoteSection;
    }

    // ------------------------------------------------------------------
    // Inline level rendering
    // ------------------------------------------------------------------

    /// <summary>Renders all child inlines of a Markdig inline container into a WPF inline collection.</summary>
    private void AddInlines(ContainerInline markdownContainer, InlineCollection targetInlines)
    {
        foreach (var markdownInline in markdownContainer)
        {
            var renderedInline = RenderInline(markdownInline);
            if (renderedInline != null)
            {
                targetInlines.Add(renderedInline);
            }
        }
    }

    /// <summary>Converts a single Markdig inline (text, emphasis, link...) to a WPF inline.</summary>
    private WpfInline? RenderInline(MarkdownInline markdownInline)
    {
        switch (markdownInline)
        {
            case LiteralInline literalInline:
                return new Run(literalInline.Content.ToString());

            case EmphasisInline emphasisInline:
                return RenderEmphasis(emphasisInline);

            case CodeInline codeInline:
                return new Run(codeInline.Content)
                {
                    FontFamily = CodeFontFamily,
                    FontSize = 13,
                    Background = DocumentTheme.CodeBackgroundBrush,
                };

            case MathInline mathInline:
                // Formulas are shown as their source text in a code style.
                return new Run(mathInline.Content.ToString())
                {
                    FontFamily = CodeFontFamily,
                    FontStyle = FontStyles.Italic,
                };

            case LinkInline { IsImage: true } imageInline:
                return RenderImage(imageInline);

            case LinkInline linkInline:
                return RenderLink(linkInline.Url ?? string.Empty, linkInline.Title, linkInline);

            case AutolinkInline autolinkInline:
                var autolinkTarget = autolinkInline.IsEmail ? "mailto:" + autolinkInline.Url : autolinkInline.Url;
                var autolinkHyperlink = CreateHyperlink(autolinkTarget, linkTitle: null);
                autolinkHyperlink.Inlines.Add(new Run(autolinkInline.Url));
                return autolinkHyperlink;

            case LineBreakInline lineBreakInline:
                // A soft break (plain newline) is a space; a hard break (two spaces / backslash) is a new line.
                return lineBreakInline.IsHard ? new LineBreak() : new Run(" ");

            case HtmlEntityInline htmlEntityInline:
                return new Run(htmlEntityInline.Transcoded.ToString());

            case HtmlInline htmlInline:
                // Only <br> is meaningful without an HTML engine; other tags are dropped.
                return htmlInline.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? new LineBreak() : null;

            case TaskList taskListInline when taskToggleHandler != null:
                return RenderTaskCheckBox(taskListInline.Checked);

            case TaskList taskListInline:
                return new Run(taskListInline.Checked ? "☑ " : "☐ ")
                {
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    Foreground = taskListInline.Checked ? DocumentTheme.LinkBrush : DocumentTheme.MutedTextBrush,
                };

            case FootnoteLink footnoteLink:
                // Back links (in the footnote list itself) are not shown.
                if (footnoteLink.IsBackLink)
                {
                    return null;
                }
                var footnoteMarker = new Span { BaselineAlignment = BaselineAlignment.Superscript, FontSize = 11 };
                footnoteMarker.Inlines.Add(new Run($"[{footnoteLink.Footnote.Order}]"));
                return footnoteMarker;

            case ContainerInline otherContainer:
                // Unknown inline container: keep its content.
                var fallbackSpan = new Span();
                AddInlines(otherContainer, fallbackSpan.Inlines);
                return fallbackSpan;

            default:
                return null;
        }
    }

    /// <summary>
    /// Renders a clickable check box for a task ("- [ ] task"). Clicking it reports the task's source line,
    /// so the main window can change "[ ]" to "[x]" (or back) in the text and save the file.
    /// </summary>
    private InlineUIContainer RenderTaskCheckBox(bool isChecked)
    {
        // Captured now: the field moves on to the next paragraph while rendering continues.
        int taskLineIndex = currentParagraphLineIndex;

        var taskCheckBox = new CheckBox
        {
            IsChecked = isChecked,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Tick or untick this task (the file is saved right away)",
        };
        taskCheckBox.Click += (_, _) => taskToggleHandler?.Invoke(taskLineIndex, taskCheckBox.IsChecked == true);

        return new InlineUIContainer(taskCheckBox) { BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>
    /// Renders emphasis: *italic*, **bold**, ~~strikethrough~~, ==marked==, ^super^ and ~sub~.
    /// </summary>
    private Span RenderEmphasis(EmphasisInline emphasisInline)
    {
        var emphasisSpan = new Span();
        AddInlines(emphasisInline, emphasisSpan.Inlines);

        switch (emphasisInline.DelimiterChar)
        {
            case '*':
            case '_':
                if (emphasisInline.DelimiterCount >= 2)
                {
                    emphasisSpan.FontWeight = FontWeights.Bold;
                }
                else
                {
                    emphasisSpan.FontStyle = FontStyles.Italic;
                }
                break;

            case '~':
                if (emphasisInline.DelimiterCount >= 2)
                {
                    emphasisSpan.TextDecorations = TextDecorations.Strikethrough;
                }
                else
                {
                    emphasisSpan.BaselineAlignment = BaselineAlignment.Subscript;
                    emphasisSpan.FontSize = 11;
                }
                break;

            case '^':
                emphasisSpan.BaselineAlignment = BaselineAlignment.Superscript;
                emphasisSpan.FontSize = 11;
                break;

            case '=':
                emphasisSpan.Background = DocumentTheme.MarkBrush;
                break;

            case '+':
                emphasisSpan.TextDecorations = TextDecorations.Underline;
                break;
        }

        return emphasisSpan;
    }

    /// <summary>Renders a [text](url) link as a clickable hyperlink.</summary>
    private Hyperlink RenderLink(string linkTarget, string? linkTitle, ContainerInline linkContent)
    {
        var renderedHyperlink = CreateHyperlink(linkTarget, linkTitle);
        AddInlines(linkContent, renderedHyperlink.Inlines);

        // A link with no visible text (e.g. "[](url)") still needs something to click on.
        if (renderedHyperlink.Inlines.Count == 0)
        {
            renderedHyperlink.Inlines.Add(new Run(linkTarget));
        }
        return renderedHyperlink;
    }

    /// <summary>Creates a styled hyperlink that reports clicks through the link click handler.</summary>
    private Hyperlink CreateHyperlink(string linkTarget, string? linkTitle)
    {
        var createdHyperlink = new Hyperlink
        {
            Foreground = DocumentTheme.LinkBrush,
            ToolTip = string.IsNullOrWhiteSpace(linkTitle) ? linkTarget : $"{linkTitle}\n{linkTarget}",
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        createdHyperlink.Click += (_, _) => linkClickHandler(linkTarget, this);
        return createdHyperlink;
    }

    /// <summary>
    /// Renders an image (![alt](path)). Local paths are resolved relative to the Markdown file.
    /// If the image cannot be loaded, its alt text is shown instead.
    /// </summary>
    private WpfInline RenderImage(LinkInline imageInline)
    {
        // Collect the alt text (the part inside the square brackets).
        string altText = string.Concat(imageInline.Descendants<LiteralInline>().Select(literal => literal.Content.ToString()));
        string imageSource = imageInline.Url ?? string.Empty;

        var altTextFallback = new TextBlock
        {
            Text = $"[image: {(string.IsNullOrWhiteSpace(altText) ? imageSource : altText)}]",
            Foreground = DocumentTheme.MutedTextBrush,
            FontStyle = FontStyles.Italic,
        };

        var resolvedImageUri = ResolveUri(imageSource);
        if (resolvedImageUri == null)
        {
            return new InlineUIContainer(altTextFallback);
        }

        try
        {
            var bitmapSource = new BitmapImage();
            bitmapSource.BeginInit();
            bitmapSource.UriSource = resolvedImageUri;
            // Load local files fully so the file is not kept locked while viewing.
            bitmapSource.CacheOption = resolvedImageUri.IsFile ? BitmapCacheOption.OnLoad : BitmapCacheOption.Default;
            bitmapSource.EndInit();

            var imageControl = new Image
            {
                Source = bitmapSource,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly, // shrink large images, never enlarge small ones
                MaxWidth = 900,
                ToolTip = string.IsNullOrWhiteSpace(altText) ? null : altText,
            };

            // Host image and fallback text together; swap to the text if loading fails (e.g. web download error).
            var imageHost = new Grid();
            imageHost.Children.Add(imageControl);
            altTextFallback.Visibility = Visibility.Collapsed;
            imageHost.Children.Add(altTextFallback);

            void ShowAltTextInstead()
            {
                imageControl.Visibility = Visibility.Collapsed;
                altTextFallback.Visibility = Visibility.Visible;
            }
            imageControl.ImageFailed += (_, _) => ShowAltTextInstead();
            if (bitmapSource.IsDownloading)
            {
                bitmapSource.DownloadFailed += (_, _) => ShowAltTextInstead();
                bitmapSource.DecodeFailed += (_, _) => ShowAltTextInstead();
            }

            return new InlineUIContainer(imageHost) { BaselineAlignment = BaselineAlignment.Bottom };
        }
        catch (Exception)
        {
            // Missing file, unsupported format (e.g. SVG) and similar problems end up here.
            return new InlineUIContainer(altTextFallback);
        }
    }

    /// <summary>
    /// Turns a link or image target into an absolute URI. Relative paths are resolved against
    /// the folder of the current Markdown file. Returns null if the target cannot be resolved.
    /// </summary>
    public Uri? ResolveUri(string rawTarget)
    {
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return null;
        }

        // Absolute URLs (http, https, file, mailto...) and rooted Windows paths are used as-is.
        if (Uri.TryCreate(rawTarget, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        if (documentFolderPath == null)
        {
            return null;
        }

        // Strip any "#anchor" or "?query" part before combining with the folder.
        string relativePath = rawTarget.Split('#', '?')[0];
        if (relativePath.Length == 0)
        {
            return null;
        }

        string fullPath = Path.GetFullPath(Path.Combine(documentFolderPath, Uri.UnescapeDataString(relativePath)));
        return new Uri(fullPath);
    }
}
