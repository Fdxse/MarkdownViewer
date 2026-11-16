namespace MarkdownViewer.Bookshelf;

/// <summary>
/// Where one note lies on a free-form post-it board: its position, its tilt and how high it is in the
/// stack. Saved per board in the settings file (<see cref="NamedBookshelf.PostItNotePlacements"/>).
/// </summary>
public class PostItNotePlacement
{
    /// <summary>Distance of the note's left edge from the left of the board, in device-independent pixels.</summary>
    public double X { get; set; }

    /// <summary>Distance of the note's top edge from the top of the board, in device-independent pixels.</summary>
    public double Y { get; set; }

    /// <summary>Tilt in degrees, clockwise (negative tilts the note to the left).</summary>
    public double Angle { get; set; }

    /// <summary>Place in the stack: a note with a higher number lies on top of notes with a lower one.</summary>
    public int StackLevel { get; set; }
}
