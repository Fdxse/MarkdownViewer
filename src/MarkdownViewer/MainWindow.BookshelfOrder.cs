using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// The order of the bookshelf: files keep the order the user gives them by dragging them up and down
/// the list (or with Alt+Up / Alt+Down). The order is the order of the entries in the settings file.
/// </summary>
public partial class MainWindow
{
    /// <summary>Name of the drag-and-drop data format for a bookshelf entry being moved within the list (the data is its file path).</summary>
    private const string BookshelfEntryDragFormat = "MarkdownViewer.BookshelfEntry";

    /// <summary>Distance from the top or bottom edge of the list (in pixels) where dragging scrolls the list.</summary>
    private const double DragAutoScrollMargin = 24;

    /// <summary>Where the left mouse button went down in the list, to tell a click from the start of a drag.</summary>
    private Point bookshelfMouseDownPoint;

    /// <summary>The entry the mouse button went down on, or null if it was not on an entry.</summary>
    private BookshelfEntry? bookshelfEntryUnderMouseDown;

    /// <summary>The line that shows where a dragged entry will land, or null while nothing is being dragged.</summary>
    private InsertionLineAdorner? bookshelfInsertionLine;

    // ------------------------------------------------------------------
    // Reordering
    // ------------------------------------------------------------------

    /// <summary>
    /// Older versions showed the bookshelf sorted by name while storing it in the order files were added.
    /// The first time this version runs, the stored order is set to the alphabetical one, so the list looks
    /// the same as before; from then on the user's own order is kept.
    /// </summary>
    private void ApplyAlphabeticalOrderOnFirstRun()
    {
        if (appSettings.HasCustomBookshelfOrder)
        {
            return;
        }

        SortBookshelfEntriesByName();
        appSettings.HasCustomBookshelfOrder = true;
        try
        {
            appSettings.Save();
        }
        catch (Exception)
        {
            // Not important: the same sort simply happens again on the next start.
        }
    }

    /// <summary>Sorts the files on the shown shelf by file name (then by folder for files with the same name).</summary>
    private void SortBookshelfEntriesByName()
    {
        var sortedEntries = currentBookshelf.Entries
            .OrderBy(bookshelfEntry => bookshelfEntry.FileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(bookshelfEntry => bookshelfEntry.FolderPath, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        currentBookshelf.Entries.Clear();
        currentBookshelf.Entries.AddRange(sortedEntries);
    }

    /// <summary>
    /// Moves a bookshelf entry next to another one, saves the new order and refreshes the list.
    /// </summary>
    /// <param name="movedEntry">The entry being moved.</param>
    /// <param name="targetEntry">The entry it is dropped on.</param>
    /// <param name="placeBelowTarget">True to put it just below the target, false for just above.</param>
    private void MoveBookshelfEntry(BookshelfEntry movedEntry, BookshelfEntry targetEntry, bool placeBelowTarget)
    {
        List<BookshelfEntry> bookshelfEntries = currentBookshelf.Entries;
        if (ReferenceEquals(movedEntry, targetEntry) || !bookshelfEntries.Contains(movedEntry) || !bookshelfEntries.Contains(targetEntry))
        {
            return;
        }

        // Take the entry out first, then find the target's position in the shortened list.
        bookshelfEntries.Remove(movedEntry);
        int targetIndex = bookshelfEntries.IndexOf(targetEntry);
        int insertIndex = placeBelowTarget ? targetIndex + 1 : targetIndex;
        bookshelfEntries.Insert(insertIndex, movedEntry);

        SaveSettingsOrWarn();
        RefreshBookshelfList();

        // Keep the moved file selected and visible, so it can be moved again with the keyboard.
        BookshelfList.SelectedItem = movedEntry;
        BookshelfList.ScrollIntoView(movedEntry);
    }

    /// <summary>Moves the single selected entry one step up (-1) or down (+1). Used by Alt+Up / Alt+Down.</summary>
    private void MoveSelectedBookshelfEntryByOneStep(int stepDirection)
    {
        if (BookshelfList.SelectedItems.Count != 1 || BookshelfList.SelectedItem is not BookshelfEntry selectedEntry || !currentBookshelf.CanEditEntriesByHand)
        {
            return;
        }

        int currentIndex = currentBookshelf.Entries.IndexOf(selectedEntry);
        int neighbourIndex = currentIndex + stepDirection;
        if (currentIndex < 0 || neighbourIndex < 0 || neighbourIndex >= currentBookshelf.Entries.Count)
        {
            return; // already at the top or bottom
        }

        BookshelfEntry neighbourEntry = currentBookshelf.Entries[neighbourIndex];
        MoveBookshelfEntry(selectedEntry, neighbourEntry, placeBelowTarget: stepDirection > 0);

        // Keep the keyboard focus on the moved item so Alt+Up/Down can be pressed repeatedly.
        if (BookshelfList.ItemContainerGenerator.ContainerFromItem(selectedEntry) is ListBoxItem movedListItem)
        {
            movedListItem.Focus();
        }
    }

    // ------------------------------------------------------------------
    // Drag and drop within the list
    // ------------------------------------------------------------------

    /// <summary>Finds the bookshelf entry under a point of the list (or under an element inside it).</summary>
    private BookshelfEntry? FindBookshelfEntryAt(DependencyObject? elementUnderMouse)
    {
        if (elementUnderMouse == null)
        {
            return null;
        }

        return ItemsControl.ContainerFromElement(BookshelfList, elementUnderMouse) is ListBoxItem listItem
            ? listItem.DataContext as BookshelfEntry
            : null;
    }

    /// <summary>Remembers where the mouse went down, in case the user starts dragging a file.</summary>
    private void BookshelfList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        bookshelfMouseDownPoint = mouseDetails.GetPosition(BookshelfList);
        bookshelfEntryUnderMouseDown = FindBookshelfEntryAt(mouseDetails.OriginalSource as DependencyObject);
    }

    /// <summary>Starts dragging an entry once the mouse has moved a few pixels with the button held down.</summary>
    private void BookshelfList_PreviewMouseMove(object sender, MouseEventArgs mouseDetails)
    {
        // A folder shelf is always sorted by name, so its files cannot be dragged.
        if (mouseDetails.LeftButton != MouseButtonState.Pressed || bookshelfEntryUnderMouseDown == null || !currentBookshelf.CanEditEntriesByHand)
        {
            return;
        }

        // Small movements while clicking are not a drag (Windows decides how small).
        Vector mouseMovement = mouseDetails.GetPosition(BookshelfList) - bookshelfMouseDownPoint;
        if (Math.Abs(mouseMovement.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(mouseMovement.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        BookshelfEntry draggedEntry = bookshelfEntryUnderMouseDown;
        bookshelfEntryUnderMouseDown = null;

        // DoDragDrop runs until the mouse button is released; the drop is handled in BookshelfList_Drop.
        // The file path (a plain string) is passed rather than the entry object, so it survives any copying of the drag data.
        var dragData = new DataObject(BookshelfEntryDragFormat, draggedEntry.FilePath);
        DragDrop.DoDragDrop(BookshelfList, dragData, DragDropEffects.Move);
        RemoveBookshelfInsertionLine();
    }

    /// <summary>
    /// Works out where a dragged entry would land: on which list item, and above or below it.
    /// Below the last item (in the empty part of the list) means "at the end".
    /// </summary>
    private (ListBoxItem TargetListItem, bool PlaceBelowTarget)? FindBookshelfDropTarget(DragEventArgs dragDetails)
    {
        if (BookshelfList.Items.Count == 0)
        {
            return null;
        }

        Point mousePosition = dragDetails.GetPosition(BookshelfList);
        var elementUnderMouse = BookshelfList.InputHitTest(mousePosition) as DependencyObject;
        if (elementUnderMouse != null && ItemsControl.ContainerFromElement(BookshelfList, elementUnderMouse) is ListBoxItem hoveredListItem)
        {
            // Upper half of an item: insert above it; lower half: insert below it.
            bool isInLowerHalf = dragDetails.GetPosition(hoveredListItem).Y > hoveredListItem.ActualHeight / 2;
            return (hoveredListItem, isInLowerHalf);
        }

        // Not over an item: the empty space after the last one.
        if (BookshelfList.ItemContainerGenerator.ContainerFromIndex(BookshelfList.Items.Count - 1) is ListBoxItem lastListItem)
        {
            return (lastListItem, true);
        }
        return null;
    }

    /// <summary>While an entry is dragged over the list: shows the insertion line and scrolls near the edges.</summary>
    private void BookshelfList_DragOver(object sender, DragEventArgs dragDetails)
    {
        if (!dragDetails.Data.GetDataPresent(BookshelfEntryDragFormat))
        {
            return; // files from Explorer are handled by the window
        }

        dragDetails.Effects = DragDropEffects.Move;
        dragDetails.Handled = true;

        ScrollBookshelfWhileDragging(dragDetails.GetPosition(BookshelfList).Y);

        var dropTarget = FindBookshelfDropTarget(dragDetails);
        if (dropTarget == null)
        {
            RemoveBookshelfInsertionLine();
            return;
        }
        ShowBookshelfInsertionLine(dropTarget.Value.TargetListItem, dropTarget.Value.PlaceBelowTarget);
    }

    /// <summary>Hides the insertion line when the drag leaves the list.</summary>
    private void BookshelfList_DragLeave(object sender, DragEventArgs dragDetails)
    {
        RemoveBookshelfInsertionLine();
    }

    /// <summary>Drops a dragged entry at its new place.</summary>
    private void BookshelfList_Drop(object sender, DragEventArgs dropDetails)
    {
        if (dropDetails.Data.GetData(BookshelfEntryDragFormat) is not string draggedFilePath)
        {
            return;
        }

        BookshelfEntry? draggedEntry = currentBookshelf.Entries.FirstOrDefault(bookshelfEntry => bookshelfEntry.HasPath(draggedFilePath));
        if (draggedEntry == null)
        {
            return;
        }

        dropDetails.Handled = true;
        RemoveBookshelfInsertionLine();

        var dropTarget = FindBookshelfDropTarget(dropDetails);
        if (dropTarget?.TargetListItem.DataContext is BookshelfEntry targetEntry)
        {
            MoveBookshelfEntry(draggedEntry, targetEntry, dropTarget.Value.PlaceBelowTarget);
        }
    }

    /// <summary>Scrolls the list a little when the mouse is near its top or bottom edge, so long lists can be reordered.</summary>
    private void ScrollBookshelfWhileDragging(double mouseY)
    {
        var listScrollViewer = FindDescendant<ScrollViewer>(BookshelfList);
        if (listScrollViewer == null)
        {
            return;
        }

        if (mouseY < DragAutoScrollMargin)
        {
            listScrollViewer.LineUp();
        }
        else if (mouseY > BookshelfList.ActualHeight - DragAutoScrollMargin)
        {
            listScrollViewer.LineDown();
        }
    }

    /// <summary>Draws the insertion line on the given item (replacing any previous line).</summary>
    private void ShowBookshelfInsertionLine(ListBoxItem targetListItem, bool placeBelowTarget)
    {
        RemoveBookshelfInsertionLine();

        var adornerLayer = AdornerLayer.GetAdornerLayer(targetListItem);
        if (adornerLayer == null)
        {
            return;
        }

        bookshelfInsertionLine = new InsertionLineAdorner(targetListItem, placeBelowTarget);
        adornerLayer.Add(bookshelfInsertionLine);
    }

    /// <summary>Removes the insertion line, if one is shown.</summary>
    private void RemoveBookshelfInsertionLine()
    {
        if (bookshelfInsertionLine == null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(bookshelfInsertionLine.AdornedElement)?.Remove(bookshelfInsertionLine);
        bookshelfInsertionLine = null;
    }

    /// <summary>Context menu → Sort by name: puts the bookshelf in alphabetical order (it can be rearranged again afterwards).</summary>
    private void SortBookshelfByNameMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        SortBookshelfEntriesByName();
        SaveSettingsOrWarn();
        RefreshBookshelfList();
    }
}
