using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// Draws a horizontal line above or below a bookshelf list item while a file is being dragged,
/// to show where it will land when the mouse button is released.
/// </summary>
public sealed class InsertionLineAdorner : Adorner
{
    /// <summary>Colour and thickness of the insertion line (the usual Windows selection blue).</summary>
    private static readonly Pen InsertionLinePen = CreateInsertionLinePen();

    /// <summary>True to draw the line along the bottom edge of the item, false for the top edge.</summary>
    private readonly bool isBelowItem;

    /// <summary>Creates a line on the given list item, at its top or bottom edge.</summary>
    public InsertionLineAdorner(UIElement adornedListItem, bool isBelowItem)
        : base(adornedListItem)
    {
        this.isBelowItem = isBelowItem;
        // The line is only a visual hint; it must not catch the mouse or the drop.
        IsHitTestVisible = false;
    }

    /// <summary>Draws the line, with a small mark at the left end so it is easy to spot.</summary>
    protected override void OnRender(DrawingContext drawingContext)
    {
        Size itemSize = AdornedElement.RenderSize;
        double lineY = isBelowItem ? itemSize.Height : 0;

        drawingContext.DrawLine(InsertionLinePen, new Point(0, lineY), new Point(itemSize.Width, lineY));
        drawingContext.DrawEllipse(InsertionLinePen.Brush, null, new Point(3, lineY), 3, 3);
    }

    /// <summary>Builds the shared, frozen pen (frozen pens are faster and can be shared safely).</summary>
    private static Pen CreateInsertionLinePen()
    {
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)), 2);
        linePen.Freeze();
        return linePen;
    }
}
