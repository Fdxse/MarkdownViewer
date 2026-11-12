using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// The free-form post-it board: notes lie on a canvas like on a real board. Drag a note to put it anywhere,
/// also on top of other notes (the note being moved always ends up on top of the stack). Tilt a note with the
/// round handle in its top-right corner, with Shift + mouse wheel, or from its right-click menu.
/// Each note's place, tilt and stacking are saved per board in the settings file
/// (<see cref="NamedBookshelf.PostItNotePlacements"/>). Notes that were never placed (e.g. a new file) are laid
/// out in rows in the first free spot. The bookshelf list on the left follows the board's reading order.
/// </summary>
public partial class MainWindow
{
    /// <summary>Space between notes when they are laid out in rows.</summary>
    private const double PostItGridGap = 20;

    /// <summary>How many degrees one step of Shift + mouse wheel (or "Tilt left/right" in the menu) tilts a note.</summary>
    private const double PostItTiltStep = 3;

    /// <summary>A tilt closer to straight than this (in degrees) snaps to exactly straight while tilting with the handle.</summary>
    private const double PostItStraightSnapAngle = 1;

    /// <summary>How far the board scrolls (in pixels) each time a moved note is held near the edge of the board.</summary>
    private const double PostItBoardAutoScrollStep = 20;

    /// <summary>Number of columns used for laying out notes when the board's width is not known yet.</summary>
    private const int FallbackPostItColumnCount = 4;

    /// <summary>What the mouse is doing with a note right now.</summary>
    private enum PostItNoteMouseAction
    {
        /// <summary>Nothing: no button is held on a note.</summary>
        None,

        /// <summary>The left button is down on a note but the mouse has not moved far yet (it may still be a click).</summary>
        Pressed,

        /// <summary>The note is being moved around the board.</summary>
        Moving,

        /// <summary>The note is being tilted with its handle.</summary>
        Tilting,
    }

    /// <summary>True while a note is being moved or tilted. The board is not redrawn meanwhile.</summary>
    private bool isPostItNoteBeingDragged;

    /// <summary>What the mouse is doing with <see cref="postItNoteUnderMouse"/>.</summary>
    private PostItNoteMouseAction postItNoteMouseAction = PostItNoteMouseAction.None;

    /// <summary>The note the left mouse button is held down on, or null.</summary>
    private Border? postItNoteUnderMouse;

    /// <summary>Where the mouse button went down, relative to the board's canvas.</summary>
    private Point postItNotePressPoint;

    /// <summary>Where the note's top-left corner was when the mouse button went down.</summary>
    private Point postItNoteStartPosition;

    /// <summary>When tilting: the direction (in degrees) from the note's centre to the mouse when tilting started.</summary>
    private double postItTiltStartMouseAngle;

    /// <summary>When tilting: the note's tilt when tilting started.</summary>
    private double postItTiltStartNoteAngle;

    /// <summary>Saves the board a moment after the last Shift + mouse wheel step, so a quick spin does not write the settings file every step.</summary>
    private DispatcherTimer? postItPlacementSaveTimer;

    // ------------------------------------------------------------------
    // Placing the notes when the board is drawn
    // ------------------------------------------------------------------

    /// <summary>
    /// Puts freshly drawn notes in their saved places (with their saved tilt and stacking). Notes without a saved
    /// place go into rows, in the first spots that no placed note covers, on top of the stack.
    /// </summary>
    private void PlacePostItNotesOnBoard(IReadOnlyList<Border> drawnNotes)
    {
        // A copy that ignores upper/lower case in file names (the one read from the settings file does not).
        var savedPlacementByName = new Dictionary<string, PostItNotePlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var savedPlacement in currentBookshelf.PostItNotePlacements ?? new Dictionary<string, PostItNotePlacement>())
        {
            savedPlacementByName.TryAdd(savedPlacement.Key, savedPlacement.Value);
        }

        var coveredAreas = new List<Rect>();
        var notesWithoutPlace = new List<Border>();
        int highestStackLevel = 0;
        foreach (var drawnNote in drawnNotes)
        {
            var noteState = (PostItNoteState)drawnNote.Tag;
            if (savedPlacementByName.TryGetValue(noteState.BookshelfEntry.DisplayName, out var notePlacement))
            {
                SetPostItNotePosition(drawnNote, new Point(notePlacement.X, notePlacement.Y));
                noteState.TiltTransform.Angle = notePlacement.Angle;
                Panel.SetZIndex(drawnNote, notePlacement.StackLevel);
                coveredAreas.Add(new Rect(notePlacement.X, notePlacement.Y, PostItNoteWidth, PostItNoteHeight));
                highestStackLevel = Math.Max(highestStackLevel, notePlacement.StackLevel);
            }
            else
            {
                notesWithoutPlace.Add(drawnNote);
            }
        }

        // As many columns as fit the board's width (the canvas has a 16 pixel margin on each side).
        double slotWidth = PostItNoteWidth + PostItGridGap;
        double slotHeight = PostItNoteHeight + PostItGridGap;
        double availableWidth = PostItBoardScroller.ActualWidth - 32;
        int columnCount = availableWidth > 0 ? Math.Max(1, (int)(availableWidth / slotWidth)) : FallbackPostItColumnCount;

        int slotIndex = 0;
        foreach (var noteWithoutPlace in notesWithoutPlace)
        {
            // Walk through the row slots until one is not covered by a placed note.
            Rect freeSlotArea;
            do
            {
                int rowIndex = slotIndex / columnCount;
                int columnIndex = slotIndex % columnCount;
                freeSlotArea = new Rect(columnIndex * slotWidth, rowIndex * slotHeight, PostItNoteWidth, PostItNoteHeight);
                slotIndex++;
            }
            while (coveredAreas.Any(coveredArea => coveredArea.IntersectsWith(freeSlotArea)));

            SetPostItNotePosition(noteWithoutPlace, freeSlotArea.TopLeft);
            // New notes lie on top, so they are never hidden under a stack.
            highestStackLevel++;
            Panel.SetZIndex(noteWithoutPlace, highestStackLevel);
        }

        UpdatePostItCanvasSize();
    }

    /// <summary>Moves a note so its top-left corner is at the given point of the board.</summary>
    private static void SetPostItNotePosition(UIElement postItNote, Point topLeftCorner)
    {
        Canvas.SetLeft(postItNote, topLeftCorner.X);
        Canvas.SetTop(postItNote, topLeftCorner.Y);
    }

    /// <summary>Where a note's top-left corner is on the board.</summary>
    private static Point GetPostItNotePosition(UIElement postItNote)
    {
        double leftEdge = Canvas.GetLeft(postItNote);
        double topEdge = Canvas.GetTop(postItNote);
        return new Point(double.IsNaN(leftEdge) ? 0 : leftEdge, double.IsNaN(topEdge) ? 0 : topEdge);
    }

    /// <summary>All notes on the board.</summary>
    private IEnumerable<Border> GetPostItNotesOnBoard() => PostItNotesCanvas.Children.OfType<Border>();

    /// <summary>
    /// Makes the canvas just big enough for all notes, so the board scrolls to the furthest one.
    /// (A canvas does not grow by itself.)
    /// </summary>
    private void UpdatePostItCanvasSize()
    {
        double furthestRightEdge = 0;
        double furthestBottomEdge = 0;
        foreach (var postItNote in GetPostItNotesOnBoard())
        {
            Point notePosition = GetPostItNotePosition(postItNote);
            furthestRightEdge = Math.Max(furthestRightEdge, notePosition.X + PostItNoteWidth);
            furthestBottomEdge = Math.Max(furthestBottomEdge, notePosition.Y + PostItNoteHeight);
        }

        // A little extra, so the corners of tilted notes and their shadows are not cut off.
        PostItNotesCanvas.Width = furthestRightEdge + PostItGridGap;
        PostItNotesCanvas.Height = furthestBottomEdge + PostItGridGap;
    }

    // ------------------------------------------------------------------
    // Moving a note with the mouse
    // ------------------------------------------------------------------

    /// <summary>The left button went down on a note: it may become a click, or a move if the mouse moves far enough.</summary>
    private void BeginPostItNotePress(Border pressedNote, MouseButtonEventArgs mouseDetails)
    {
        postItNoteUnderMouse = pressedNote;
        postItNoteMouseAction = PostItNoteMouseAction.Pressed;
        postItNotePressPoint = mouseDetails.GetPosition(PostItNotesCanvas);
        postItNoteStartPosition = GetPostItNotePosition(pressedNote);
        // Keep getting the mouse even when it leaves the note (or the window) while the button is down.
        pressedNote.CaptureMouse();
    }

    /// <summary>The round handle in a note's corner was pressed: dragging it tilts the note around its centre.</summary>
    private void PostItTiltHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (sender is not FrameworkElement { Tag: Border tiltedNote } || tiltedNote.Tag is not PostItNoteState noteState)
        {
            return;
        }

        // Handled, so the note itself does not also start a move.
        mouseDetails.Handled = true;
        postItNoteUnderMouse = tiltedNote;
        postItNoteMouseAction = PostItNoteMouseAction.Tilting;
        isPostItNoteBeingDragged = true;
        postItTiltStartMouseAngle = GetAngleFromPostItNoteCentre(tiltedNote, mouseDetails.GetPosition(PostItNotesCanvas));
        postItTiltStartNoteAngle = noteState.TiltTransform.Angle;
        BringPostItNoteToFront(tiltedNote);
        tiltedNote.CaptureMouse();
    }

    /// <summary>Moves or tilts the note under the mouse while the left button is held down.</summary>
    private void PostItNote_MouseMove(object sender, MouseEventArgs mouseDetails)
    {
        if (!ReferenceEquals(sender, postItNoteUnderMouse) || postItNoteUnderMouse.Tag is not PostItNoteState noteState)
        {
            return;
        }

        Point mouseOnCanvas = mouseDetails.GetPosition(PostItNotesCanvas);
        switch (postItNoteMouseAction)
        {
            case PostItNoteMouseAction.Pressed:
                // Windows' own drag threshold, so a slightly shaky click still counts as a click.
                bool movedFarEnough =
                    Math.Abs(mouseOnCanvas.X - postItNotePressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(mouseOnCanvas.Y - postItNotePressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
                if (!movedFarEnough)
                {
                    return;
                }

                // Picked up: the note goes on top of the stack, and the cursor shows it is being moved.
                postItNoteMouseAction = PostItNoteMouseAction.Moving;
                isPostItNoteBeingDragged = true;
                BringPostItNoteToFront(postItNoteUnderMouse);
                Mouse.OverrideCursor = Cursors.SizeAll;
                MovePostItNoteWithMouse(postItNoteUnderMouse, mouseOnCanvas);
                break;

            case PostItNoteMouseAction.Moving:
                MovePostItNoteWithMouse(postItNoteUnderMouse, mouseOnCanvas);
                break;

            case PostItNoteMouseAction.Tilting:
                // The note turns as far as the mouse has turned around its centre since tilting started.
                double mouseAngle = GetAngleFromPostItNoteCentre(postItNoteUnderMouse, mouseOnCanvas);
                double newTiltAngle = NormalizeTiltAngle(postItTiltStartNoteAngle + (mouseAngle - postItTiltStartMouseAngle));
                // Easy to get it exactly straight again.
                noteState.TiltTransform.Angle = Math.Abs(newTiltAngle) < PostItStraightSnapAngle ? 0 : newTiltAngle;
                break;
        }
    }

    /// <summary>Puts the note where the mouse has dragged it (never past the board's top or left edge).</summary>
    private void MovePostItNoteWithMouse(Border movedNote, Point mouseOnCanvas)
    {
        double newLeftEdge = Math.Max(0, postItNoteStartPosition.X + (mouseOnCanvas.X - postItNotePressPoint.X));
        double newTopEdge = Math.Max(0, postItNoteStartPosition.Y + (mouseOnCanvas.Y - postItNotePressPoint.Y));
        SetPostItNotePosition(movedNote, new Point(newLeftEdge, newTopEdge));
        // The board grows when a note is pulled past its right or bottom edge.
        UpdatePostItCanvasSize();
        ScrollPostItBoardNearEdge(Mouse.GetPosition(PostItBoardScroller));
    }

    /// <summary>
    /// Ends a press on a note and says what it was: a click (<see cref="PostItNoteMouseAction.Pressed"/>),
    /// a move or a tilt. A move or tilt is saved.
    /// </summary>
    private PostItNoteMouseAction EndPostItNotePress()
    {
        var finishedAction = postItNoteMouseAction;
        var releasedNote = postItNoteUnderMouse;

        // Reset first: letting go of the mouse capture below raises LostMouseCapture, which must see nothing in progress.
        postItNoteMouseAction = PostItNoteMouseAction.None;
        postItNoteUnderMouse = null;
        isPostItNoteBeingDragged = false;
        Mouse.OverrideCursor = null;
        releasedNote?.ReleaseMouseCapture();

        if (finishedAction is PostItNoteMouseAction.Moving or PostItNoteMouseAction.Tilting)
        {
            SavePostItNotePlacements();
            // Catch up on any files that were added, removed or saved while the note was being moved.
            RefreshPostItBoardIfChanged();
        }
        return finishedAction;
    }

    /// <summary>
    /// The note lost the mouse while it was being moved or tilted (e.g. another window popped up):
    /// keep it where it is now and save that.
    /// </summary>
    private void PostItNote_LostMouseCapture(object sender, MouseEventArgs mouseDetails)
    {
        if (ReferenceEquals(sender, postItNoteUnderMouse))
        {
            EndPostItNotePress();
        }
    }

    /// <summary>Shift + mouse wheel over a note tilts it a few degrees (without Shift the wheel scrolls the board as usual).</summary>
    private void PostItNote_PreviewMouseWheel(object sender, MouseWheelEventArgs wheelDetails)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || sender is not Border { Tag: PostItNoteState noteState })
        {
            return;
        }

        wheelDetails.Handled = true;
        // Wheel away from you tilts to the right (clockwise), towards you to the left.
        double tiltChange = wheelDetails.Delta > 0 ? PostItTiltStep : -PostItTiltStep;
        noteState.TiltTransform.Angle = NormalizeTiltAngle(noteState.TiltTransform.Angle + tiltChange);
        SavePostItNotePlacementsSoon();
    }

    /// <summary>The direction (in degrees) from the centre of a note to a point on the board.</summary>
    private static double GetAngleFromPostItNoteCentre(UIElement postItNote, Point pointOnCanvas)
    {
        Point notePosition = GetPostItNotePosition(postItNote);
        double centreX = notePosition.X + PostItNoteWidth / 2;
        double centreY = notePosition.Y + PostItNoteHeight / 2;
        return Math.Atan2(pointOnCanvas.Y - centreY, pointOnCanvas.X - centreX) * 180 / Math.PI;
    }

    /// <summary>Keeps a tilt between -180 and +180 degrees, so a note turned all the way round does not end up at e.g. 400 degrees.</summary>
    private static double NormalizeTiltAngle(double tiltAngle)
    {
        double normalizedAngle = tiltAngle % 360;
        if (normalizedAngle > 180)
        {
            normalizedAngle -= 360;
        }
        else if (normalizedAngle <= -180)
        {
            normalizedAngle += 360;
        }
        return Math.Round(normalizedAngle, 1);
    }

    /// <summary>Scrolls the board a little when a note being moved is held near its edge, so notes can be moved further than the window shows.</summary>
    private void ScrollPostItBoardNearEdge(Point pointOnScroller)
    {
        if (pointOnScroller.Y < DragAutoScrollMargin)
        {
            PostItBoardScroller.ScrollToVerticalOffset(PostItBoardScroller.VerticalOffset - PostItBoardAutoScrollStep);
        }
        else if (pointOnScroller.Y > PostItBoardScroller.ViewportHeight - DragAutoScrollMargin)
        {
            PostItBoardScroller.ScrollToVerticalOffset(PostItBoardScroller.VerticalOffset + PostItBoardAutoScrollStep);
        }

        if (pointOnScroller.X < DragAutoScrollMargin)
        {
            PostItBoardScroller.ScrollToHorizontalOffset(PostItBoardScroller.HorizontalOffset - PostItBoardAutoScrollStep);
        }
        else if (pointOnScroller.X > PostItBoardScroller.ViewportWidth - DragAutoScrollMargin)
        {
            PostItBoardScroller.ScrollToHorizontalOffset(PostItBoardScroller.HorizontalOffset + PostItBoardAutoScrollStep);
        }
    }

    // ------------------------------------------------------------------
    // Stacking and tilting from the menu
    // ------------------------------------------------------------------

    /// <summary>Puts a note on top of all other notes.</summary>
    private void BringPostItNoteToFront(Border postItNote)
    {
        int highestOtherStackLevel = GetPostItNotesOnBoard()
            .Where(otherNote => !ReferenceEquals(otherNote, postItNote))
            .Select(Panel.GetZIndex)
            .DefaultIfEmpty(0)
            .Max();
        if (Panel.GetZIndex(postItNote) <= highestOtherStackLevel)
        {
            Panel.SetZIndex(postItNote, highestOtherStackLevel + 1);
        }
    }

    /// <summary>Puts a note underneath all other notes.</summary>
    private void SendPostItNoteToBack(Border postItNote)
    {
        int lowestOtherStackLevel = GetPostItNotesOnBoard()
            .Where(otherNote => !ReferenceEquals(otherNote, postItNote))
            .Select(Panel.GetZIndex)
            .DefaultIfEmpty(0)
            .Min();
        if (Panel.GetZIndex(postItNote) >= lowestOtherStackLevel)
        {
            Panel.SetZIndex(postItNote, lowestOtherStackLevel - 1);
        }
    }

    /// <summary>The "Arrange" submenu of a note's right-click menu: tilting, stacking and tidying up the whole board.</summary>
    private MenuItem CreatePostItArrangeMenu(Border postItNote)
    {
        var arrangeMenu = new MenuItem { Header = "_Arrange" };

        // Tilting this note.
        void AddTiltMenuItem(string menuHeader, Func<double, double> computeNewAngle)
        {
            var tiltMenuItem = new MenuItem { Header = menuHeader };
            tiltMenuItem.Click += (_, _) =>
            {
                var noteState = (PostItNoteState)postItNote.Tag;
                noteState.TiltTransform.Angle = NormalizeTiltAngle(computeNewAngle(noteState.TiltTransform.Angle));
                SavePostItNotePlacements();
            };
            arrangeMenu.Items.Add(tiltMenuItem);
        }
        AddTiltMenuItem("Tilt _left", currentAngle => currentAngle - PostItTiltStep);
        AddTiltMenuItem("Tilt _right", currentAngle => currentAngle + PostItTiltStep);
        AddTiltMenuItem("_Straighten", _ => 0);
        arrangeMenu.Items.Add(new Separator());

        // Stacking this note.
        var bringToFrontMenuItem = new MenuItem { Header = "Bring to _front" };
        bringToFrontMenuItem.Click += (_, _) =>
        {
            BringPostItNoteToFront(postItNote);
            SavePostItNotePlacements();
        };
        var sendToBackMenuItem = new MenuItem { Header = "Send to _back" };
        sendToBackMenuItem.Click += (_, _) =>
        {
            SendPostItNoteToBack(postItNote);
            SavePostItNotePlacements();
        };
        arrangeMenu.Items.Add(bringToFrontMenuItem);
        arrangeMenu.Items.Add(sendToBackMenuItem);
        arrangeMenu.Items.Add(new Separator());

        // The whole board.
        var lineUpMenuItem = new MenuItem
        {
            Header = "_Line up all notes in rows",
            ToolTip = "Put all notes back in straight rows, keeping their order (top-left first)",
        };
        lineUpMenuItem.Click += (_, _) => LineUpPostItNotes();
        arrangeMenu.Items.Add(lineUpMenuItem);
        arrangeMenu.Items.Add(CreateSortPostItNotesByNameMenuItem());

        return arrangeMenu;
    }

    // ------------------------------------------------------------------
    // Saving and resetting the arrangement
    // ------------------------------------------------------------------

    /// <summary>
    /// Saves where every note on the board lies (position, tilt and stacking), and puts the shelf's files in the
    /// board's reading order (top row first, left to right) so the bookshelf list matches the board.
    /// Once anything has been moved, all notes keep their places, so new files never push existing notes around.
    /// </summary>
    private void SavePostItNotePlacements()
    {
        postItPlacementSaveTimer?.Stop();
        var notesOnBoard = GetPostItNotesOnBoard().ToList();
        if (notesOnBoard.Count == 0)
        {
            return;
        }

        var newPlacements = new Dictionary<string, PostItNotePlacement>(StringComparer.OrdinalIgnoreCase);

        // Stack levels are renumbered 1, 2, 3... from the bottom up, so they stay small however often notes are moved.
        var notesBottomToTop = notesOnBoard
            .Select((postItNote, drawOrder) => (postItNote, drawOrder))
            .OrderBy(noteAndOrder => Panel.GetZIndex(noteAndOrder.postItNote))
            .ThenBy(noteAndOrder => noteAndOrder.drawOrder)
            .Select(noteAndOrder => noteAndOrder.postItNote);
        int stackLevel = 1;
        foreach (var postItNote in notesBottomToTop)
        {
            var noteState = (PostItNoteState)postItNote.Tag;
            Point notePosition = GetPostItNotePosition(postItNote);
            newPlacements[noteState.BookshelfEntry.DisplayName] = new PostItNotePlacement
            {
                X = Math.Round(notePosition.X, 1),
                Y = Math.Round(notePosition.Y, 1),
                Angle = noteState.TiltTransform.Angle,
                StackLevel = stackLevel,
            };
            Panel.SetZIndex(postItNote, stackLevel);
            stackLevel++;
        }

        // Keep the places of files that are on the shelf but have no note drawn (beyond the most notes shown).
        // Places of files that are gone (deleted, renamed, moved away) are dropped.
        if (currentBookshelf.PostItNotePlacements != null)
        {
            var namesOnShelf = new HashSet<string>(currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.DisplayName), StringComparer.OrdinalIgnoreCase);
            foreach (var oldPlacement in currentBookshelf.PostItNotePlacements)
            {
                if (namesOnShelf.Contains(oldPlacement.Key))
                {
                    newPlacements.TryAdd(oldPlacement.Key, oldPlacement.Value);
                }
            }
        }

        currentBookshelf.PostItNotePlacements = newPlacements;
        currentBookshelf.PostItNoteOrder = GetPostItNotesInReadingOrder()
            .Select(postItNote => ((PostItNoteState)postItNote.Tag).BookshelfEntry.DisplayName)
            .ToList();
        currentBookshelf.ApplyPostItNoteOrder();
        SaveSettingsOrWarn();
        // The bookshelf list on the left shows the files in the board's reading order.
        ShowBookshelfEntries();
    }

    /// <summary>Saves the board shortly, after a series of quick changes (Shift + mouse wheel) has stopped.</summary>
    private void SavePostItNotePlacementsSoon()
    {
        if (postItPlacementSaveTimer == null)
        {
            postItPlacementSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            postItPlacementSaveTimer.Tick += (_, _) => SavePostItNotePlacements();
        }
        postItPlacementSaveTimer.Stop();
        postItPlacementSaveTimer.Start();
    }

    /// <summary>
    /// The notes in reading order: row by row from the top (a row is one note height), left to right in a row.
    /// Used for the bookshelf list and for lining the notes up again.
    /// </summary>
    private List<Border> GetPostItNotesInReadingOrder()
    {
        double rowHeight = PostItNoteHeight + PostItGridGap;
        return GetPostItNotesOnBoard()
            .OrderBy(postItNote => Math.Floor((GetPostItNotePosition(postItNote).Y + PostItNoteHeight / 2) / rowHeight))
            .ThenBy(postItNote => GetPostItNotePosition(postItNote).X)
            .ToList();
    }

    /// <summary>Asks, then puts all notes back in straight rows, keeping their reading order.</summary>
    private void LineUpPostItNotes()
    {
        var lineUpAnswer = MessageBox.Show(this,
            $"Put all notes on \"{currentBookshelf.Name}\" back in straight rows?\n\n" +
            "They keep their order (top-left first), but where you put them, how you tilted them and how they were stacked is forgotten.",
            "Markdown Viewer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (lineUpAnswer != MessageBoxResult.OK)
        {
            return;
        }

        currentBookshelf.PostItNoteOrder = GetPostItNotesInReadingOrder()
            .Select(postItNote => ((PostItNoteState)postItNote.Tag).BookshelfEntry.DisplayName)
            .ToList();
        currentBookshelf.PostItNotePlacements = null;
        currentBookshelf.ApplyPostItNoteOrder();
        SaveSettingsOrWarn();
        ShowBookshelfEntries();
        RedrawPostItBoard();
    }

    /// <summary>The "Sort all notes A-Z" item, which forgets the board's arrangement and lines the notes up by name.</summary>
    private MenuItem CreateSortPostItNotesByNameMenuItem()
    {
        var sortByNameMenuItem = new MenuItem
        {
            Header = "Sort all notes _A-Z",
            ToolTip = "Forget how the notes were arranged and put them in rows sorted by name",
        };
        sortByNameMenuItem.Click += (_, _) => SortPostItNotesByName();
        return sortByNameMenuItem;
    }

    /// <summary>Asks, then forgets the board's arrangement and shows the notes in rows sorted by name (as a new board does).</summary>
    private void SortPostItNotesByName()
    {
        var sortAnswer = MessageBox.Show(this,
            $"Put the notes on \"{currentBookshelf.Name}\" in rows sorted by name?\n\n" +
            "Where you put them, how you tilted them and how they were stacked is forgotten.",
            "Markdown Viewer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (sortAnswer != MessageBoxResult.OK)
        {
            return;
        }

        currentBookshelf.PostItNoteOrder = null;
        currentBookshelf.PostItNotePlacements = null;
        SaveSettingsOrWarn();
        // Re-reading the folder sorts its files by name; then redraw the list and the board in that order.
        RescanCurrentFolderShelf();
        ShowBookshelfEntries();
        RedrawPostItBoard();
    }

    /// <summary>Draws the whole board again from scratch (used after its arrangement was reset).</summary>
    private void RedrawPostItBoard()
    {
        postItBoardDrawnFromSignature = string.Empty;
        RefreshPostItBoardIfChanged();
    }
}
