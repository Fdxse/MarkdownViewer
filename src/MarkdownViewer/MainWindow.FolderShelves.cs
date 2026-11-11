using System.IO;
using System.Windows;
using System.Windows.Threading;
using MarkdownViewer.Bookshelf;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// Folder bookshelves: a shelf that always lists the Markdown files in one folder (optionally with
/// its subfolders). While such a shelf is shown, the folder is watched, so files that are added,
/// removed or renamed in Explorer (or by any other program) appear and disappear by themselves.
/// </summary>
public partial class MainWindow
{
    /// <summary>Text shown in an empty normal shelf.</summary>
    private const string EmptyShelfHintText =
        "This bookshelf is empty. Click Add..., press Ctrl+D to add the open file, or drop Markdown files (or a folder) here.";

    /// <summary>Watches the folder of the folder shelf that is shown; null while a normal shelf is shown.</summary>
    private FileSystemWatcher? shelfFolderWatcher;

    /// <summary>
    /// Waits a moment after the last change in the watched folder before re-reading it, because copying
    /// or saving a file raises several events in a row.
    /// </summary>
    private DispatcherTimer? shelfFolderChangeTimer;

    // ------------------------------------------------------------------
    // Reading and watching the folder
    // ------------------------------------------------------------------

    /// <summary>
    /// For a folder shelf: re-reads the folder and puts the right hint in the empty-list text.
    /// Returns true if the list of files changed (so the list on screen needs rebuilding).
    /// </summary>
    private bool RescanCurrentFolderShelf()
    {
        if (!currentBookshelf.IsFolderShelf)
        {
            BookshelfEmptyHint.Text = currentBookshelf.IsTrashShelf ? EmptyTrashHintText
                : currentBookshelf.IsQueryShelf ? EmptyQueryShelfHintText
                : EmptyShelfHintText;
            return false;
        }

        var previousFilePaths = currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        bool folderExists = currentBookshelf.RescanFolder();

        BookshelfEmptyHint.Text = currentBookshelf.IsArchiveShelf ? EmptyArchiveHintText
            : folderExists
            ? $"There are no Markdown files in this folder yet:\n{currentBookshelf.FolderPath}"
            : $"The folder was not found. It may have been moved, renamed or deleted, or its drive is not connected:\n{currentBookshelf.FolderPath}";

        var currentFilePaths = currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        return !previousFilePaths.SequenceEqual(currentFilePaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts watching the folder of the shown shelf (and stops watching the previous one).
    /// Does nothing for a normal shelf or a folder that does not exist.
    /// </summary>
    private void WatchCurrentShelfFolder()
    {
        StopWatchingShelfFolder();
        if (!currentBookshelf.IsFolderShelf || !Directory.Exists(currentBookshelf.FolderPath))
        {
            return;
        }

        if (shelfFolderChangeTimer == null)
        {
            shelfFolderChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            shelfFolderChangeTimer.Tick += (_, _) =>
            {
                shelfFolderChangeTimer.Stop();
                RefreshFolderShelfAfterChange();
            };
        }

        try
        {
            shelfFolderWatcher = new FileSystemWatcher(currentBookshelf.FolderPath!)
            {
                IncludeSubdirectories = currentBookshelf.IncludeSubfolders,
                // File and folder names: covers files added, deleted and renamed, and folders renamed or moved.
                // A post-it board shows the files' text too, so it also listens for files being saved.
                NotifyFilter = currentBookshelf.IsPostItBoard
                    ? NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    : NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            // Watcher events arrive on a background thread; restart the wait timer on the UI thread.
            void OnFolderChanged(object? sender, EventArgs changeDetails)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    shelfFolderChangeTimer.Stop();
                    shelfFolderChangeTimer.Start();
                });
            }

            shelfFolderWatcher.Created += OnFolderChanged;
            shelfFolderWatcher.Deleted += OnFolderChanged;
            shelfFolderWatcher.Renamed += OnFolderChanged;
            // Only raised for a post-it board (LastWrite is not watched otherwise): a file was saved.
            shelfFolderWatcher.Changed += OnFolderChanged;
            // Too many changes at once (the watcher's buffer overflowed): simply re-read the whole folder.
            shelfFolderWatcher.Error += OnFolderChanged;
            shelfFolderWatcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // Some network drives cannot be watched. The list is still re-read each time the window is activated.
            StopWatchingShelfFolder();
        }
    }

    /// <summary>Stops watching the shelf folder, if one is watched.</summary>
    private void StopWatchingShelfFolder()
    {
        shelfFolderWatcher?.Dispose();
        shelfFolderWatcher = null;
        shelfFolderChangeTimer?.Stop();
    }

    /// <summary>Re-reads the folder after a change and rebuilds the list only if files came or went.</summary>
    private void RefreshFolderShelfAfterChange()
    {
        if (!currentBookshelf.IsFolderShelf)
        {
            return;
        }

        if (RescanCurrentFolderShelf())
        {
            ShowBookshelfEntries();
        }
        // The notes on a post-it board are redrawn if a file came, went or was saved.
        RefreshPostItBoardIfChanged();
        UpdatePropertiesPanel();
    }

    /// <summary>
    /// Turns the buttons that change a shelf's files on or off: a folder shelf's files come from the
    /// folder, so files cannot be added, removed or reordered by hand there.
    /// </summary>
    private void UpdateShelfEditingControls()
    {
        bool canEditFiles = currentBookshelf.CanEditEntriesByHand;
        // In the Trash and the Archive the Add button is "Restore"; the Trash's Remove button deletes for good.
        bool isTrashOrArchive = currentBookshelf.IsTrashShelf || currentBookshelf.IsArchiveShelf;
        AddFilesButton.IsEnabled = canEditFiles || isTrashOrArchive;
        RemoveFilesButton.IsEnabled = canEditFiles || currentBookshelf.IsTrashShelf;

        // On a query shelf the Add button makes a new query, and Remove moves queries to the trash.
        AddFilesButton.Content = isTrashOrArchive ? "Restore" : currentBookshelf.IsQueryShelf ? "New..." : "Add...";
        AddFilesButton.ToolTip = currentBookshelf.IsTrashShelf ? "Put the selected items back where they came from"
            : currentBookshelf.IsArchiveShelf ? "Move the selected files back to where they were archived from"
            : currentBookshelf.IsQueryShelf ? "Add a new SQL Server query to this bookshelf"
            : "Add Markdown files to this bookshelf";
        RemoveFilesButton.Content = currentBookshelf.IsTrashShelf ? "Delete" : "Remove";
        RemoveFilesButton.ToolTip = currentBookshelf.IsTrashShelf ? "Delete the selected items for good"
            : currentBookshelf.IsQueryShelf ? "Move the selected queries to the trash"
            : "Remove the selected files from this bookshelf (the files themselves are not deleted)";

        // The Trash has its own right-click menu (Restore, Delete for good...).
        BookshelfList.ContextMenu = currentBookshelf.IsTrashShelf ? trashContextMenu : normalBookshelfContextMenu;

        // The drop-down's tooltip says which folder a folder shelf shows.
        ShelfChooser.ToolTip = currentBookshelf.IsTrashShelf
            ? $"Things moved to the trash. They go to the Windows Recycle Bin after {appSettings.TrashKeepDays} days ({trashBin.TrashFolderPath})"
            : currentBookshelf.IsArchiveShelf
            ? $"Archived files: the Archive folder {currentBookshelf.FolderPath}"
            : currentBookshelf.IsFolderShelf
            ? (currentBookshelf.IsPostItBoard ? "Post-it board: " : string.Empty)
                + $"Shows the Markdown files in {currentBookshelf.FolderPath}" + (currentBookshelf.IncludeSubfolders ? " and its subfolders" : string.Empty)
            : currentBookshelf.IsQueryShelf ? "A bookshelf of saved SQL Server queries" : "Choose a bookshelf";
    }

    /// <summary>
    /// Tells the user that files cannot be put on a folder shelf by hand. Returns true if the shown
    /// shelf is a folder shelf (so the caller should stop).
    /// </summary>
    private bool RejectChangeToFolderShelf()
    {
        if (currentBookshelf.CanEditEntriesByHand)
        {
            return false;
        }

        // The Trash and the Archive get their own explanation.
        if (currentBookshelf.IsTrashShelf || currentBookshelf.IsArchiveShelf)
        {
            string explanationText = currentBookshelf.IsTrashShelf
                ? "The Trash lists what was moved to the trash, so files cannot be added here.\n\nRight-click a file and choose Move to trash (Ctrl+Del) instead."
                : "The Archive lists the files in the Archive folder, so its list cannot be changed by hand.\n\n" +
                  "Right-click a file on another bookshelf and choose Archive to add it, or use Restore or Move to trash (Ctrl+Del) here.";
            MessageBox.Show(this, explanationText, "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        MessageBox.Show(this,
            $"\"{currentBookshelf.Name}\" shows the Markdown files in a folder, so files cannot be added, removed or reordered here.\n\n" +
            "Put the file in the folder instead, or switch to another bookshelf.",
            "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    // ------------------------------------------------------------------
    // Menu commands
    // ------------------------------------------------------------------

    /// <summary>Shelf menu → New folder bookshelf: picks a folder, asks about subfolders and a name, and shows the new shelf.</summary>
    private void NewFolderShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        CreateFolderShelf(asPostItBoard: false);
    }

    /// <summary>
    /// Creates a folder shelf: picks a folder, asks about subfolders and a name, and shows the new shelf.
    /// With <paramref name="asPostItBoard"/> the shelf shows its files as yellow post-it notes.
    /// </summary>
    private void CreateFolderShelf(bool asPostItBoard)
    {
        string shelfKindText = asPostItBoard ? "post-it board" : "folder bookshelf";
        var folderDialog = new OpenFolderDialog
        {
            Title = asPostItBoard
                ? "Choose the folder whose Markdown files are shown as post-it notes"
                : "Choose the folder whose Markdown files this bookshelf shows",
            InitialDirectory = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null,
        };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }
        string chosenFolderPath = folderDialog.FolderName;

        var subfolderAnswer = MessageBox.Show(this,
            $"Also show the Markdown files in the subfolders of\n{chosenFolderPath}?\n\n(You can change this later in the ⋯ menu.)",
            $"New {shelfKindText}", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (subfolderAnswer == MessageBoxResult.Cancel)
        {
            return;
        }

        // Suggest the folder's own name, e.g. "Documentation" for C:\Projects\Documentation.
        string suggestedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(chosenFolderPath));
        string? newShelfName = AskForShelfName($"New {shelfKindText}",
            $"Name of the {shelfKindText} that shows the Markdown files in\n{chosenFolderPath}:", suggestedName, shelfBeingRenamed: null);
        if (newShelfName == null)
        {
            return;
        }

        var newShelf = new NamedBookshelf
        {
            Name = newShelfName,
            FolderPath = chosenFolderPath,
            IncludeSubfolders = subfolderAnswer == MessageBoxResult.Yes,
            IsPostItBoard = asPostItBoard,
        };
        appSettings.Bookshelves.Add(newShelf);
        SaveSettingsOrWarn();
        ShowBookshelf(newShelf);

        if (newShelf.WasFolderListCutShort)
        {
            MessageBox.Show(this,
                $"The folder holds more than {NamedBookshelf.MaximumFolderShelfFileCount:N0} Markdown files, so only the first ones are listed.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Shelf menu → Include subfolders (folder shelves only): switches subfolders on or off.</summary>
    private void IncludeSubfoldersMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        // The Archive always shows its subfolders (one per bookshelf the files came from).
        if (!currentBookshelf.IsFolderShelf || currentBookshelf.IsArchiveShelf)
        {
            return;
        }

        currentBookshelf.IncludeSubfolders = !currentBookshelf.IncludeSubfolders;
        SaveSettingsOrWarn();
        // Re-reads the folder and restarts the watcher with the new setting.
        ShowBookshelf(currentBookshelf);
    }

    /// <summary>Shelf menu → Open the folder (folder shelves and the Trash): shows the folder in Explorer.</summary>
    private void OpenShelfFolderMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentBookshelf.IsTrashShelf)
        {
            // The Trash folder is only made when something is first moved to the trash.
            Directory.CreateDirectory(trashBin.TrashFolderPath);
            OpenWithDefaultProgram(trashBin.TrashFolderPath);
        }
        else if (currentBookshelf.IsFolderShelf)
        {
            OpenWithDefaultProgram(currentBookshelf.FolderPath!);
        }
    }
}
