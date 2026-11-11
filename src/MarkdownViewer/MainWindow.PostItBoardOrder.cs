using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// Rearranging the notes on a post-it board: drag a note and drop it where it should go. While it is
/// dragged, the other notes make room for it, so the board shows the new order before the note is let go.
/// The order is saved per board in the settings file (<see cref="NamedBookshelf.PostItNoteOrder"/>) and
/// is also the order of the board's files in the bookshelf list. New files go after the arranged notes.
/// </summary>
public partial class MainWindow
{
    /// <summary>Name of the drag-and-drop data format for a post-it note being moved on its board (the data is the note itself).</summary>
    private const string PostItNoteDragFormat = "MarkdownViewer.PostItNote";

    /// <summary>How far the board scrolls (in pixels) each time a dragged note is held near its top or bottom edge.</summary>
    private const double PostItBoardAutoScrollStep = 20;

    /// <summary>True while a note is being dragged. The board is not redrawn meanwhile.</summary>
    private bool isPostItNoteBeingDragged;

    /// <summary>The note the left mouse button went down on, or null. Moving the mouse far enough with the button down drags it.</summary>
    private Border? postItNotePressedForDrag;

    /// <summary>Where the mouse button went down on that note (relative to the board), to tell a click from a drag.</summary>
    private Point postItNotePressPoint;

    // ------------------------------------------------------------------
    // Starting a drag
    // ------------------------------------------------------------------

    /// <summary>Remembers the note and the point a single press happened on, in case the mouse now moves (a drag).</summary>
    private void RememberPostItNotePressed(object sender, MouseButtonEventArgs mouseDetails)
    {
        // A double-click opens the note instead, so it never starts a drag.
        if (mouseDetails.ClickCount == 1 && sender is Border pressedNote)
        {
            postItNotePressedForDrag = pressedNote;
            postItNotePressPoint = mouseDetails.GetPosition(PostItBoardView);
        }
        else
        {
            postItNotePressedForDrag = null;
        }
    }

    /// <summary>Starts dragging the pressed note once the mouse has moved a few pixels with the left button held down.</summary>
    private void PostItNote_MouseMove(object sender, MouseEventArgs mouseDetails)
    {
        if (mouseDetails.LeftButton != MouseButtonState.Pressed)
        {
            // The button was let go somewhere else (e.g. outside the window): forget the press.
            postItNotePressedForDrag = null;
            return;
        }

        if (postItNotePressedForDrag == null || !ReferenceEquals(sender, postItNotePressedForDrag) || isPostItNoteBeingDragged)
        {
            return;
        }

        // Windows' own drag threshold, so a slightly shaky click still counts as a click.
        Point currentMousePoint = mouseDetails.GetPosition(PostItBoardView);
        bool movedFarEnough =
            Math.Abs(currentMousePoint.X - postItNotePressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(currentMousePoint.Y - postItNotePressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (movedFarEnough)
        {
            Border draggedNote = postItNotePressedForDrag;
            postItNotePressedForDrag = null;
            DragPostItNote(draggedNote);
        }
    }

    /// <summary>
    /// Drags a note until it is dropped (which saves the new order) or the drag is cancelled with Esc or by
    /// dropping it outside the board (which puts the note back where it was).
    /// </summary>
    private void DragPostItNote(Border draggedNote)
    {
        int originalNoteIndex = PostItNotesPanel.Children.IndexOf(draggedNote);
        if (originalNoteIndex < 0)
        {
            return;
        }

        isPostItNoteBeingDragged = true;
        // The note is shown faded while it is "in the hand", so it is easy to see where it will land.
        draggedNote.Opacity = 0.5;
        DragDropEffects dragResult;
        try
        {
            // Runs until the mouse button is let go or Esc is pressed. The notes move around in PostItBoardView_DragOver meanwhile.
            dragResult = DragDrop.DoDragDrop(draggedNote, new DataObject(PostItNoteDragFormat, draggedNote), DragDropEffects.Move);
        }
        finally
        {
            isPostItNoteBeingDragged = false;
            draggedNote.Opacity = 1;
        }

        if (dragResult == DragDropEffects.Move)
        {
            SavePostItNoteOrderFromBoard();
        }
        else if (PostItNotesPanel.Children.Contains(draggedNote))
        {
            // Cancelled: put the note back in its old place (the others follow by themselves).
            PostItNotesPanel.Children.Remove(draggedNote);
            PostItNotesPanel.Children.Insert(Math.Min(originalNoteIndex, PostItNotesPanel.Children.Count), draggedNote);
        }

        // Catch up on any files that were added, removed or saved while the note was being dragged.
        RefreshPostItBoardIfChanged();
    }

    // ------------------------------------------------------------------
    // While dragging and dropping
    // ------------------------------------------------------------------

    /// <summary>
    /// A note is dragged over the board: the note moves into the place under the mouse at once, and the
    /// board scrolls when the mouse is held near its top or bottom edge.
    /// </summary>
    private void PostItBoardView_DragOver(object sender, DragEventArgs dragDetails)
    {
        // Anything else (e.g. files from Explorer) is left to the window's own drop handling.
        if (dragDetails.Data.GetData(PostItNoteDragFormat) is not Border draggedNote)
        {
            return;
        }

        dragDetails.Effects = DragDropEffects.Move;
        dragDetails.Handled = true;

        ScrollPostItBoardNearEdge(dragDetails.GetPosition(PostItBoardView));

        int targetNoteIndex = FindPostItNoteIndexAt(dragDetails.GetPosition(PostItNotesPanel));
        int draggedNoteIndex = PostItNotesPanel.Children.IndexOf(draggedNote);
        if (targetNoteIndex < 0 || draggedNoteIndex < 0 || targetNoteIndex == draggedNoteIndex)
        {
            return;
        }

        // Take the dragged note out and put it in the target's place; the notes in between shift by one.
        // All notes have the same size, so the dragged note now sits under the mouse and does not jump back.
        PostItNotesPanel.Children.RemoveAt(draggedNoteIndex);
        PostItNotesPanel.Children.Insert(targetNoteIndex, draggedNote);
        // Lay the board out now, so the next DragOver finds the notes in their new places.
        PostItNotesPanel.UpdateLayout();
    }

    /// <summary>A note was let go over the board. It is already in its new place, so this only confirms the move.</summary>
    private void PostItBoardView_Drop(object sender, DragEventArgs dropDetails)
    {
        if (dropDetails.Data.GetDataPresent(PostItNoteDragFormat))
        {
            dropDetails.Effects = DragDropEffects.Move;
            dropDetails.Handled = true;
        }
    }

    /// <summary>
    /// Which place on the board a point (relative to the notes panel) is over: the index of the note there,
    /// the last index if the point is past the last note, or -1 if it is over no note (e.g. the title).
    /// </summary>
    private int FindPostItNoteIndexAt(Point pointOnPanel)
    {
        int noteCount = PostItNotesPanel.Children.Count;
        for (int noteIndex = 0; noteIndex < noteCount; noteIndex++)
        {
            // The layout slot is the note's place including its margin, so the gaps between notes count too,
            // and the note's slight tilt does not matter.
            Rect noteSlot = LayoutInformation.GetLayoutSlot((FrameworkElement)PostItNotesPanel.Children[noteIndex]);
            if (noteSlot.Contains(pointOnPanel))
            {
                return noteIndex;
            }
        }

        if (noteCount == 0)
        {
            return -1;
        }

        // In the empty space after the last note (to its right on the last row, or below it): the end of the board.
        Rect lastNoteSlot = LayoutInformation.GetLayoutSlot((FrameworkElement)PostItNotesPanel.Children[noteCount - 1]);
        bool isPastLastNote = pointOnPanel.Y > lastNoteSlot.Bottom
            || (pointOnPanel.Y >= lastNoteSlot.Top && pointOnPanel.X > lastNoteSlot.Right);
        return isPastLastNote ? noteCount - 1 : -1;
    }

    /// <summary>Scrolls the board up or down a little when a dragged note is near its top or bottom edge.</summary>
    private void ScrollPostItBoardNearEdge(Point pointOnBoard)
    {
        if (pointOnBoard.Y < DragAutoScrollMargin)
        {
            PostItBoardView.ScrollToVerticalOffset(PostItBoardView.VerticalOffset - PostItBoardAutoScrollStep);
        }
        else if (pointOnBoard.Y > PostItBoardView.ActualHeight - DragAutoScrollMargin)
        {
            PostItBoardView.ScrollToVerticalOffset(PostItBoardView.VerticalOffset + PostItBoardAutoScrollStep);
        }
    }

    // ------------------------------------------------------------------
    // Saving and resetting the order
    // ------------------------------------------------------------------

    /// <summary>
    /// Takes the order of the notes on screen as the board's order: puts the shelf's files in that order,
    /// remembers it in the settings file and refreshes the bookshelf list to match.
    /// </summary>
    private void SavePostItNoteOrderFromBoard()
    {
        // The board's files by path. (The folder may have been re-read during the drag, so the notes are matched by path.)
        var currentEntryByPath = new Dictionary<string, BookshelfEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var bookshelfEntry in currentBookshelf.Entries)
        {
            currentEntryByPath.TryAdd(bookshelfEntry.FilePath, bookshelfEntry);
        }

        // The notes in their order on screen, leaving out any whose file was removed meanwhile.
        var entriesInBoardOrder = PostItNotesPanel.Children
            .OfType<FrameworkElement>()
            .Select(noteElement => noteElement.Tag)
            .OfType<PostItNoteState>()
            .Select(noteState => currentEntryByPath.GetValueOrDefault(noteState.BookshelfEntry.FilePath))
            .OfType<BookshelfEntry>()
            .ToList();

        // Files without a note (beyond the most notes drawn, or added during the drag) keep their order after them.
        var entriesOnBoard = new HashSet<BookshelfEntry>(entriesInBoardOrder);
        var entriesNotOnBoard = currentBookshelf.Entries.Where(bookshelfEntry => !entriesOnBoard.Contains(bookshelfEntry));

        currentBookshelf.Entries = entriesInBoardOrder.Concat(entriesNotOnBoard).ToList();
        currentBookshelf.PostItNoteOrder = currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.DisplayName).ToList();
        SaveSettingsOrWarn();
        // The bookshelf list on the left shows the files in the same order as the board.
        ShowBookshelfEntries();
    }

    /// <summary>The "Sort all notes A-Z" item of a note's right-click menu, which forgets the board's own order.</summary>
    private MenuItem CreateSortPostItNotesByNameMenuItem()
    {
        var sortByNameMenuItem = new MenuItem
        {
            Header = "Sort all notes _A-Z",
            ToolTip = "Forget the order you dragged the notes into and sort them by name again",
        };
        sortByNameMenuItem.Click += (_, _) => SortPostItNotesByName();
        return sortByNameMenuItem;
    }

    /// <summary>Asks, then forgets the board's own order and shows the notes sorted by name (as a new board does).</summary>
    private void SortPostItNotesByName()
    {
        if (currentBookshelf.PostItNoteOrder is not { Count: > 0 })
        {
            // Never rearranged: the notes are already in A-Z order.
            return;
        }

        var sortAnswer = MessageBox.Show(this,
            $"Sort the notes on \"{currentBookshelf.Name}\" by name?\n\nThe order you dragged them into is forgotten.",
            "Markdown Viewer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (sortAnswer != MessageBoxResult.OK)
        {
            return;
        }

        currentBookshelf.PostItNoteOrder = null;
        SaveSettingsOrWarn();
        // Re-reading the folder sorts its files by name; then redraw the list and the board in that order.
        RescanCurrentFolderShelf();
        ShowBookshelfEntries();
        postItBoardDrawnFromSignature = string.Empty;
        RefreshPostItBoardIfChanged();
    }
}
