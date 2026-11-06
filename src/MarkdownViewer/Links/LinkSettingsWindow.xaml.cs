using System.Collections.ObjectModel;
using System.Windows;

namespace MarkdownViewer.Links;

/// <summary>
/// Dialog for adding, editing, reordering and removing the saved links. Works on copies, so
/// Cancel leaves the current links untouched; OK makes the edited list available in <see cref="EditedLinks"/>.
/// </summary>
public partial class LinkSettingsWindow : Window
{
    /// <summary>The rows shown in the grid (copies of the links passed in).</summary>
    private readonly ObservableCollection<SavedLink> editableLinks;

    /// <summary>Creates the dialog showing copies of the given links.</summary>
    public LinkSettingsWindow(IEnumerable<SavedLink> currentLinks)
    {
        InitializeComponent();

        editableLinks = new ObservableCollection<SavedLink>(currentLinks.Select(link => link.Clone()));
        LinkGrid.ItemsSource = editableLinks;
    }

    /// <summary>The links as edited by the user, with rows lacking a URL removed. Only meaningful after OK.</summary>
    public List<SavedLink> EditedLinks { get; private set; } = new();

    /// <summary>OK: finishes any edit in progress and closes the dialog.</summary>
    private void OkButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // Commit the cell, then the row, the user may still be typing in; otherwise its text is lost.
        LinkGrid.CommitEdit();
        LinkGrid.CommitEdit();

        // A link without an address cannot be opened, so it is dropped. A missing name falls back to the URL.
        EditedLinks = editableLinks
            .Where(link => !string.IsNullOrWhiteSpace(link.Url))
            .Select(link => new SavedLink
            {
                Name = string.IsNullOrWhiteSpace(link.Name) ? link.Url.Trim() : link.Name.Trim(),
                Url = link.Url.Trim(),
            })
            .ToList();

        DialogResult = true;
    }

    /// <summary>Removes the selected row (the Delete key does the same).</summary>
    private void RemoveButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // The "new row" placeholder is not a SavedLink, so the pattern match skips it.
        if (LinkGrid.SelectedItem is SavedLink selectedLink)
        {
            LinkGrid.CancelEdit();
            editableLinks.Remove(selectedLink);
        }
    }

    /// <summary>Moves the selected link one step up in the menu.</summary>
    private void MoveUpButton_Click(object sender, RoutedEventArgs clickDetails) => MoveSelectedLink(-1);

    /// <summary>Moves the selected link one step down in the menu.</summary>
    private void MoveDownButton_Click(object sender, RoutedEventArgs clickDetails) => MoveSelectedLink(+1);

    /// <summary>Moves the selected link by the given number of rows, if it stays inside the list.</summary>
    private void MoveSelectedLink(int rowOffset)
    {
        if (LinkGrid.SelectedItem is not SavedLink selectedLink)
        {
            return;
        }

        // Finish any typing first; moving a row that is being edited is not allowed by the grid.
        LinkGrid.CommitEdit();
        LinkGrid.CommitEdit();

        int currentIndex = editableLinks.IndexOf(selectedLink);
        int targetIndex = currentIndex + rowOffset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= editableLinks.Count)
        {
            return;
        }

        editableLinks.Move(currentIndex, targetIndex);

        // Keep the moved link selected so the button can be clicked repeatedly.
        LinkGrid.SelectedItem = selectedLink;
        LinkGrid.ScrollIntoView(selectedLink);
    }
}
