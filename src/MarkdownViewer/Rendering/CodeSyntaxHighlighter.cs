using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ColorCode;
using ColorCode.Parsing;
using ColorCode.Styling;

// ColorCode and WPF both have a "Style" type; in this file it means ColorCode's token style.
using Style = ColorCode.Styling.Style;

namespace MarkdownViewer.Rendering;

/// <summary>
/// Colours the code in fenced code blocks (```csharp, ```sql, ...).
/// ColorCode.Core splits the code into scopes (keyword, string, comment, ...) and this class
/// turns each piece into a coloured WPF <see cref="Run"/>, so the code stays selectable text.
/// </summary>
internal sealed class CodeSyntaxHighlighter : CodeColorizerBase
{
    /// <summary>Brushes already made for a ColorCode colour string, so each colour is converted only once.</summary>
    private static readonly Dictionary<string, Brush?> BrushesByColorText = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The paragraph that receives the coloured text while <see cref="TryAppendHighlightedCode"/> runs.</summary>
    private readonly Paragraph targetParagraph;

    /// <summary>Creates a highlighter that writes into the given paragraph.</summary>
    private CodeSyntaxHighlighter(Paragraph targetParagraph)
        : base(StyleDictionary.DefaultLight, null) // null: ColorCode creates its own default parser
    {
        this.targetParagraph = targetParagraph;
    }

    /// <summary>
    /// Finds the ColorCode language for a fence's info string, e.g. "csharp", "cs", "C#", "sql", "json", "py".
    /// Returns null for languages ColorCode does not know (and for blocks without a language).
    /// </summary>
    public static ILanguage? FindLanguage(string? fenceInfo)
    {
        if (string.IsNullOrWhiteSpace(fenceInfo))
        {
            return null;
        }

        // The info string can carry extra words after the language ("csharp title=x"); only the first counts.
        string languageName = fenceInfo.Trim().Split(' ', '\t')[0];
        try
        {
            return Languages.FindById(languageName);
        }
        catch (Exception)
        {
            // Never let a highlighting problem stop the document from rendering.
            return null;
        }
    }

    /// <summary>
    /// Adds the code to the paragraph as coloured runs, with explicit line breaks between lines.
    /// Returns false (and adds nothing) if ColorCode fails, so the caller can fall back to plain text.
    /// </summary>
    public static bool TryAppendHighlightedCode(Paragraph targetParagraph, string codeText, ILanguage language)
    {
        // Colour into a scratch paragraph first, so a failure halfway leaves the real paragraph untouched.
        var scratchParagraph = new Paragraph();
        try
        {
            var highlighter = new CodeSyntaxHighlighter(scratchParagraph);
            highlighter.languageParser.Parse(codeText, language, highlighter.Write);
        }
        catch (Exception)
        {
            return false;
        }

        // Move the finished runs over to the real paragraph.
        foreach (var finishedInline in scratchParagraph.Inlines.ToList())
        {
            scratchParagraph.Inlines.Remove(finishedInline);
            targetParagraph.Inlines.Add(finishedInline);
        }
        return true;
    }

    /// <summary>
    /// Called by ColorCode for each piece of the code. <paramref name="scopes"/> says which parts of the
    /// piece are keywords, strings etc.; their indexes are relative to the start of the piece.
    /// </summary>
    protected override void Write(string parsedSourceCode, IList<Scope> scopes)
    {
        AppendScopedText(parsedSourceCode, 0, parsedSourceCode.Length, scopes, inheritedStyle: null);
    }

    /// <summary>
    /// Adds the text between <paramref name="startIndex"/> and <paramref name="endIndex"/>. Parts covered by a
    /// scope get that scope's style (nested scopes win over their parent); the rest gets <paramref name="inheritedStyle"/>.
    /// </summary>
    private void AppendScopedText(string pieceText, int startIndex, int endIndex, IList<Scope> scopes, Style? inheritedStyle)
    {
        int writtenUpToIndex = startIndex;
        foreach (var scope in scopes.OrderBy(scope => scope.Index))
        {
            // Skip scopes that fall outside this range or overlap text already written.
            int scopeStartIndex = Math.Max(scope.Index, writtenUpToIndex);
            int scopeEndIndex = Math.Min(scope.Index + scope.Length, endIndex);
            if (scopeEndIndex <= scopeStartIndex)
            {
                continue;
            }

            // Plain text before this scope.
            AppendStyledText(pieceText.Substring(writtenUpToIndex, scopeStartIndex - writtenUpToIndex), inheritedStyle);

            // The scope itself, letting its own child scopes colour parts of it.
            Style? scopeStyle = Styles.Contains(scope.Name) ? Styles[scope.Name] : inheritedStyle;
            AppendScopedText(pieceText, scopeStartIndex, scopeEndIndex, scope.Children ?? new List<Scope>(), scopeStyle);

            writtenUpToIndex = scopeEndIndex;
        }

        // Plain text after the last scope.
        AppendStyledText(pieceText.Substring(writtenUpToIndex, endIndex - writtenUpToIndex), inheritedStyle);
    }

    /// <summary>Adds text as one or more runs with the given style, turning "\n" into WPF line breaks.</summary>
    private void AppendStyledText(string text, Style? textStyle)
    {
        if (text.Length == 0)
        {
            return;
        }

        string[] textLines = text.Split('\n');
        for (int lineIndex = 0; lineIndex < textLines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                targetParagraph.Inlines.Add(new LineBreak());
            }
            if (textLines[lineIndex].Length == 0)
            {
                continue;
            }

            var codeRun = new Run(textLines[lineIndex]);
            if (textStyle != null)
            {
                // Without a colour the run keeps the paragraph's normal text colour.
                var foregroundBrush = GetBrush(textStyle.Foreground);
                if (foregroundBrush != null)
                {
                    codeRun.Foreground = foregroundBrush;
                }
                if (textStyle.Bold)
                {
                    codeRun.FontWeight = FontWeights.Bold;
                }
                if (textStyle.Italic)
                {
                    codeRun.FontStyle = FontStyles.Italic;
                }
            }
            targetParagraph.Inlines.Add(codeRun);
        }
    }

    /// <summary>Converts a ColorCode colour such as "#FF0000FF" into a frozen brush, or null if there is none.</summary>
    private static Brush? GetBrush(string? colorText)
    {
        if (string.IsNullOrWhiteSpace(colorText))
        {
            return null;
        }

        lock (BrushesByColorText)
        {
            if (BrushesByColorText.TryGetValue(colorText, out var cachedBrush))
            {
                return cachedBrush;
            }

            Brush? createdBrush = null;
            try
            {
                createdBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorText));
                createdBrush.Freeze();
            }
            catch (FormatException)
            {
                // Unknown colour name: leave the text in the normal colour.
            }
            BrushesByColorText[colorText] = createdBrush;
            return createdBrush;
        }
    }
}
