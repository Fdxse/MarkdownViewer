using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MarkdownViewer.Archiving;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Storage;
using MarkdownViewer.Todo;
using MarkdownViewer.Trash;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// Trash and archive.
/// <list type="bullet">
/// <item>Trash: "Move to trash" (Ctrl+Del) moves files into the app's own Trash folder next to the exe. Queries and
/// deleted bookshelves go there too. The Trash shelf at the end of the drop-down lists it all, with Restore (back where
/// it came from, and back on its shelves) and Delete for good. After <see cref="Macros.AppSettings.TrashKeepDays"/>
/// days (30 by default) items are moved on to the Windows Recycle Bin.</item>
/// <item>Archive: "Archive" moves a finished file into the Archive folder next to the exe (in a subfolder named after
/// its bookshelf). The Archive shelf shows that folder, global search still finds the files, and Restore puts a file
/// back. Todo → Archive done tasks moves the ticked tasks of todo.md into Archive\todo-archive.md.</item>
/// </list>
/// </summary>
public partial class MainWindow
{
    /// <summary>Tag of the "Archive" item in the file list's context menu (files on any shelf except the Archive).</summary>
    private const string ArchiveFilesMenuTag = "ArchiveFiles";

    /// <summary>Tag of the "Restore from archive" item in the file list's context menu (Archive shelf only).</summary>
    private const string RestoreFromArchiveMenuTag = "RestoreFromArchive";

    /// <summary>Tag of the "Empty the trash" item in the shelf menu.</summary>
    private const string EmptyTrashMenuTag = "EmptyTrash";

    /// <summary>Start of the made-up path of a trashed query or bookshelf in the Trash list (they have no file).</summary>
    private const string TrashEntryPathPrefix = "trash:";

    /// <summary>Name of the Trash shelf in the drop-down.</summary>
    private const string TrashShelfName = "Trash";

    /// <summary>Text shown in the Trash list while it is empty.</summary>
    private string EmptyTrashHintText =>
        "The trash is empty. Files, post-it notes, queries and bookshelves you move to the trash (Ctrl+Del) are kept here" +
        (appSettings.TrashKeepDays > 0 ? $" for {appSettings.TrashKeepDays} days, then moved to the Windows Recycle Bin." : " until you empty it.");

    /// <summary>Text shown in the Archive list while nothing is archived.</summary>
    private const string EmptyArchiveHintText =
        "Nothing is archived yet. Right-click a file or a post-it note and choose Archive to move it here. " +
        "Todo → Archive done tasks moves the ticked tasks of the todo list here too.";

    /// <summary>The app's own trash folder.</summary>
    private TrashBin trashBin = null!;

    /// <summary>The archive folder.</summary>
    private DocumentArchive documentArchive = null!;

    /// <summary>The Archive shelf: a folder shelf on the Archive folder. It is in the bookshelf list (so search, Zip all and the properties panel see it) but never saved.</summary>
    private NamedBookshelf archiveShelf = null!;

    /// <summary>The Trash shelf. Not in the bookshelf list (its files must not be searched or backed up), only in the drop-down.</summary>
    private NamedBookshelf trashShelf = null!;

    /// <summary>The file list's normal context menu (from the XAML), swapped for <see cref="trashContextMenu"/> while the Trash is shown.</summary>
    private ContextMenu? normalBookshelfContextMenu;

    /// <summary>The file list's context menu while the Trash is shown.</summary>
    private ContextMenu trashContextMenu = null!;

    // ------------------------------------------------------------------
    // Setting up
    // ------------------------------------------------------------------

    /// <summary>
    /// Finds the Trash and Archive folders and adds the Archive and Trash shelves. Called once from the constructor,
    /// before the bookshelf panel is set up. Moving old trash to the Recycle Bin waits until the window is idle.
    /// </summary>
    private void InitializeTrashAndArchive()
    {
        trashBin = new TrashBin(StorageFolders.ChooseFolder(TrashBin.FolderName));
        documentArchive = new DocumentArchive(StorageFolders.ChooseFolder(DocumentArchive.FolderName));

        RenameShelvesNamedLikeBuiltInShelves();

        archiveShelf = new NamedBookshelf
        {
            Name = DocumentArchive.FolderName,
            FolderPath = documentArchive.ArchiveFolderPath,
            IncludeSubfolders = true,
            BuiltInKind = BuiltInShelfKind.Archive,
        };
        appSettings.Bookshelves.Add(archiveShelf);

        trashShelf = new NamedBookshelf { Name = TrashShelfName, BuiltInKind = BuiltInShelfKind.Trash };
        RebuildTrashShelfEntries();

        // The list's own context menu (from the XAML) is swapped for the Trash menu while the Trash is shown.
        normalBookshelfContextMenu = BookshelfList.ContextMenu;
        trashContextMenu = CreateTrashContextMenu();

        Dispatcher.BeginInvoke(MoveExpiredTrashToRecycleBin, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// A shelf the user made earlier with the name "Archive" or "Trash" gets "(my shelf)" added to its name,
    /// so the drop-down never shows two shelves with the same name.
    /// </summary>
    private void RenameShelvesNamedLikeBuiltInShelves()
    {
        bool anyShelfRenamed = false;
        foreach (var clashingShelf in appSettings.Bookshelves.Where(bookshelf =>
            bookshelf.HasName(DocumentArchive.FolderName) || bookshelf.HasName(TrashShelfName)))
        {
            clashingShelf.Name = MakeUniqueShelfName($"{clashingShelf.Name} (my shelf)");
            anyShelfRenamed = true;
        }

        if (anyShelfRenamed)
        {
            SaveSettingsOrWarn();
        }
    }

    /// <summary>The given shelf name if no shelf has it yet, otherwise "name (2)", "name (3)"...</summary>
    private string MakeUniqueShelfName(string wantedName)
    {
        string candidateName = wantedName;
        int copyNumber = 2;
        while (appSettings.Bookshelves.Any(bookshelf => bookshelf.HasName(candidateName))
            || candidateName.Equals(DocumentArchive.FolderName, StringComparison.CurrentCultureIgnoreCase)
            || candidateName.Equals(TrashShelfName, StringComparison.CurrentCultureIgnoreCase))
        {
            candidateName = $"{wantedName} ({copyNumber})";
            copyNumber++;
        }
        return candidateName;
    }

    /// <summary>The shelves for the drop-down: the user's shelves (Standard first), then Archive and Trash at the end.</summary>
    private List<NamedBookshelf> GetShelvesForChooser()
    {
        var chooserShelves = appSettings.Bookshelves.Where(bookshelf => !bookshelf.IsBuiltInShelf).ToList();
        chooserShelves.Add(archiveShelf);
        chooserShelves.Add(trashShelf);
        return chooserShelves;
    }

    /// <summary>Fills the Trash shelf's list from what is in the trash (newest first).</summary>
    private void RebuildTrashShelfEntries()
    {
        trashShelf.Entries = trashBin.Items
            .Select(trashItem => new BookshelfEntry
            {
                // A trashed file is listed (and opened) where it is now; a query or bookshelf gets a made-up path.
                FilePath = trashItem.StoredFilePath ?? TrashEntryPathPrefix + trashItem.ItemFolderPath,
                AddedOn = trashItem.DeletedOn,
                TrashItem = trashItem,
            })
            .ToList();
    }

    /// <summary>Re-reads the trash folder (something may have changed in Explorer) and refills the Trash shelf.</summary>
    private void ReloadTrashShelf()
    {
        trashBin.Reload();
        RebuildTrashShelfEntries();
    }

    /// <summary>
    /// After anything was moved to or from the trash or the archive: saves the bookshelves, refreshes the drop-down
    /// (the Trash shows a count), the list, a post-it board and the properties, and brings the search index up to date.
    /// </summary>
    private void RefreshAfterTrashOrArchiveChange()
    {
        RebuildTrashShelfEntries();
        SaveSettingsOrWarn();
        RefreshShelfChooser();
        RefreshBookshelfList();
        RefreshPostItBoardIfChanged();
        UpdatePropertiesPanel();
        _ = RefreshSearchIndexAsync(forceRereadAllFiles: false);
    }

    // ------------------------------------------------------------------
    // Shelves a file is on
    // ------------------------------------------------------------------

    /// <summary>The user's normal bookshelves (not folder, query, Archive or Trash shelves) that the file is on.</summary>
    private List<NamedBookshelf> FindNormalShelvesWithFile(string filePath)
    {
        return appSettings.Bookshelves
            .Where(bookshelf => !bookshelf.IsFolderShelf && !bookshelf.IsQueryShelf && !bookshelf.IsBuiltInShelf && bookshelf.ContainsFile(filePath))
            .ToList();
    }

    /// <summary>Takes a file off every normal bookshelf (it is moved away, so the entries would only show "File not found").</summary>
    private void TakeFileOffNormalShelves(string filePath)
    {
        foreach (var bookshelf in FindNormalShelvesWithFile(filePath))
        {
            bookshelf.Entries.RemoveAll(bookshelfEntry => bookshelfEntry.HasPath(filePath));
        }
    }

    /// <summary>
    /// Puts a restored file back on the normal bookshelves it was on (those that still exist). If it ends up on no
    /// shelf at all (and is in no folder shelf's folder either) it goes on the Standard shelf, so it is not lost.
    /// Returns the names of the shelves it was put on.
    /// </summary>
    private List<string> PutFileBackOnShelves(string filePath, IEnumerable<string> shelfNames)
    {
        var shelvesPutOn = new List<string>();
        foreach (string shelfName in shelfNames)
        {
            var bookshelf = appSettings.Bookshelves.FirstOrDefault(candidateShelf =>
                candidateShelf.HasName(shelfName) && !candidateShelf.IsFolderShelf && !candidateShelf.IsQueryShelf && !candidateShelf.IsBuiltInShelf);
            if (bookshelf == null)
            {
                continue;
            }
            if (!bookshelf.ContainsFile(filePath))
            {
                bookshelf.Entries.Add(new BookshelfEntry { FilePath = filePath, AddedOn = DateTime.Now });
            }
            shelvesPutOn.Add(bookshelf.Name);
        }

        bool isInAFolderShelf = appSettings.Bookshelves.Any(bookshelf => bookshelf.IsFolderShelf && bookshelf.ContainsFile(filePath));
        if (shelvesPutOn.Count == 0 && !isInAFolderShelf)
        {
            appSettings.StandardBookshelf.Entries.Add(new BookshelfEntry { FilePath = filePath, AddedOn = DateTime.Now });
            shelvesPutOn.Add(appSettings.StandardBookshelf.Name);
        }
        return shelvesPutOn;
    }

    /// <summary>True if the file is inside the Trash folder (a trashed file that is being viewed).</summary>
    private bool IsInTrashFolder(string filePath)
    {
        string trashFolderWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trashBin.TrashFolderPath)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(trashFolderWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if the given path is the document that is open.</summary>
    private bool IsOpenDocument(string filePath)
    {
        return currentFilePath != null && string.Equals(Path.GetFullPath(currentFilePath), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// After the open document was moved elsewhere (archived or restored): keeps it open, with any unsaved edits,
    /// at its new place, so Ctrl+S saves there.
    /// </summary>
    private void FollowMovedOpenDocument(string newFilePath)
    {
        currentFilePath = Path.GetFullPath(newFilePath);
        WatchFile(currentFilePath);
        // Images with relative paths are looked up next to the file; the board, if shown, stays on screen.
        RenderPreview();
        UpdateTitleAndStatus();
    }

    /// <summary>After the open document was moved into the trash or deleted: shows the welcome page (and the board again on a post-it board).</summary>
    private void CloseRemovedOpenDocument()
    {
        bool wasBoardShown = isPostItBoardShown;
        ShowWelcomePage();
        if (wasBoardShown || currentBookshelf.IsPostItBoard)
        {
            ShowOrHidePostItBoardForCurrentShelf();
        }
    }

    /// <summary>Shows a list of things that went wrong, one per line (at most ten).</summary>
    private void ShowProblems(string introText, List<string> problemLines)
    {
        if (problemLines.Count == 0)
        {
            return;
        }

        const int maximumProblemsShown = 10;
        string problemText = string.Join("\n", problemLines.Take(maximumProblemsShown))
            + (problemLines.Count > maximumProblemsShown ? "\n..." : string.Empty);
        MessageBox.Show(this, $"{introText}\n\n{problemText}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ------------------------------------------------------------------
    // Moving to the trash
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves bookshelf entries to the trash: files are moved into the Trash folder (and taken off every shelf),
    /// queries are taken off their query shelf. Entries that are already in the trash are ignored.
    /// </summary>
    private void MoveEntriesToTrash(IReadOnlyCollection<BookshelfEntry> entriesToTrash)
    {
        if (currentBookshelf.IsTrashShelf || entriesToTrash.Count == 0)
        {
            return;
        }

        // Queries are stored in the settings, so they are written into the trash and taken off their shelf.
        var queryEntries = entriesToTrash.Where(bookshelfEntry => bookshelfEntry.IsQuery).ToList();
        var problemLines = new List<string>();
        foreach (var queryEntry in queryEntries)
        {
            try
            {
                trashBin.AddQuery(queryEntry, currentBookshelf.Name);
                currentBookshelf.Entries.Remove(queryEntry);

                // A trashed query can no longer be re-run or edited; its last result stays on screen as an untitled document.
                if (ReferenceEquals(queryEntry, currentQueryEntry))
                {
                    currentQueryEntry = null;
                    currentQueryRunResult = null;
                    UpdateTitleAndStatus();
                }
            }
            catch (Exception trashError)
            {
                problemLines.Add($"{queryEntry.FileName}: {trashError.Message}");
            }
        }

        var filePaths = entriesToTrash.Where(bookshelfEntry => !bookshelfEntry.HasNoFile).Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        if (filePaths.Count > 0)
        {
            // Saves the settings and refreshes everything, including for the queries above.
            MoveFilesToTrash(filePaths, problemLines);
        }
        else
        {
            RefreshAfterTrashOrArchiveChange();
            ShowProblems("These could not be moved to the trash:", problemLines);
        }
    }

    /// <summary>
    /// Moves files into the Trash folder and takes them off every bookshelf. If the open document is one of them,
    /// the user is first asked about unsaved changes, and the welcome page is shown afterwards.
    /// </summary>
    /// <param name="filePaths">The files to move.</param>
    /// <param name="earlierProblemLines">Problems from the caller to report together with these (may be null).</param>
    private void MoveFilesToTrash(IReadOnlyCollection<string> filePaths, List<string>? earlierProblemLines = null)
    {
        var problemLines = earlierProblemLines ?? new List<string>();
        bool isOpenDocumentAmongThem = filePaths.Any(IsOpenDocument);
        if (isOpenDocumentAmongThem)
        {
            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }
            // Stop watching it, so moving it is not taken for a change by another program.
            WatchFile(null);
        }

        int movedFileCount = 0;
        bool wasOpenDocumentMoved = false;
        foreach (string filePath in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var shelfNames = FindNormalShelvesWithFile(filePath).Select(bookshelf => bookshelf.Name);
                trashBin.MoveFileToTrash(filePath, shelfNames);
                TakeFileOffNormalShelves(filePath);
                movedFileCount++;
                wasOpenDocumentMoved |= IsOpenDocument(filePath);
            }
            catch (Exception trashError)
            {
                problemLines.Add($"{Path.GetFileName(filePath)}: {trashError.Message}");
            }
        }

        if (wasOpenDocumentMoved)
        {
            CloseRemovedOpenDocument();
        }
        else if (isOpenDocumentAmongThem)
        {
            WatchFile(currentFilePath);
        }

        RefreshAfterTrashOrArchiveChange();
        if (movedFileCount > 0)
        {
            DocumentInfoStatusText.Text = movedFileCount == 1
                ? "Moved 1 file to the trash (restore it from the Trash bookshelf)"
                : $"Moved {movedFileCount} files to the trash (restore them from the Trash bookshelf)";
        }
        ShowProblems("These could not be moved to the trash:", problemLines);
    }

    /// <summary>Shelf menu → Delete this bookshelf: moves the shelf (not its files) to the trash, after asking.</summary>
    private void MoveCurrentShelfToTrash()
    {
        if (currentBookshelf.IsStandardShelf || currentBookshelf.IsBuiltInShelf)
        {
            return;
        }

        int entryCount = currentBookshelf.Entries.Count;
        string whatHappensText = currentBookshelf.IsFolderShelf
            ? $"It shows the folder {currentBookshelf.FolderPath}. The folder and its files are not touched."
            : currentBookshelf.IsQueryShelf
                ? $"It holds {(entryCount == 1 ? "1 query" : $"{entryCount} queries")}, which go to the trash with it (the databases are not touched)."
                : $"It holds {(entryCount == 1 ? "1 file" : $"{entryCount} files")}. The files themselves are not touched.";
        var userAnswer = MessageBox.Show(this,
            $"Move the bookshelf \"{currentBookshelf.Name}\" to the trash?\n\n{whatHappensText}\n\nYou can restore it from the Trash bookshelf.",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (userAnswer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            trashBin.AddBookshelf(currentBookshelf);
        }
        catch (Exception trashError)
        {
            MessageBox.Show(this, $"The bookshelf could not be moved to the trash:\n{trashBin.TrashFolderPath}\n\n{trashError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // A query from the shelf whose result is shown can no longer be re-run.
        if (currentQueryEntry != null && currentBookshelf.Entries.Contains(currentQueryEntry))
        {
            currentQueryEntry = null;
            currentQueryRunResult = null;
            UpdateTitleAndStatus();
        }

        appSettings.Bookshelves.Remove(currentBookshelf);
        currentBookshelf = appSettings.StandardBookshelf;
        RefreshAfterTrashOrArchiveChange();
        ShowBookshelf(appSettings.StandardBookshelf);
    }

    // ------------------------------------------------------------------
    // The Trash shelf: restoring and deleting for good
    // ------------------------------------------------------------------

    /// <summary>Puts trashed items back: files where they were (and on their shelves), queries on their shelf, bookshelves in the list.</summary>
    private void RestoreTrashEntries(IReadOnlyCollection<BookshelfEntry> trashEntries)
    {
        var problemLines = new List<string>();
        var restoredDescriptions = new List<string>();
        foreach (var trashItem in trashEntries.Select(bookshelfEntry => bookshelfEntry.TrashItem).OfType<TrashItem>())
        {
            try
            {
                string? restoredDescription = trashItem.Kind switch
                {
                    TrashItemKind.File => RestoreTrashedFile(trashItem),
                    TrashItemKind.Query => RestoreTrashedQuery(trashItem),
                    _ => RestoreTrashedBookshelf(trashItem),
                };
                if (restoredDescription != null)
                {
                    restoredDescriptions.Add(restoredDescription);
                }
            }
            catch (Exception restoreError)
            {
                problemLines.Add($"{trashItem.DisplayName}: {restoreError.Message}");
            }
        }

        RefreshAfterTrashOrArchiveChange();
        if (restoredDescriptions.Count > 0)
        {
            DocumentInfoStatusText.Text = restoredDescriptions.Count == 1 ? restoredDescriptions[0] : $"Restored {restoredDescriptions.Count} items";
        }
        ShowProblems("These could not be restored:", problemLines);
    }

    /// <summary>Moves a trashed file back to where it was and onto its shelves. Returns a short description, or null if the user skipped it.</summary>
    private string? RestoreTrashedFile(TrashItem trashItem)
    {
        string? targetFilePath = ChooseRestoreTarget(trashItem.OriginalPath);
        if (targetFilePath == null)
        {
            return null;
        }

        // If the trashed file is open (it can be viewed in the Trash), it follows along to its old place.
        bool isOpen = IsOpenDocument(trashItem.StoredFilePath!);
        if (isOpen)
        {
            WatchFile(null);
        }
        try
        {
            trashBin.RestoreFile(trashItem, targetFilePath);
        }
        finally
        {
            if (isOpen)
            {
                WatchFile(currentFilePath);
            }
        }
        if (isOpen)
        {
            FollowMovedOpenDocument(targetFilePath);
        }

        var shelfNames = PutFileBackOnShelves(targetFilePath, trashItem.ShelfNames);
        string shelvesText = shelfNames.Count > 0 ? $" (on {string.Join(", ", shelfNames)})" : string.Empty;
        return $"Restored {Path.GetFileName(targetFilePath)} to {Path.GetDirectoryName(targetFilePath)}{shelvesText}";
    }

    /// <summary>Puts a trashed query back on its query shelf (making that shelf again if it is gone). Returns a short description.</summary>
    private string RestoreTrashedQuery(TrashItem trashItem)
    {
        var queryEntry = trashItem.QueryEntry ?? throw new InvalidOperationException("The trashed query has no query in it.");
        string wantedShelfName = trashItem.ShelfNames.FirstOrDefault() ?? "Queries";

        var queryShelf = appSettings.Bookshelves.FirstOrDefault(bookshelf => bookshelf.IsQueryShelf && bookshelf.HasName(wantedShelfName));
        if (queryShelf == null)
        {
            queryShelf = new NamedBookshelf { Name = MakeUniqueShelfName(wantedShelfName), IsQueryShelf = true };
            appSettings.Bookshelves.Add(queryShelf);
        }

        // Its id must still be unique (it would only clash if the same query was restored from a copy of the trash).
        bool isIdTaken = appSettings.Bookshelves.SelectMany(bookshelf => bookshelf.Entries).Any(bookshelfEntry => bookshelfEntry.HasPath(queryEntry.FilePath));
        if (isIdTaken || !queryEntry.FilePath.StartsWith(BookshelfEntry.QueryPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            queryEntry.FilePath = BookshelfEntry.QueryPathPrefix + Guid.NewGuid().ToString("N");
        }

        queryShelf.Entries.Add(queryEntry);
        trashBin.Forget(trashItem);
        return $"Restored the query \"{trashItem.Name}\" to the bookshelf \"{queryShelf.Name}\"";
    }

    /// <summary>Puts a trashed bookshelf back in the list (with "(restored)" added if its name is taken). Returns a short description.</summary>
    private string RestoreTrashedBookshelf(TrashItem trashItem)
    {
        var restoredShelf = trashItem.Bookshelf ?? throw new InvalidOperationException("The trashed bookshelf is empty.");
        if (appSettings.Bookshelves.Any(bookshelf => bookshelf.HasName(restoredShelf.Name)))
        {
            restoredShelf.Name = MakeUniqueShelfName($"{restoredShelf.Name} (restored)");
        }
        restoredShelf.Entries ??= new List<BookshelfEntry>();

        appSettings.Bookshelves.Add(restoredShelf);
        trashBin.Forget(trashItem);
        return $"Restored the bookshelf \"{restoredShelf.Name}\"";
    }

    /// <summary>
    /// Where to put a file back: its old path, or, if a file with that name is there now, a new name next to it
    /// ("notes (2).md") if the user agrees. Null if the user chose to skip it.
    /// </summary>
    private string? ChooseRestoreTarget(string wantedFilePath)
    {
        if (!File.Exists(wantedFilePath))
        {
            return wantedFilePath;
        }

        string folderPath = Path.GetDirectoryName(wantedFilePath) ?? string.Empty;
        string newFilePath = StorageFolders.GetUnusedFilePath(folderPath, Path.GetFileName(wantedFilePath));
        var userAnswer = MessageBox.Show(this,
            $"There is already a file called {Path.GetFileName(wantedFilePath)} in\n{folderPath}\n\n" +
            $"Keep both? The restored file is then called {Path.GetFileName(newFilePath)}.\n(No skips this file.)",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return userAnswer == MessageBoxResult.Yes ? newFilePath : null;
    }

    /// <summary>Deletes trashed items for good, after asking. This cannot be undone.</summary>
    private void DeleteTrashEntriesForGood(IReadOnlyCollection<BookshelfEntry> trashEntries)
    {
        var trashItems = trashEntries.Select(bookshelfEntry => bookshelfEntry.TrashItem).OfType<TrashItem>().ToList();
        if (trashItems.Count == 0)
        {
            return;
        }

        string whatText = trashItems.Count == 1 ? $"\"{trashItems[0].DisplayName}\"" : $"these {trashItems.Count} items";
        var userAnswer = MessageBox.Show(this, $"Delete {whatText} for good? This cannot be undone.",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (userAnswer == MessageBoxResult.Yes)
        {
            DeleteTrashItemsForGood(trashItems);
        }
    }

    /// <summary>Shelf menu → Empty the trash: deletes everything in the trash for good, after asking.</summary>
    private void EmptyTrash()
    {
        ReloadTrashShelf();
        if (trashBin.Items.Count == 0)
        {
            MessageBox.Show(this, "The trash is already empty.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string countText = trashBin.Items.Count == 1 ? "the 1 item" : $"all {trashBin.Items.Count} items";
        var userAnswer = MessageBox.Show(this, $"Delete {countText} in the trash for good? This cannot be undone.",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (userAnswer == MessageBoxResult.Yes)
        {
            DeleteTrashItemsForGood(trashBin.Items.ToList());
        }
    }

    /// <summary>Deletes the given trash items for good (no question asked here) and reports any that could not be deleted.</summary>
    private void DeleteTrashItemsForGood(List<TrashItem> trashItems)
    {
        // A trashed file that is open in the viewer is closed first.
        if (trashItems.Any(trashItem => trashItem.StoredFilePath != null && IsOpenDocument(trashItem.StoredFilePath)))
        {
            WatchFile(null);
            CloseRemovedOpenDocument();
        }

        var problemLines = new List<string>();
        foreach (var trashItem in trashItems)
        {
            try
            {
                trashBin.DeleteForGood(trashItem);
            }
            catch (Exception deleteError)
            {
                problemLines.Add($"{trashItem.DisplayName}: {deleteError.Message}");
            }
        }

        RefreshAfterTrashOrArchiveChange();
        ShowProblems("These could not be deleted:", problemLines);
    }

    /// <summary>
    /// At startup (once the window is idle): moves everything that has been in the trash longer than the
    /// TrashKeepDays setting (30 days by default) to the Windows Recycle Bin.
    /// </summary>
    private void MoveExpiredTrashToRecycleBin()
    {
        int movedItemCount = trashBin.MoveExpiredItemsToRecycleBin(appSettings.TrashKeepDays);
        if (movedItemCount == 0)
        {
            return;
        }

        RebuildTrashShelfEntries();
        RefreshShelfChooser();
        if (currentBookshelf.IsTrashShelf)
        {
            ShowBookshelfEntries();
        }
        DocumentInfoStatusText.Text = movedItemCount == 1
            ? $"Moved 1 item older than {appSettings.TrashKeepDays} days from the Trash to the Windows Recycle Bin"
            : $"Moved {movedItemCount} items older than {appSettings.TrashKeepDays} days from the Trash to the Windows Recycle Bin";
    }

    /// <summary>The file list's context menu while the Trash is shown: Open, Restore, Show in folder, Delete for good, Empty the trash.</summary>
    private ContextMenu CreateTrashContextMenu()
    {
        var openMenuItem = new MenuItem { Header = "_Open" };
        openMenuItem.Click += (_, _) =>
        {
            if (BookshelfList.SelectedItem is BookshelfEntry selectedEntry)
            {
                OpenBookshelfEntry(selectedEntry);
            }
        };

        var restoreMenuItem = new MenuItem { Header = "_Restore", ToolTip = "Put the selected items back where they came from" };
        restoreMenuItem.Click += (_, _) => RestoreTrashEntries(SelectedBookshelfEntries);

        var showInFolderMenuItem = new MenuItem { Header = "Show in _folder" };
        showInFolderMenuItem.Click += (_, _) =>
        {
            if (BookshelfList.SelectedItem is BookshelfEntry { TrashItem: TrashItem selectedItem })
            {
                ShowFileInExplorer(selectedItem.StoredFilePath ?? Path.Combine(selectedItem.ItemFolderPath, TrashItem.DescriptionFileName));
            }
        };

        var deleteMenuItem = new MenuItem { Header = "_Delete for good...", InputGestureText = "Del" };
        deleteMenuItem.Click += (_, _) => DeleteTrashEntriesForGood(SelectedBookshelfEntries);

        var emptyTrashMenuItem = new MenuItem { Header = "_Empty the trash..." };
        emptyTrashMenuItem.Click += (_, _) => EmptyTrash();

        var trashMenu = new ContextMenu();
        trashMenu.Items.Add(openMenuItem);
        trashMenu.Items.Add(restoreMenuItem);
        trashMenu.Items.Add(showInFolderMenuItem);
        trashMenu.Items.Add(new Separator());
        trashMenu.Items.Add(deleteMenuItem);
        trashMenu.Items.Add(emptyTrashMenuItem);
        trashMenu.Opened += (_, _) =>
        {
            // Everything except "Empty the trash" needs a selection; Open and Show in folder need exactly one item.
            int selectedCount = BookshelfList.SelectedItems.Count;
            openMenuItem.IsEnabled = selectedCount == 1;
            showInFolderMenuItem.IsEnabled = selectedCount == 1;
            restoreMenuItem.IsEnabled = selectedCount > 0;
            deleteMenuItem.IsEnabled = selectedCount > 0;
            emptyTrashMenuItem.IsEnabled = trashShelf.Entries.Count > 0;
        };
        return trashMenu;
    }

    // ------------------------------------------------------------------
    // Archiving
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves files into the Archive folder (each into the subfolder of the bookshelf it was archived from) and takes
    /// them off the normal shelves. The open document stays open at its new place, unsaved edits included.
    /// </summary>
    private void ArchiveFiles(IReadOnlyCollection<string> filePaths)
    {
        var problemLines = new List<string>();
        int archivedFileCount = 0;
        foreach (string filePath in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (documentArchive.ContainsFile(filePath))
            {
                problemLines.Add($"{Path.GetFileName(filePath)}: it is already in the archive.");
                continue;
            }
            if (IsInTrashFolder(filePath))
            {
                problemLines.Add($"{Path.GetFileName(filePath)}: it is in the trash. Restore it first.");
                continue;
            }

            bool isOpen = IsOpenDocument(filePath);
            if (isOpen)
            {
                // Stop watching it, so moving it is not taken for a change by another program.
                WatchFile(null);
            }

            try
            {
                var shelfNames = FindNormalShelvesWithFile(filePath).Select(bookshelf => bookshelf.Name).ToList();
                string archivedFilePath = documentArchive.ArchiveFile(filePath, ChooseArchiveSubfolderName(filePath), shelfNames);
                TakeFileOffNormalShelves(filePath);
                archivedFileCount++;
                if (isOpen)
                {
                    FollowMovedOpenDocument(archivedFilePath);
                }
            }
            catch (Exception archiveError)
            {
                if (isOpen)
                {
                    WatchFile(currentFilePath);
                }
                problemLines.Add($"{Path.GetFileName(filePath)}: {archiveError.Message}");
            }
        }

        RefreshAfterTrashOrArchiveChange();
        if (archivedFileCount > 0)
        {
            DocumentInfoStatusText.Text = archivedFileCount == 1
                ? "Archived 1 file (see the Archive bookshelf)"
                : $"Archived {archivedFileCount} files (see the Archive bookshelf)";
        }
        ShowProblems("These could not be archived:", problemLines);
    }

    /// <summary>
    /// The bookshelf whose name the archived file's subfolder gets: the shown shelf if the file is on it, else the
    /// first shelf it is on, else "Other".
    /// </summary>
    private string ChooseArchiveSubfolderName(string filePath)
    {
        if (!currentBookshelf.IsBuiltInShelf && currentBookshelf.ContainsFile(filePath))
        {
            return currentBookshelf.Name;
        }
        return appSettings.Bookshelves.FirstOrDefault(bookshelf => !bookshelf.IsBuiltInShelf && bookshelf.ContainsFile(filePath))?.Name ?? "Other";
    }

    /// <summary>
    /// Archive shelf → Restore: moves archived files back to where they were and onto their shelves. A file whose
    /// origin is not known (e.g. it was copied into the Archive folder by hand) asks for a folder.
    /// </summary>
    private void RestoreFromArchive(IReadOnlyCollection<BookshelfEntry> archivedEntries)
    {
        var problemLines = new List<string>();
        var restoredDescriptions = new List<string>();
        foreach (var archivedEntry in archivedEntries.Where(bookshelfEntry => !bookshelfEntry.HasNoFile))
        {
            string archivedFilePath = archivedEntry.FilePath;
            if (string.Equals(Path.GetFullPath(archivedFilePath), Path.GetFullPath(documentArchive.TodoArchiveFilePath), StringComparison.OrdinalIgnoreCase))
            {
                problemLines.Add($"{DocumentArchive.TodoArchiveFileName}: it holds the archived tasks of the todo list and stays in the archive.");
                continue;
            }

            var archiveRecord = documentArchive.FindRecord(archivedFilePath);
            string? wantedFilePath = archiveRecord?.OriginalPath ?? AskWhereToRestore(archivedFilePath);
            string? targetFilePath = wantedFilePath == null ? null : ChooseRestoreTarget(wantedFilePath);
            if (targetFilePath == null)
            {
                continue;
            }

            bool isOpen = IsOpenDocument(archivedFilePath);
            if (isOpen)
            {
                WatchFile(null);
            }
            try
            {
                documentArchive.RestoreFile(archivedFilePath, targetFilePath);
                if (isOpen)
                {
                    FollowMovedOpenDocument(targetFilePath);
                }

                var shelfNames = PutFileBackOnShelves(targetFilePath, archiveRecord?.ShelfNames ?? new List<string>());
                string shelvesText = shelfNames.Count > 0 ? $" (on {string.Join(", ", shelfNames)})" : string.Empty;
                restoredDescriptions.Add($"Restored {Path.GetFileName(targetFilePath)} to {Path.GetDirectoryName(targetFilePath)}{shelvesText}");
            }
            catch (Exception restoreError)
            {
                if (isOpen)
                {
                    WatchFile(currentFilePath);
                }
                problemLines.Add($"{Path.GetFileName(archivedFilePath)}: {restoreError.Message}");
            }
        }

        RefreshAfterTrashOrArchiveChange();
        if (restoredDescriptions.Count > 0)
        {
            DocumentInfoStatusText.Text = restoredDescriptions.Count == 1 ? restoredDescriptions[0] : $"Restored {restoredDescriptions.Count} files from the archive";
        }
        ShowProblems("These were not restored:", problemLines);
    }

    /// <summary>For an archived file whose origin is unknown: asks which folder to put it in. Null if the user cancelled.</summary>
    private string? AskWhereToRestore(string archivedFilePath)
    {
        var folderDialog = new OpenFolderDialog
        {
            Title = $"Where should {Path.GetFileName(archivedFilePath)} be restored to?",
        };
        return folderDialog.ShowDialog(this) == true
            ? Path.Combine(folderDialog.FolderName, Path.GetFileName(archivedFilePath))
            : null;
    }

    /// <summary>
    /// Todo → Archive done tasks: moves the ticked tasks of todo.md (with their sub-tasks and notes) into
    /// Archive\todo-archive.md, newest first under a dated heading. If the todo list is open, its unsaved edits are kept.
    /// </summary>
    private void ArchiveDoneTodoTasks()
    {
        string todoText;
        bool isTodoListOpen = IsTodoListOpen;
        try
        {
            todoText = isTodoListOpen ? MarkdownEditor.Text : File.Exists(TodoFilePath) ? File.ReadAllText(TodoFilePath) : string.Empty;
        }
        catch (Exception readError)
        {
            MessageBox.Show(this, $"Could not read the todo list:\n{TodoFilePath}\n\n{readError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (remainingTodoText, taskGroups, taskCount) = TodoDocument.TakeOutCompletedTasks(todoText);
        if (taskCount == 0)
        {
            MessageBox.Show(this, $"There are no ticked tasks in {TodoDocument.FileName} to archive.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Write the archive first: if the todo list then cannot be saved, the tasks are in both places but never lost.
        string archiveFilePath = documentArchive.TodoArchiveFilePath;
        try
        {
            string archiveText = File.Exists(archiveFilePath) ? File.ReadAllText(archiveFilePath) : string.Empty;
            Directory.CreateDirectory(documentArchive.ArchiveFolderPath);
            File.WriteAllText(archiveFilePath, TodoDocument.AddToArchive(archiveText, taskGroups, DateTime.Now), SaveEncoding);
        }
        catch (Exception writeError)
        {
            MessageBox.Show(this, $"Could not write the archive:\n{archiveFilePath}\n\n{writeError.Message}\n\nThe todo list was not changed.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (isTodoListOpen)
        {
            // Work on the editor text so any unsaved edits in the todo list are kept, then save (SaveToFile reports errors).
            ReplaceEditorTextKeepingCaret(remainingTodoText);
            previewRefreshTimer.Stop();
            RenderPreview();
            SaveToFile(currentFilePath!);
        }
        else
        {
            try
            {
                File.WriteAllText(TodoFilePath, remainingTodoText, SaveEncoding);
            }
            catch (Exception writeError)
            {
                MessageBox.Show(this,
                    $"The tasks were copied to the archive, but the todo list could not be saved:\n{TodoFilePath}\n\n{writeError.Message}",
                    "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        RefreshAfterTrashOrArchiveChange();
        DocumentInfoStatusText.Text = taskCount == 1
            ? $"Moved 1 done task to {DocumentArchive.FolderName}\\{DocumentArchive.TodoArchiveFileName}"
            : $"Moved {taskCount} done tasks to {DocumentArchive.FolderName}\\{DocumentArchive.TodoArchiveFileName}";
    }

    // ------------------------------------------------------------------
    // Menus and buttons
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows or hides the trash and archive items of the file list's context menu: Archive for files on any shelf but
    /// the Archive, Restore from archive on the Archive shelf, Move to trash everywhere.
    /// </summary>
    private void UpdateTrashAndArchiveMenuItems(ContextMenu bookshelfContextMenu)
    {
        bool hasSelection = SelectedBookshelfEntries.Count > 0;
        foreach (var menuItem in bookshelfContextMenu.Items.OfType<MenuItem>())
        {
            if (Equals(menuItem.Tag, ArchiveFilesMenuTag))
            {
                menuItem.Visibility = currentBookshelf.IsQueryShelf || currentBookshelf.IsArchiveShelf ? Visibility.Collapsed : Visibility.Visible;
                menuItem.IsEnabled = hasSelection;
            }
            else if (Equals(menuItem.Tag, RestoreFromArchiveMenuTag))
            {
                menuItem.Visibility = currentBookshelf.IsArchiveShelf ? Visibility.Visible : Visibility.Collapsed;
                menuItem.IsEnabled = hasSelection;
            }
        }
    }

    /// <summary>
    /// Adds "Archive" and "Move to trash" to a post-it note's right-click menu.
    /// </summary>
    private void AddArchiveAndTrashMenuItems(ContextMenu noteContextMenu, BookshelfEntry bookshelfEntry)
    {
        var archiveMenuItem = new MenuItem { Header = "Arc_hive", ToolTip = "Move the note to the Archive folder; it can still be searched and restored" };
        archiveMenuItem.Click += (_, _) => ArchiveFiles(new[] { bookshelfEntry.FilePath });

        var trashMenuItem = new MenuItem { Header = "Move to _trash", ToolTip = "Move the note to the Trash; it can be restored from the Trash bookshelf" };
        trashMenuItem.Click += (_, _) => MoveFilesToTrash(new[] { bookshelfEntry.FilePath });

        noteContextMenu.Items.Add(new Separator());
        noteContextMenu.Items.Add(archiveMenuItem);
        noteContextMenu.Items.Add(trashMenuItem);
    }

    /// <summary>File list context menu → Archive: archives the selected files.</summary>
    private void ArchiveBookshelfEntriesMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var selectedFilePaths = SelectedBookshelfEntries.Where(bookshelfEntry => !bookshelfEntry.HasNoFile).Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        if (selectedFilePaths.Count > 0)
        {
            ArchiveFiles(selectedFilePaths);
        }
    }

    /// <summary>File list context menu → Restore from archive (Archive shelf only).</summary>
    private void RestoreFromArchiveMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        RestoreFromArchive(SelectedBookshelfEntries);
    }

    /// <summary>File list context menu → Move to trash (Ctrl+Del): files, or queries on a query shelf.</summary>
    private void MoveBookshelfEntriesToTrashMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        MoveEntriesToTrash(SelectedBookshelfEntries);
    }

    /// <summary>File → Archive this document: archives the open file.</summary>
    private void ArchiveOpenDocumentMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentFilePath == null)
        {
            MessageBox.Show(this, "Only a saved file can be archived.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ArchiveFiles(new[] { currentFilePath });
    }

    /// <summary>File → Move this document to the trash: moves the open file to the trash.</summary>
    private void TrashOpenDocumentMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (currentFilePath == null)
        {
            MessageBox.Show(this, "Only a saved file can be moved to the trash.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsInTrashFolder(currentFilePath))
        {
            MessageBox.Show(this, "This file is already in the trash.", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MoveFilesToTrash(new[] { currentFilePath });
    }

    /// <summary>Shelf menu → Empty the trash.</summary>
    private void EmptyTrashMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        EmptyTrash();
    }

    /// <summary>Todo → Archive done tasks.</summary>
    private void ArchiveDoneTasksMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        ArchiveDoneTodoTasks();
    }
}
