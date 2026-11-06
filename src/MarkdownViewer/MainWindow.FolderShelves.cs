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
            BookshelfEmptyHint.Text = currentBookshelf.IsQueryShelf ? EmptyQueryShelfHintText : EmptyShelfHintText;
            return false;
        }

        var previousFilePaths = currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        bool folderExists = currentBookshelf.RescanFolder();

        BookshelfEmptyHint.Text = folderExists
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
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
        UpdatePropertiesPanel();
    }

    /// <summary>
    /// Turns the buttons that change a shelf's files on or off: a folder shelf's files come from the
    /// folder, so files cannot be added, removed or reordered by hand there.
    /// </summary>
    private void UpdateShelfEditingControls()
    {
        bool canEditFiles = !currentBookshelf.IsFolderShelf;
        AddFilesButton.IsEnabled = canEditFiles;
        RemoveFilesButton.IsEnabled = canEditFiles;

        // On a query shelf the Add button makes a new query, and Remove deletes queries.
        AddFilesButton.Content = currentBookshelf.IsQueryShelf ? "New..." : "Add...";
        AddFilesButton.ToolTip = currentBookshelf.IsQueryShelf ? "Add a new SQL Server query to this bookshelf" : "Add Markdown files to this bookshelf";
        RemoveFilesButton.ToolTip = currentBookshelf.IsQueryShelf
            ? "Delete the selected queries"
            : "Remove the selected files from this bookshelf (the files themselves are not deleted)";

        // The drop-down's tooltip says which folder a folder shelf shows.
        ShelfChooser.ToolTip = currentBookshelf.IsFolderShelf
            ? $"Shows the Markdown files in {currentBookshelf.FolderPath}" + (currentBookshelf.IncludeSubfolders ? " and its subfolders" : string.Empty)
            : currentBookshelf.IsQueryShelf ? "A bookshelf of saved SQL Server queries" : "Choose a bookshelf";
    }

    /// <summary>
    /// Tells the user that files cannot be put on a folder shelf by hand. Returns true if the shown
    /// shelf is a folder shelf (so the caller should stop).
    /// </summary>
    private bool RejectChangeToFolderShelf()
    {
        if (!currentBookshelf.IsFolderShelf)
        {
            return false;
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
        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose the folder whose Markdown files this bookshelf shows",
            InitialDirectory = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null,
        };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }
        string chosenFolderPath = folderDialog.FolderName;

        var subfolderAnswer = MessageBox.Show(this,
            $"Also show the Markdown files in the subfolders of\n{chosenFolderPath}?\n\n(You can change this later in the ⋯ menu.)",
            "New folder bookshelf", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (subfolderAnswer == MessageBoxResult.Cancel)
        {
            return;
        }

        // Suggest the folder's own name, e.g. "Documentation" for C:\Projects\Documentation.
        string suggestedName = Path.GetFileName(Path.TrimEndingDirectorySeparator(chosenFolderPath));
        string? newShelfName = AskForShelfName("New folder bookshelf",
            $"Name of the bookshelf that shows the Markdown files in\n{chosenFolderPath}:", suggestedName, shelfBeingRenamed: null);
        if (newShelfName == null)
        {
            return;
        }

        var newShelf = new NamedBookshelf
        {
            Name = newShelfName,
            FolderPath = chosenFolderPath,
            IncludeSubfolders = subfolderAnswer == MessageBoxResult.Yes,
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
        if (!currentBookshelf.IsFolderShelf)
        {
            return;
        }

        currentBookshelf.IncludeSubfolders = !currentBookshelf.IncludeSubfolders;
        SaveSettingsOrWarn();
        // Re-reads the folder and restarts the watcher with the new setting.
        ShowBookshelf(currentBookshelf);
    }

    /// <summary>Shelf menu → Open the folder (folder shelves only): shows the folder in Explorer.</summary>
    private void OpenShelfFolderMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentBookshelf.IsFolderShelf)
        {
            OpenWithDefaultProgram(currentBookshelf.FolderPath!);
        }
    }
}
