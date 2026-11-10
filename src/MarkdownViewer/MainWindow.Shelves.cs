using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Todo;

namespace MarkdownViewer;

/// <summary>
/// Several named bookshelves (e.g. "Standard", "Documentation", "Post-its"). A drop-down in the
/// bookshelf panel switches between them; the "⋯" button creates, renames and deletes them.
/// The Standard shelf always exists and is the one shown when the app starts.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tag of the "Rename this bookshelf" item in the shelf menu, used to enable or disable it.</summary>
    private const string RenameShelfMenuTag = "RenameShelf";

    /// <summary>Tag of the "Include subfolders" item in the shelf menu (folder shelves only).</summary>
    private const string IncludeSubfoldersMenuTag = "IncludeSubfolders";

    /// <summary>Tag of the "Open the folder" item in the shelf menu (folder shelves only).</summary>
    private const string OpenShelfFolderMenuTag = "OpenShelfFolder";

    /// <summary>Tag of the context menu items that change a shelf's files by hand (sort, remove), which folder shelves do not allow.</summary>
    private const string EditsShelfFilesMenuTag = "EditsShelfFiles";

    /// <summary>Tag of the "Delete this bookshelf" item in the shelf menu.</summary>
    private const string DeleteShelfMenuTag = "DeleteShelf";

    /// <summary>Tag of the "Add to bookshelf" submenu in the file list's context menu.</summary>
    private const string CopyToShelfMenuTag = "CopyToShelf";

    /// <summary>Tag of the "Move to bookshelf" submenu in the file list's context menu.</summary>
    private const string MoveToShelfMenuTag = "MoveToShelf";

    /// <summary>The shelf whose files are listed. Set in <see cref="InitializeBookshelf"/> before anything else uses it.</summary>
    private NamedBookshelf currentBookshelf = null!;

    /// <summary>Set while the drop-down is filled by code, so that does not count as the user choosing a shelf.</summary>
    private bool isRefreshingShelfChooser;

    // ------------------------------------------------------------------
    // Switching shelves
    // ------------------------------------------------------------------

    /// <summary>Fills the shelf drop-down (Standard first, then the others in creation order) and selects the shown shelf.</summary>
    private void RefreshShelfChooser()
    {
        isRefreshingShelfChooser = true;
        try
        {
            ShelfChooser.ItemsSource = appSettings.Bookshelves.ToList();
            ShelfChooser.SelectedItem = currentBookshelf;
        }
        finally
        {
            isRefreshingShelfChooser = false;
        }
    }

    /// <summary>Shows another shelf: its files in the list, and the properties updated (they say which shelves the open file is on).</summary>
    private void ShowBookshelf(NamedBookshelf shelfToShow)
    {
        currentBookshelf = shelfToShow;
        RefreshShelfChooser();
        RefreshBookshelfList();
        UpdateShelfEditingControls();
        WatchCurrentShelfFolder();
        // A post-it board shows its notes in the document area; any other shelf shows the open document.
        ShowOrHidePostItBoardForCurrentShelf();
        UpdatePropertiesPanel();
    }

    /// <summary>The user picked a shelf in the drop-down.</summary>
    private void ShelfChooser_SelectionChanged(object sender, SelectionChangedEventArgs selectionDetails)
    {
        if (isRefreshingShelfChooser || ShelfChooser.SelectedItem is not NamedBookshelf chosenShelf || ReferenceEquals(chosenShelf, currentBookshelf))
        {
            return;
        }
        ShowBookshelf(chosenShelf);
    }

    // ------------------------------------------------------------------
    // Creating, renaming and deleting shelves
    // ------------------------------------------------------------------

    /// <summary>
    /// Asks for a shelf name. Returns the trimmed name, or null if the user cancelled.
    /// Keeps asking while the name is empty or already used by another shelf.
    /// </summary>
    private string? AskForShelfName(string windowTitle, string promptText, string initialName, NamedBookshelf? shelfBeingRenamed)
    {
        string suggestedName = initialName;
        while (true)
        {
            var namePrompt = new TextPromptWindow(windowTitle, promptText, suggestedName) { Owner = this };
            if (namePrompt.ShowDialog() != true)
            {
                return null;
            }

            string enteredName = namePrompt.AnswerText.Trim();
            suggestedName = enteredName;

            if (enteredName.Length == 0)
            {
                MessageBox.Show(this, "Please type a name for the bookshelf.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }

            bool isNameTaken = appSettings.Bookshelves.Any(existingShelf =>
                !ReferenceEquals(existingShelf, shelfBeingRenamed) && existingShelf.HasName(enteredName));
            if (isNameTaken)
            {
                MessageBox.Show(this, $"There is already a bookshelf called \"{enteredName}\". Please choose another name.",
                    "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }

            return enteredName;
        }
    }

    /// <summary>"⋯" button: opens the shelf menu below the button, with Rename/Delete disabled for the Standard shelf.</summary>
    private void ShelfMenuButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        var shelfMenu = ShelfMenuButton.ContextMenu;
        if (shelfMenu == null)
        {
            return;
        }

        foreach (var shelfMenuItem in shelfMenu.Items.OfType<MenuItem>())
        {
            if (shelfMenuItem.Tag is RenameShelfMenuTag or DeleteShelfMenuTag)
            {
                shelfMenuItem.IsEnabled = !currentBookshelf.IsStandardShelf;
            }
            else if (shelfMenuItem.Tag is IncludeSubfoldersMenuTag)
            {
                shelfMenuItem.IsEnabled = currentBookshelf.IsFolderShelf;
                shelfMenuItem.IsChecked = currentBookshelf.IsFolderShelf && currentBookshelf.IncludeSubfolders;
            }
            else if (shelfMenuItem.Tag is OpenShelfFolderMenuTag)
            {
                shelfMenuItem.IsEnabled = currentBookshelf.IsFolderShelf;
            }
            else if (shelfMenuItem.Tag is ShowAsPostItBoardMenuTag)
            {
                // Any folder shelf can be switched between a plain list and a post-it board.
                shelfMenuItem.IsEnabled = currentBookshelf.IsFolderShelf;
                shelfMenuItem.IsChecked = currentBookshelf.IsFolderShelf && currentBookshelf.IsPostItBoard;
            }
        }

        shelfMenu.PlacementTarget = ShelfMenuButton;
        shelfMenu.Placement = PlacementMode.Bottom;
        shelfMenu.IsOpen = true;
    }

    /// <summary>Shelf menu → New bookshelf: creates an empty shelf and shows it.</summary>
    private void NewShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        string? newShelfName = AskForShelfName("New bookshelf",
            "Name of the new bookshelf, for example \"Documentation\" or \"Post-its\":", string.Empty, shelfBeingRenamed: null);
        if (newShelfName == null)
        {
            return;
        }

        var newShelf = new NamedBookshelf { Name = newShelfName };
        appSettings.Bookshelves.Add(newShelf);
        SaveSettingsOrWarn();
        ShowBookshelf(newShelf);
    }

    /// <summary>Shelf menu → Rename this bookshelf (not available for Standard).</summary>
    private void RenameShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentBookshelf.IsStandardShelf)
        {
            return;
        }

        string? newShelfName = AskForShelfName("Rename bookshelf",
            $"New name for the bookshelf \"{currentBookshelf.Name}\":", currentBookshelf.Name, shelfBeingRenamed: currentBookshelf);
        if (newShelfName == null)
        {
            return;
        }

        currentBookshelf.Name = newShelfName;
        SaveSettingsOrWarn();
        ShowBookshelf(currentBookshelf);
    }

    /// <summary>Shelf menu → Delete this bookshelf (not available for Standard). The files on disk are not touched.</summary>
    private void DeleteShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentBookshelf.IsStandardShelf)
        {
            return;
        }

        int fileCount = currentBookshelf.Entries.Count;
        string fileCountText = fileCount == 1 ? "1 file" : $"{fileCount} files";
        string queryCountText = fileCount == 1 ? "1 query" : $"{fileCount} queries";
        string whatHappensText = currentBookshelf.IsFolderShelf
            ? $"It shows the folder {currentBookshelf.FolderPath}. The folder and its files are not touched."
            : currentBookshelf.IsQueryShelf
                ? $"It holds {queryCountText}, which are deleted with it (the databases are not touched)."
                : $"It holds {fileCountText}. The files themselves are not deleted from disk.";
        var userAnswer = MessageBox.Show(this,
            $"Delete the bookshelf \"{currentBookshelf.Name}\"?\n\n{whatHappensText}",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (userAnswer != MessageBoxResult.Yes)
        {
            return;
        }

        appSettings.Bookshelves.Remove(currentBookshelf);
        SaveSettingsOrWarn();
        ShowBookshelf(appSettings.StandardBookshelf);
    }

    // ------------------------------------------------------------------
    // Putting files on other shelves
    // ------------------------------------------------------------------

    /// <summary>
    /// The file list's context menu is opening: fills the "Add to bookshelf" and "Move to bookshelf"
    /// submenus with every shelf other than the shown one.
    /// </summary>
    private void BookshelfContextMenu_Opened(object sender, RoutedEventArgs openDetails)
    {
        if (sender is not ContextMenu bookshelfContextMenu)
        {
            return;
        }

        // Files can only be put on normal shelves (a folder shelf shows its folder), and queries only on query shelves.
        var otherShelves = appSettings.Bookshelves
            .Where(bookshelf => !ReferenceEquals(bookshelf, currentBookshelf) && !bookshelf.IsFolderShelf
                && bookshelf.IsQueryShelf == currentBookshelf.IsQueryShelf)
            .ToList();
        bool hasSelection = SelectedBookshelfEntries.Count > 0;

        // Sorting and removing make no sense on a folder shelf.
        foreach (var editingMenuItem in bookshelfContextMenu.Items.OfType<MenuItem>().Where(menuItem => Equals(menuItem.Tag, EditsShelfFilesMenuTag)))
        {
            editingMenuItem.IsEnabled = !currentBookshelf.IsFolderShelf;
        }

        // Show in folder and Copy path are for files; Edit query is for queries. Hidden where they do not apply.
        foreach (var kindSpecificMenuItem in bookshelfContextMenu.Items.OfType<MenuItem>())
        {
            if (Equals(kindSpecificMenuItem.Tag, FileOnlyMenuTag))
            {
                kindSpecificMenuItem.Visibility = currentBookshelf.IsQueryShelf ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (Equals(kindSpecificMenuItem.Tag, QueryOnlyMenuTag))
            {
                kindSpecificMenuItem.Visibility = currentBookshelf.IsQueryShelf ? Visibility.Visible : Visibility.Collapsed;
                kindSpecificMenuItem.IsEnabled = SelectedBookshelfEntries.Count == 1;
            }
        }

        foreach (var submenu in bookshelfContextMenu.Items.OfType<MenuItem>())
        {
            bool isMoveSubmenu = Equals(submenu.Tag, MoveToShelfMenuTag);
            if (!isMoveSubmenu && !Equals(submenu.Tag, CopyToShelfMenuTag))
            {
                continue;
            }

            submenu.Items.Clear();
            // Moving would take the files off this shelf, which a folder shelf cannot do.
            submenu.IsEnabled = hasSelection && !(isMoveSubmenu && currentBookshelf.IsFolderShelf);

            if (otherShelves.Count == 0)
            {
                string noShelvesText = currentBookshelf.IsQueryShelf
                    ? "(no other query bookshelves yet: create one with the ⋯ button)"
                    : "(no other bookshelves yet: create one with the ⋯ button)";
                submenu.Items.Add(new MenuItem { Header = noShelvesText, IsEnabled = false });
                continue;
            }

            foreach (var targetShelf in otherShelves)
            {
                var shelfMenuItem = new MenuItem
                {
                    // A single "_" would be taken as a keyboard shortcut marker, so double it to show it as is.
                    Header = targetShelf.Name.Replace("_", "__"),
                    Tag = targetShelf,
                };
                shelfMenuItem.Click += isMoveSubmenu ? MoveToShelfMenuItem_Click : CopyToShelfMenuItem_Click;
                submenu.Items.Add(shelfMenuItem);
            }
        }
    }

    /// <summary>
    /// Puts the selected files on another shelf (at its end; files already there are skipped).
    /// With <paramref name="removeFromCurrentShelf"/> they are also taken off the shown shelf (a move).
    /// </summary>
    private void PutSelectedFilesOnShelf(NamedBookshelf targetShelf, bool removeFromCurrentShelf)
    {
        var selectedEntries = SelectedBookshelfEntries;
        if (selectedEntries.Count == 0)
        {
            return;
        }

        foreach (var selectedEntry in selectedEntries)
        {
            if (selectedEntry.Query != null)
            {
                // A moved query keeps its identity (so the shown result still belongs to it); a copied query becomes
                // a new, separate query, so editing one copy does not change the other.
                targetShelf.Entries.Add(removeFromCurrentShelf ? selectedEntry : BookshelfEntry.CreateForQuery(selectedEntry.Query.Clone()));
            }
            else if (!targetShelf.ContainsFile(selectedEntry.FilePath))
            {
                // A new entry, so the two shelves never share (and reorder) the same object.
                targetShelf.Entries.Add(new BookshelfEntry { FilePath = selectedEntry.FilePath, AddedOn = DateTime.Now });
            }

            if (removeFromCurrentShelf)
            {
                currentBookshelf.Entries.Remove(selectedEntry);
            }
        }

        SaveSettingsOrWarn();
        RefreshBookshelfList();
        UpdatePropertiesPanel();
    }

    /// <summary>Context menu → Add to bookshelf → (shelf): the files stay on this shelf too.</summary>
    private void CopyToShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (sender is MenuItem { Tag: NamedBookshelf targetShelf })
        {
            PutSelectedFilesOnShelf(targetShelf, removeFromCurrentShelf: false);
        }
    }

    /// <summary>Context menu → Move to bookshelf → (shelf): the files leave this shelf.</summary>
    private void MoveToShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (sender is MenuItem { Tag: NamedBookshelf targetShelf })
        {
            PutSelectedFilesOnShelf(targetShelf, removeFromCurrentShelf: true);
        }
    }
}
