using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using MarkdownViewer.Sorting;

namespace MarkdownViewer;

/// <summary>
/// Sorting sections: right-click a heading in the viewer → "Sort sections at this level" sorts that heading
/// and its siblings (same level, same parent heading). Each section's text and subsections move with it.
/// The file is saved straight away, like ticking a task; Ctrl+Z (or "Undo last sort") puts it back.
/// </summary>
public partial class MainWindow
{
    /// <summary>The text before and after the last sort, so it can be undone while nothing else has changed.</summary>
    private (string TextBeforeSort, string TextAfterSort)? lastSectionSort;

    /// <summary>
    /// Gives every heading in the rendered document its "Sort sections" right-click menu.
    /// Called after each render; the menu items are built when the menu opens, from the text at that moment.
    /// </summary>
    private void AttachSectionSortMenus(IReadOnlyList<(Paragraph HeadingParagraph, int SourceLineIndex)> renderedHeadings)
    {
        foreach (var (headingParagraph, sourceLineIndex) in renderedHeadings)
        {
            var headingContextMenu = new ContextMenu { Tag = sourceLineIndex };
            // Needs at least one item, or WPF does not open the menu at all; the real items are added on opening.
            headingContextMenu.Items.Add(new MenuItem { Header = "Sort sections at this level" });
            headingContextMenu.Opened += HeadingContextMenu_Opened;
            headingParagraph.ContextMenu = headingContextMenu;
        }
    }

    /// <summary>Fills a heading's right-click menu with the sort flavours for its group of sibling sections.</summary>
    private void HeadingContextMenu_Opened(object sender, RoutedEventArgs openDetails)
    {
        if (sender is not ContextMenu headingContextMenu || headingContextMenu.Tag is not int headingLineIndex)
        {
            return;
        }
        headingContextMenu.Items.Clear();

        // While typing in the editor the preview may be behind the text, so line numbers might not match yet.
        var sectionGroup = previewRefreshTimer.IsEnabled ? null : SiblingSectionGroup.Find(MarkdownEditor.Text, headingLineIndex);

        var sortSubmenu = new MenuItem { Header = "_Sort sections at this level" };
        if (sectionGroup == null)
        {
            sortSubmenu.Header = "Sort sections (wait until the preview has updated)";
            sortSubmenu.IsEnabled = false;
        }
        else if (sectionGroup.Sections.Count < 2)
        {
            sortSubmenu.Header = "Sort sections (this heading has no siblings to sort)";
            sortSubmenu.IsEnabled = false;
        }
        else
        {
            // e.g. "Sort the 5 "###" sections under "Projects""
            string headingMarks = new('#', sectionGroup.HeadingLevel);
            string parentDescription = sectionGroup.ParentHeadingText != null ? $" under \"{sectionGroup.ParentHeadingText}\"" : string.Empty;
            sortSubmenu.Header = $"_Sort the {sectionGroup.Sections.Count} \"{headingMarks}\" sections{parentDescription}";

            foreach (SectionSortFlavour sortFlavour in Enum.GetValues<SectionSortFlavour>())
            {
                var flavourMenuItem = new MenuItem { Header = SectionSortFlavours.GetDisplayName(sortFlavour) };
                flavourMenuItem.Click += (_, _) =>
                    ApplySectionOrder(sectionGroup, SectionSortFlavours.Sort(sectionGroup.Sections, sortFlavour));
                sortSubmenu.Items.Add(flavourMenuItem);
            }

            sortSubmenu.Items.Add(new Separator());
            var reorderMenuItem = new MenuItem { Header = "_Reorder by hand..." };
            reorderMenuItem.Click += (_, _) => ShowReorderSectionsWindow(sectionGroup);
            sortSubmenu.Items.Add(reorderMenuItem);
        }
        headingContextMenu.Items.Add(sortSubmenu);

        var undoMenuItem = new MenuItem
        {
            Header = "_Undo last sort",
            InputGestureText = "Ctrl+Z",
            IsEnabled = CanUndoSectionSort(),
        };
        undoMenuItem.Click += (_, _) => UndoSectionSort();
        headingContextMenu.Items.Add(undoMenuItem);
    }

    /// <summary>Opens the drag-and-drop window and applies the order the user chose.</summary>
    private void ShowReorderSectionsWindow(SiblingSectionGroup sectionGroup)
    {
        var reorderWindow = new ReorderSectionsWindow(sectionGroup) { Owner = this };
        if (reorderWindow.ShowDialog() == true)
        {
            ApplySectionOrder(sectionGroup, reorderWindow.OrderedSections);
        }
    }

    /// <summary>
    /// Writes the sections in their new order into the document, shows it and saves the file.
    /// Nothing happens if the text changed since the menu was opened, or the order is unchanged.
    /// </summary>
    private void ApplySectionOrder(SiblingSectionGroup sectionGroup, IReadOnlyList<MarkdownSection> orderedSections)
    {
        if (!string.Equals(MarkdownEditor.Text, sectionGroup.DocumentText, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "The document changed while sorting, so nothing was changed. Please try again.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string sortedText = sectionGroup.BuildDocument(orderedSections);
        if (string.Equals(sortedText, sectionGroup.DocumentText, StringComparison.Ordinal))
        {
            DocumentInfoStatusText.Text = "The sections were already in that order";
            return;
        }

        ReplaceWholeEditorText(sortedText);
        lastSectionSort = (sectionGroup.DocumentText, sortedText);
        DocumentInfoStatusText.Text = $"Sorted {orderedSections.Count} sections (Ctrl+Z undoes it)";
    }

    /// <summary>True when the last sort can still be undone: the text is exactly as the sort left it.</summary>
    private bool CanUndoSectionSort() =>
        lastSectionSort != null && string.Equals(MarkdownEditor.Text, lastSectionSort.Value.TextAfterSort, StringComparison.Ordinal);

    /// <summary>Puts the document back as it was before the last sort, and saves it.</summary>
    private void UndoSectionSort()
    {
        if (!CanUndoSectionSort())
        {
            return;
        }
        ReplaceWholeEditorText(lastSectionSort!.Value.TextBeforeSort);
        lastSectionSort = null;
        DocumentInfoStatusText.Text = "Sort undone";
    }

    /// <summary>
    /// Replaces the editor text as one edit (so the editor's own Ctrl+Z also works), redraws the viewer
    /// at the same scroll position and saves the file if it has one.
    /// </summary>
    private void ReplaceWholeEditorText(string newText)
    {
        int previousCaretIndex = MarkdownEditor.CaretIndex;
        MarkdownEditor.SelectAll();
        MarkdownEditor.SelectedText = newText;
        MarkdownEditor.Select(Math.Min(previousCaretIndex, newText.Length), 0);

        // Show the result now instead of waiting for the typing timer.
        previewRefreshTimer.Stop();
        RenderPreview();

        if (currentFilePath != null)
        {
            SaveToFile(currentFilePath);
        }
    }

    /// <summary>
    /// Ctrl+Z outside the editor undoes the last sort. Inside the editor the editor's own undo is used,
    /// which also restores the text because the sort was made as a single edit.
    /// </summary>
    private void SectionSort_PreviewKeyDown(object sender, KeyEventArgs keyDetails)
    {
        if (keyDetails.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
            && !MarkdownEditor.IsKeyboardFocusWithin && CanUndoSectionSort())
        {
            UndoSectionSort();
            keyDetails.Handled = true;
        }
    }
}
