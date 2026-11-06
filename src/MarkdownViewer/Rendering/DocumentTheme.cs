using System.Windows.Media;

namespace MarkdownViewer.Rendering;

/// <summary>
/// Colours used when rendering Markdown. Kept in one place so the look is easy to adjust.
/// The palette loosely follows GitHub's light theme.
/// </summary>
public static class DocumentTheme
{
    /// <summary>Normal body text.</summary>
    public static readonly Brush TextBrush = CreateFrozenBrush("#1F2328");

    /// <summary>Secondary text: block quotes, footnotes, alt text.</summary>
    public static readonly Brush MutedTextBrush = CreateFrozenBrush("#59636E");

    /// <summary>Page background.</summary>
    public static readonly Brush PageBrush = CreateFrozenBrush("#FFFFFF");

    /// <summary>Hyperlink text.</summary>
    public static readonly Brush LinkBrush = CreateFrozenBrush("#0969DA");

    /// <summary>Lines under headings, table borders, horizontal rules.</summary>
    public static readonly Brush BorderBrush = CreateFrozenBrush("#D1D9E0");

    /// <summary>Background of code blocks and inline code.</summary>
    public static readonly Brush CodeBackgroundBrush = CreateFrozenBrush("#F6F8FA");

    /// <summary>Bar on the left of block quotes.</summary>
    public static readonly Brush QuoteBarBrush = CreateFrozenBrush("#D1D9E0");

    /// <summary>Table header row background.</summary>
    public static readonly Brush TableHeaderBrush = CreateFrozenBrush("#F6F8FA");

    /// <summary>Background of every other table body row.</summary>
    public static readonly Brush TableStripeBrush = CreateFrozenBrush("#FAFBFC");

    /// <summary>Highlight for ==marked== text.</summary>
    public static readonly Brush MarkBrush = CreateFrozenBrush("#FFF8C5");

    /// <summary>
    /// Creates a brush from a hex colour and freezes it, which makes it immutable,
    /// faster to draw and safe to share between documents.
    /// </summary>
    private static Brush CreateFrozenBrush(string hexColor)
    {
        var colorBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor));
        colorBrush.Freeze();
        return colorBrush;
    }
}
