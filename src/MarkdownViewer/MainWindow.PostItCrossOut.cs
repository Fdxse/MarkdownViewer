using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// Crossing out post-it notes: right-click a note → Cross out puts a big red X over it, and choosing it again
/// takes the X away. Which notes are crossed out is saved per board in
/// <see cref="NamedBookshelf.CrossedOutPostItNotes"/>, by the file's display name, like the note's colour.
/// The file itself is not changed.
/// </summary>
public partial class MainWindow
{
    /// <summary>The colour of the X: a clear "pen" red that stands out on every paper colour.</summary>
    private static readonly Brush PostItCrossOutBrush = CreateFrozenPostItBrush("#D32F2F");

    /// <summary>How thick the two strokes of the X are, in pixels.</summary>
    private const double PostItCrossOutStrokeThickness = 9;

    /// <summary>
    /// The big red X laid over a note: two diagonal strokes from corner to corner, a little inside the paper.
    /// Hidden until the note is crossed out. Clicks go through it to the note, so the note can still be
    /// opened, moved and right-clicked.
    /// </summary>
    private static Path CreatePostItCrossOutMark()
    {
        // Two lines in a 1 x 1 box; Stretch.Fill scales them to the size of the note (the stroke is not scaled).
        var crossGeometry = Geometry.Parse("M 0,0 L 1,1 M 1,0 L 0,1");
        crossGeometry.Freeze();

        return new Path
        {
            Data = crossGeometry,
            Stretch = Stretch.Fill,
            Stroke = PostItCrossOutBrush,
            StrokeThickness = PostItCrossOutStrokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            // Slightly see-through, so the text under the X can still be read.
            Opacity = 0.8,
            Margin = new Thickness(14),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
    }

    /// <summary>True if the user has crossed out this note on the current board.</summary>
    private bool IsPostItNoteCrossedOut(BookshelfEntry bookshelfEntry)
    {
        return currentBookshelf.CrossedOutPostItNotes?.Contains(bookshelfEntry.DisplayName, StringComparer.OrdinalIgnoreCase) == true;
    }

    /// <summary>Shows or hides the red X of one note.</summary>
    private static void ShowOrHidePostItCrossOutMark(PostItNoteState noteState, bool isCrossedOut)
    {
        noteState.CrossOutMark.Visibility = isCrossedOut ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The "Cross out" item of a note's right-click menu, ticked while the note is crossed out.</summary>
    private MenuItem CreatePostItCrossOutMenuItem(BookshelfEntry bookshelfEntry, Border postItNote)
    {
        var crossOutMenuItem = new MenuItem
        {
            Header = "Cross _out",
            IsCheckable = true,
            IsChecked = IsPostItNoteCrossedOut(bookshelfEntry),
        };
        // WPF flips IsChecked before Click, so IsChecked is already the new state here.
        crossOutMenuItem.Click += (_, _) => SetPostItNoteCrossedOut(bookshelfEntry, postItNote, crossOutMenuItem.IsChecked);
        return crossOutMenuItem;
    }

    /// <summary>
    /// Crosses a note out (or takes the X away), saves it and shows the change at once, without redrawing the board.
    /// </summary>
    private void SetPostItNoteCrossedOut(BookshelfEntry bookshelfEntry, Border postItNote, bool isCrossedOut)
    {
        // Keep the crossed-out notes of the other files on the shelf; names of files that are gone (deleted, renamed) are dropped.
        var namesOnShelf = new HashSet<string>(currentBookshelf.Entries.Select(shelfEntry => shelfEntry.DisplayName), StringComparer.OrdinalIgnoreCase);
        var crossedOutNames = new List<string>();
        foreach (var crossedOutName in currentBookshelf.CrossedOutPostItNotes ?? new List<string>())
        {
            bool isThisNote = string.Equals(crossedOutName, bookshelfEntry.DisplayName, StringComparison.OrdinalIgnoreCase);
            bool alreadyListed = crossedOutNames.Contains(crossedOutName, StringComparer.OrdinalIgnoreCase);
            if (!isThisNote && !alreadyListed && namesOnShelf.Contains(crossedOutName))
            {
                crossedOutNames.Add(crossedOutName);
            }
        }

        if (isCrossedOut)
        {
            crossedOutNames.Add(bookshelfEntry.DisplayName);
        }

        // An empty list is saved as "no entry", which keeps the settings file small.
        currentBookshelf.CrossedOutPostItNotes = crossedOutNames.Count > 0 ? crossedOutNames : null;
        SaveSettingsOrWarn();

        if (postItNote.Tag is PostItNoteState noteState)
        {
            ShowOrHidePostItCrossOutMark(noteState, isCrossedOut);
        }
    }
}
