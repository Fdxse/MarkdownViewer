using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer.Sorting;

/// <summary>
/// "Reorder sections" dialog: lists the sibling headings so the user can drag them into place (or use
/// Alt+Up / Alt+Down), with buttons that apply one of the sort flavours as a starting point.
/// Built in code because it is small. Read <see cref="OrderedSections"/> after ShowDialog() returned true.
/// </summary>
public sealed class ReorderSectionsWindow : Window
{
    /// <summary>Drag data format for a row being moved inside the list.</summary>
    private const string SectionRowDragFormat = "MarkdownViewer.SectionRow";

    /// <summary>The rows in their current order; the list box shows this collection.</summary>
    private readonly ObservableCollection<SectionRow> sectionRows;

    /// <summary>The list of headings.</summary>
    private readonly ListBox sectionListBox;

    /// <summary>Where the left mouse button went down on a row, to tell a click from the start of a drag.</summary>
    private Point? dragStartPoint;

    /// <summary>The row the mouse button went down on, which becomes the dragged row.</summary>
    private SectionRow? rowUnderMouseDown;

    /// <summary>The insertion line drawn while dragging, so it can be removed again.</summary>
    private (AdornerLayer Layer, InsertionLineAdorner Line)? shownInsertionLine;

    /// <summary>The sections in the order the user chose.</summary>
    public IReadOnlyList<MarkdownSection> OrderedSections => sectionRows.Select(row => row.Section).ToList();

    /// <summary>Creates the dialog for one group of sibling sections.</summary>
    public ReorderSectionsWindow(SiblingSectionGroup sectionGroup)
    {
        Title = "Reorder sections";
        Width = 520;
        Height = 480;
        MinWidth = 380;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        sectionRows = new ObservableCollection<SectionRow>(sectionGroup.Sections.Select(section => new SectionRow(section)));

        // Explanation at the top: which headings these are.
        string headingMarks = new('#', sectionGroup.HeadingLevel);
        string parentDescription = sectionGroup.ParentHeadingText != null ? $"under \"{sectionGroup.ParentHeadingText}\"" : "at the top of the document";
        var explanationText = new TextBlock
        {
            Text = $"The \"{headingMarks}\" sections {parentDescription}. Drag them into place, or select one and press Alt+Up / Alt+Down. " +
                   "Each section's text and subsections move with it.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        sectionListBox = new ListBox
        {
            ItemsSource = sectionRows,
            AllowDrop = true,
            SelectionMode = SelectionMode.Single,
        };
        sectionListBox.PreviewMouseLeftButtonDown += SectionListBox_PreviewMouseLeftButtonDown;
        sectionListBox.PreviewMouseMove += SectionListBox_PreviewMouseMove;
        sectionListBox.DragOver += SectionListBox_DragOver;
        sectionListBox.DragLeave += (_, _) => RemoveInsertionLine();
        sectionListBox.Drop += SectionListBox_Drop;
        sectionListBox.PreviewKeyDown += SectionListBox_PreviewKeyDown;

        // Right column: quick sorts that rearrange the list (nothing is written until Apply).
        var quickSortPanel = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        quickSortPanel.Children.Add(new TextBlock { Text = "Quick sort:", Margin = new Thickness(0, 0, 0, 4) });
        foreach (SectionSortFlavour sortFlavour in Enum.GetValues<SectionSortFlavour>())
        {
            var flavourButton = new Button
            {
                Content = SectionSortFlavours.GetDisplayName(sortFlavour),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            flavourButton.Click += (_, _) => ApplyQuickSort(sortFlavour);
            quickSortPanel.Children.Add(flavourButton);
        }

        var moveUpButton = new Button { Content = "Move up (Alt+Up)", Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 12, 0, 4) };
        moveUpButton.Click += (_, _) => MoveSelectedRow(-1);
        var moveDownButton = new Button { Content = "Move down (Alt+Down)", Padding = new Thickness(8, 3, 8, 3) };
        moveDownButton.Click += (_, _) => MoveSelectedRow(+1);
        quickSortPanel.Children.Add(moveUpButton);
        quickSortPanel.Children.Add(moveDownButton);

        // Bottom: Apply (Enter) and Cancel (Esc).
        var applyButton = new Button { Content = "Apply", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
        applyButton.Click += (_, _) => DialogResult = true;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var bottomButtonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { applyButton, cancelButton },
        };

        // Layout: explanation / (list | buttons) / Apply+Cancel.
        var layoutGrid = new Grid { Margin = new Thickness(12) };
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumnSpan(explanationText, 2);
        Grid.SetRow(sectionListBox, 1);
        Grid.SetRow(quickSortPanel, 1);
        Grid.SetColumn(quickSortPanel, 1);
        Grid.SetRow(bottomButtonRow, 2);
        Grid.SetColumnSpan(bottomButtonRow, 2);
        layoutGrid.Children.Add(explanationText);
        layoutGrid.Children.Add(sectionListBox);
        layoutGrid.Children.Add(quickSortPanel);
        layoutGrid.Children.Add(bottomButtonRow);
        Content = layoutGrid;

        Loaded += (_, _) =>
        {
            sectionListBox.SelectedIndex = 0;
            sectionListBox.Focus();
        };
    }

    /// <summary>Rearranges the list with one of the sort flavours, keeping the selected row selected.</summary>
    private void ApplyQuickSort(SectionSortFlavour sortFlavour)
    {
        var selectedRow = sectionListBox.SelectedItem as SectionRow;
        var sortedSections = SectionSortFlavours.Sort(sectionRows.Select(row => row.Section), sortFlavour);
        var rowsBySection = sectionRows.ToDictionary(row => row.Section);

        sectionRows.Clear();
        foreach (var section in sortedSections)
        {
            sectionRows.Add(rowsBySection[section]);
        }
        sectionListBox.SelectedItem = selectedRow;
    }

    /// <summary>Moves the selected row one step up (-1) or down (+1).</summary>
    private void MoveSelectedRow(int stepDirection)
    {
        int currentIndex = sectionListBox.SelectedIndex;
        int newIndex = currentIndex + stepDirection;
        if (currentIndex < 0 || newIndex < 0 || newIndex >= sectionRows.Count)
        {
            return;
        }
        sectionRows.Move(currentIndex, newIndex);
        sectionListBox.SelectedIndex = newIndex;
        sectionListBox.ScrollIntoView(sectionListBox.SelectedItem);
        (sectionListBox.ItemContainerGenerator.ContainerFromIndex(newIndex) as ListBoxItem)?.Focus();
    }

    /// <summary>Alt+Up / Alt+Down move the selected row. (Alt makes WPF report the key as Key.System.)</summary>
    private void SectionListBox_PreviewKeyDown(object sender, KeyEventArgs keyDetails)
    {
        Key pressedKey = keyDetails.Key == Key.System ? keyDetails.SystemKey : keyDetails.Key;
        if (Keyboard.Modifiers != ModifierKeys.Alt)
        {
            return;
        }
        if (pressedKey == Key.Up)
        {
            MoveSelectedRow(-1);
            keyDetails.Handled = true;
        }
        else if (pressedKey == Key.Down)
        {
            MoveSelectedRow(+1);
            keyDetails.Handled = true;
        }
    }

    // ------------------------------------------------------------------
    // Drag and drop within the list
    // ------------------------------------------------------------------

    /// <summary>Remembers where a possible drag starts.</summary>
    private void SectionListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        dragStartPoint = mouseDetails.GetPosition(sectionListBox);
        rowUnderMouseDown = FindListItemAt(mouseDetails.OriginalSource as DependencyObject)?.DataContext as SectionRow;
    }

    /// <summary>Starts dragging once the mouse has moved far enough with the button held down.</summary>
    private void SectionListBox_PreviewMouseMove(object sender, MouseEventArgs mouseDetails)
    {
        if (mouseDetails.LeftButton != MouseButtonState.Pressed || dragStartPoint == null || rowUnderMouseDown == null)
        {
            return;
        }

        Vector mouseMovement = mouseDetails.GetPosition(sectionListBox) - dragStartPoint.Value;
        if (Math.Abs(mouseMovement.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(mouseMovement.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var draggedRow = rowUnderMouseDown;
        dragStartPoint = null;
        rowUnderMouseDown = null;
        // DoDragDrop runs until the button is released; the drop itself is handled in SectionListBox_Drop.
        DragDrop.DoDragDrop(sectionListBox, new DataObject(SectionRowDragFormat, draggedRow), DragDropEffects.Move);
        RemoveInsertionLine();
    }

    /// <summary>Shows the insertion line above or below the row under the mouse.</summary>
    private void SectionListBox_DragOver(object sender, DragEventArgs dragDetails)
    {
        if (!dragDetails.Data.GetDataPresent(SectionRowDragFormat))
        {
            dragDetails.Effects = DragDropEffects.None;
            dragDetails.Handled = true;
            return;
        }
        dragDetails.Effects = DragDropEffects.Move;
        dragDetails.Handled = true;

        RemoveInsertionLine();
        var dropTarget = FindDropTarget(dragDetails);
        if (dropTarget != null)
        {
            var adornerLayer = AdornerLayer.GetAdornerLayer(dropTarget.Value.TargetListItem);
            if (adornerLayer != null)
            {
                var insertionLine = new InsertionLineAdorner(dropTarget.Value.TargetListItem, dropTarget.Value.PlaceBelowTarget);
                adornerLayer.Add(insertionLine);
                shownInsertionLine = (adornerLayer, insertionLine);
            }
        }
    }

    /// <summary>Moves the dragged row to where it was dropped.</summary>
    private void SectionListBox_Drop(object sender, DragEventArgs dropDetails)
    {
        RemoveInsertionLine();
        if (dropDetails.Data.GetData(SectionRowDragFormat) is not SectionRow draggedRow)
        {
            return;
        }

        var dropTarget = FindDropTarget(dropDetails);
        int oldIndex = sectionRows.IndexOf(draggedRow);
        int newIndex;
        if (dropTarget == null)
        {
            // Dropped on the empty space below the rows: move to the end.
            newIndex = sectionRows.Count - 1;
        }
        else
        {
            int targetIndex = sectionRows.IndexOf((SectionRow)dropTarget.Value.TargetListItem.DataContext);
            int insertIndex = dropTarget.Value.PlaceBelowTarget ? targetIndex + 1 : targetIndex;
            // Taking the row out first shifts the rows after it up by one.
            newIndex = insertIndex > oldIndex ? insertIndex - 1 : insertIndex;
        }

        if (oldIndex >= 0 && newIndex != oldIndex)
        {
            sectionRows.Move(oldIndex, newIndex);
        }
        sectionListBox.SelectedItem = draggedRow;
    }

    /// <summary>The row under the mouse, and whether the mouse is in its lower half (drop below it).</summary>
    private (ListBoxItem TargetListItem, bool PlaceBelowTarget)? FindDropTarget(DragEventArgs dragDetails)
    {
        var targetListItem = FindListItemAt(dragDetails.OriginalSource as DependencyObject);
        if (targetListItem == null)
        {
            return null;
        }
        bool placeBelowTarget = dragDetails.GetPosition(targetListItem).Y > targetListItem.ActualHeight / 2;
        return (targetListItem, placeBelowTarget);
    }

    /// <summary>The list row that contains the given element (e.g. the text under the mouse), or null.</summary>
    private ListBoxItem? FindListItemAt(DependencyObject? elementUnderMouse) =>
        elementUnderMouse == null ? null : ItemsControl.ContainerFromElement(sectionListBox, elementUnderMouse) as ListBoxItem;

    /// <summary>Removes the insertion line, if one is shown.</summary>
    private void RemoveInsertionLine()
    {
        if (shownInsertionLine != null)
        {
            shownInsertionLine.Value.Layer.Remove(shownInsertionLine.Value.Line);
            shownInsertionLine = null;
        }
    }

    /// <summary>One row of the list: the section plus the text shown for it (heading, date and open tasks).</summary>
    private sealed class SectionRow
    {
        /// <summary>The section this row stands for.</summary>
        public MarkdownSection Section { get; }

        public SectionRow(MarkdownSection section) => Section = section;

        /// <summary>What the list box shows, e.g. "Release 2 (3 open tasks)".</summary>
        public override string ToString()
        {
            var extraDetails = new List<string>();
            if (Section.OpenTaskCount > 0)
            {
                extraDetails.Add(Section.OpenTaskCount == 1 ? "1 open task" : $"{Section.OpenTaskCount} open tasks");
            }
            string headingText = string.IsNullOrWhiteSpace(Section.HeadingText) ? "(empty heading)" : Section.HeadingText;
            return extraDetails.Count > 0 ? $"{headingText}   ({string.Join(", ", extraDetails)})" : headingText;
        }
    }
}
