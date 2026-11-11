using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Macros;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// The bookshelf part of the main window: a side panel listing Markdown files from anywhere
/// on disk (stored in the settings file), and a properties panel describing the open document.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width the bookshelf column had before it was hidden, so showing it again restores it.</summary>
    private double bookshelfWidthBeforeHiding = 280;

    /// <summary>Smallest width of the bookshelf column while it is visible.</summary>
    private const double BookshelfMinimumWidth = 150;

    // ------------------------------------------------------------------
    // Showing and hiding the panel
    // ------------------------------------------------------------------

    /// <summary>Sets up the bookshelf panel from the saved settings. Called once from the constructor.</summary>
    private void InitializeBookshelf()
    {
        bookshelfWidthBeforeHiding = Math.Max(appSettings.BookshelfPanelWidth, BookshelfMinimumWidth);
        BookshelfColumn.Width = new GridLength(bookshelfWidthBeforeHiding);

        // The Standard shelf is always the one shown when the app starts.
        currentBookshelf = appSettings.StandardBookshelf;
        RefreshShelfChooser();
        UpdateShelfEditingControls();
        WatchCurrentShelfFolder();

        ApplyAlphabeticalOrderOnFirstRun();
        SetBookshelfVisible(appSettings.IsBookshelfVisible);
        RefreshBookshelfList();
    }

    /// <summary>Shows or hides the bookshelf and properties panel.</summary>
    private void SetBookshelfVisible(bool showBookshelf)
    {
        // Remember the current width before collapsing the column.
        if (!showBookshelf && BookshelfColumn.ActualWidth > 0)
        {
            bookshelfWidthBeforeHiding = BookshelfColumn.ActualWidth;
        }

        BookshelfColumn.MinWidth = showBookshelf ? BookshelfMinimumWidth : 0;
        BookshelfColumn.Width = showBookshelf ? new GridLength(bookshelfWidthBeforeHiding) : new GridLength(0);
        BookshelfPanel.Visibility = showBookshelf ? Visibility.Visible : Visibility.Collapsed;
        BookshelfSplitter.Visibility = showBookshelf ? Visibility.Visible : Visibility.Collapsed;
        BookshelfMenuItem.IsChecked = showBookshelf;
        appSettings.IsBookshelfVisible = showBookshelf;

        if (showBookshelf)
        {
            UpdatePropertiesPanel();
        }
    }

    /// <summary>Stores the panel width and visibility so the next start looks the same. Errors are ignored.</summary>
    private void SaveBookshelfLayout()
    {
        if (appSettings.IsBookshelfVisible && BookshelfColumn.ActualWidth > 0)
        {
            appSettings.BookshelfPanelWidth = BookshelfColumn.ActualWidth;
        }
        else
        {
            appSettings.BookshelfPanelWidth = bookshelfWidthBeforeHiding;
        }

        try
        {
            appSettings.Save();
        }
        catch (Exception)
        {
            // Losing the panel width is not worth bothering the user about while the app closes.
        }
    }

    // ------------------------------------------------------------------
    // The list of files
    // ------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the list from the settings, in the user's own order, with missing files re-checked.
    /// Keeps the open file selected.
    /// </summary>
    private void RefreshBookshelfList()
    {
        // A folder shelf's files are read from its folder, and the Trash's from the Trash folder.
        RescanCurrentFolderShelf();
        if (currentBookshelf.IsTrashShelf)
        {
            ReloadTrashShelf();
        }

        foreach (var bookshelfEntry in currentBookshelf.Entries)
        {
            bookshelfEntry.RefreshFileExists();
        }

        ShowBookshelfEntries();
    }

    /// <summary>Puts the shown shelf's entries in the list (without re-checking the disk) and keeps the open file selected.</summary>
    private void ShowBookshelfEntries()
    {
        // A copy, so the list control is not tied to the settings list while it is being reordered.
        BookshelfList.ItemsSource = currentBookshelf.Entries.ToList();

        BookshelfEmptyHint.Visibility = currentBookshelf.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectOpenFileInBookshelf();
    }

    /// <summary>Highlights the open file in the bookshelf list, if it is on the shelf.</summary>
    private void SelectOpenFileInBookshelf()
    {
        // A query entry is found by its made-up path, just like a file.
        string? openEntryPath = currentFilePath ?? currentQueryEntry?.FilePath;
        var openFileEntry = openEntryPath == null
            ? null
            : BookshelfList.Items.OfType<BookshelfEntry>().FirstOrDefault(bookshelfEntry => bookshelfEntry.HasPath(openEntryPath));

        BookshelfList.SelectedItem = openFileEntry;
        if (openFileEntry != null)
        {
            BookshelfList.ScrollIntoView(openFileEntry);
        }
    }

    /// <summary>True if the given file is already on the shelf that is shown.</summary>
    private bool IsOnBookshelf(string filePath)
    {
        return currentBookshelf.ContainsFile(filePath);
    }

    /// <summary>
    /// Puts files on the bookshelf (files already on it are skipped), saves the settings
    /// and refreshes the list. Returns how many files were added.
    /// </summary>
    private int AddFilesToBookshelf(IEnumerable<string> filePaths)
    {
        if (RejectChangeToFolderShelf() || RejectFilesOnQueryShelf())
        {
            return 0;
        }

        int addedFileCount = 0;
        foreach (string filePath in filePaths)
        {
            string fullPath = Path.GetFullPath(filePath);
            if (IsOnBookshelf(fullPath))
            {
                continue;
            }

            currentBookshelf.Entries.Add(new BookshelfEntry { FilePath = fullPath, AddedOn = DateTime.Now });
            addedFileCount++;
        }

        if (addedFileCount > 0)
        {
            SaveSettingsOrWarn();
            RefreshBookshelfList();
            UpdatePropertiesPanel();
        }
        return addedFileCount;
    }

    /// <summary>
    /// Takes files off the bookshelf. The files on disk are not touched. On a query shelf the queries go to the trash
    /// (they exist nowhere else), and in the Trash "remove" means delete for good (after asking).
    /// </summary>
    private void RemoveFromBookshelf(IReadOnlyCollection<BookshelfEntry> entriesToRemove)
    {
        if (entriesToRemove.Count == 0)
        {
            return;
        }
        if (currentBookshelf.IsTrashShelf)
        {
            DeleteTrashEntriesForGood(entriesToRemove);
            return;
        }
        if (currentBookshelf.IsQueryShelf)
        {
            MoveEntriesToTrash(entriesToRemove);
            return;
        }
        if (RejectChangeToFolderShelf())
        {
            return;
        }

        foreach (var entryToRemove in entriesToRemove)
        {
            currentBookshelf.Entries.Remove(entryToRemove);
        }

        SaveSettingsOrWarn();
        RefreshBookshelfList();
        UpdatePropertiesPanel();
    }

    /// <summary>Saves the settings file; tells the user (without failing) if that is not possible.</summary>
    private void SaveSettingsOrWarn()
    {
        try
        {
            appSettings.Save();
        }
        catch (Exception saveError)
        {
            MessageBox.Show(this, $"The bookshelf could not be saved:\n{AppSettings.SettingsFilePath}\n\n{saveError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Zip all (backup)
    // ------------------------------------------------------------------

    /// <summary>
    /// File → Zip all: writes every bookshelf file plus the settings into a timestamped zip next to the exe,
    /// so the whole setup can be moved to another computer by unzipping it there.
    /// If the exe's folder cannot be written to (e.g. under Program Files), asks where to save the zip instead.
    /// </summary>
    private async void ZipAllMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        DateTime backupTime = DateTime.Now;
        string backupFileName = BookshelfBackup.CreateBackupFileName(backupTime);

        // Environment.ProcessPath is the real .exe even for a single-file app; fall back to the base directory just in case.
        string exeFolderPath = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string zipFilePath = Path.Combine(exeFolderPath, backupFileName);

        // Take a snapshot of what to back up on the UI thread; the zipping itself runs in the background.
        // Folder shelves that are not shown may not have been read for a while, so read them now.
        foreach (var folderShelf in appSettings.Bookshelves.Where(bookshelf => bookshelf.IsFolderShelf))
        {
            folderShelf.RescanFolder();
        }

        // Files from every shelf; a file that is on several shelves goes into the zip once.
        // Queries have no file: they are saved in settings.json (with their connection strings encrypted).
        List<string> bookshelfFilePaths = appSettings.Bookshelves
            .SelectMany(bookshelf => bookshelf.Entries)
            .Where(bookshelfEntry => !bookshelfEntry.IsQuery)
            .Select(bookshelfEntry => bookshelfEntry.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // The todo list lives next to the exe rather than on the bookshelf, so add it explicitly (if it exists yet).
        if (File.Exists(TodoFilePath) && !bookshelfFilePaths.Contains(TodoFilePath, StringComparer.OrdinalIgnoreCase))
        {
            bookshelfFilePaths.Add(TodoFilePath);
        }
        // The same goes for the journal.
        if (File.Exists(JournalFilePath) && !bookshelfFilePaths.Contains(JournalFilePath, StringComparer.OrdinalIgnoreCase))
        {
            bookshelfFilePaths.Add(JournalFilePath);
        }
        string settingsJson = appSettings.ToJson();

        BookshelfBackup.BackupResult? backupResult = await TryCreateBackupAsync(bookshelfFilePaths, settingsJson, zipFilePath, backupTime, reportAccessDenied: false);
        if (backupResult == null)
        {
            // No write access next to the exe: let the user pick another place.
            var saveDialog = new SaveFileDialog
            {
                Title = "Save backup zip",
                FileName = backupFileName,
                Filter = "Zip files (*.zip)|*.zip",
                DefaultExt = ".zip",
            };
            if (saveDialog.ShowDialog(this) != true)
            {
                return;
            }

            backupResult = await TryCreateBackupAsync(bookshelfFilePaths, settingsJson, saveDialog.FileName, backupTime, reportAccessDenied: true);
            if (backupResult == null)
            {
                return;
            }
        }

        ShowBackupSummary(backupResult);
    }

    /// <summary>
    /// Runs the backup on a background thread with a wait cursor, so a slow network share does not freeze the window.
    /// Returns null if the zip could not be written: an "access denied" error is reported only when
    /// <paramref name="reportAccessDenied"/> is true (otherwise the caller offers another location); other errors are always reported.
    /// </summary>
    private async Task<BookshelfBackup.BackupResult?> TryCreateBackupAsync(
        List<string> bookshelfFilePaths, string settingsJson, string zipFilePath, DateTime backupTime, bool reportAccessDenied)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            return await Task.Run(() => BookshelfBackup.CreateBackup(bookshelfFilePaths, settingsJson, zipFilePath, backupTime));
        }
        catch (UnauthorizedAccessException) when (!reportAccessDenied)
        {
            return null;
        }
        catch (Exception backupError)
        {
            MessageBox.Show(this, $"The backup could not be written:\n{zipFilePath}\n\n{backupError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>Tells the user where the zip is and what was skipped, and offers to show it in Explorer.</summary>
    private void ShowBackupSummary(BookshelfBackup.BackupResult backupResult)
    {
        string summaryText = $"Backed up {backupResult.BackedUpFileCount} file(s) and the settings to:\n{backupResult.ZipFilePath}";
        if (backupResult.SkippedFiles.Count > 0)
        {
            // Show at most a handful here; the full list is in backup-contents.txt inside the zip.
            const int maximumSkippedFilesShown = 10;
            summaryText += $"\n\n{backupResult.SkippedFiles.Count} file(s) could not be included:\n"
                + string.Join("\n", backupResult.SkippedFiles.Take(maximumSkippedFilesShown));
            if (backupResult.SkippedFiles.Count > maximumSkippedFilesShown)
            {
                summaryText += "\n...";
            }
        }

        summaryText += "\n\nShow the zip in its folder?";
        var userAnswer = MessageBox.Show(this, summaryText, "Markdown Viewer",
            MessageBoxButton.YesNo, backupResult.SkippedFiles.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        if (userAnswer == MessageBoxResult.Yes)
        {
            // Opens Explorer with the zip selected.
            Process.Start("explorer.exe", $"/select,\"{backupResult.ZipFilePath}\"");
        }
    }

    /// <summary>The bookshelf entries that are selected in the list.</summary>
    private List<BookshelfEntry> SelectedBookshelfEntries => BookshelfList.SelectedItems.OfType<BookshelfEntry>().ToList();

    /// <summary>
    /// Opens a bookshelf file in the viewer. If the file is missing, offers to take it off the shelf.
    /// </summary>
    private void OpenBookshelfEntry(BookshelfEntry bookshelfEntry)
    {
        // A trashed query or bookshelf has nothing to show: it has to be restored first.
        if (bookshelfEntry.TrashItem != null && bookshelfEntry.HasNoFile)
        {
            MessageBox.Show(this, $"\"{bookshelfEntry.TrashItem.DisplayName}\" is in the trash. Restore it to use it again.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A query is run rather than read from disk.
        if (bookshelfEntry.IsQuery)
        {
            OpenQueryEntry(bookshelfEntry);
            return;
        }

        // Clicking the file that is already open does not read it again; on a post-it board it just brings the document back into view.
        if (currentFilePath != null && bookshelfEntry.HasPath(currentFilePath))
        {
            HidePostItBoard();
            return;
        }

        // A trashed file that is gone (e.g. deleted in Explorer) simply drops out of the Trash list.
        if (!File.Exists(bookshelfEntry.FilePath) && currentBookshelf.IsTrashShelf)
        {
            RefreshBookshelfList();
            return;
        }

        if (!File.Exists(bookshelfEntry.FilePath))
        {
            var userAnswer = MessageBox.Show(this,
                $"The file was not found. It may have been moved, renamed or deleted:\n{bookshelfEntry.FilePath}\n\nRemove it from the bookshelf?",
                "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (userAnswer == MessageBoxResult.Yes)
            {
                RemoveFromBookshelf(new[] { bookshelfEntry });
            }
            else
            {
                RefreshBookshelfList();
            }
            return;
        }

        if (ConfirmDiscardUnsavedChanges())
        {
            LoadFile(bookshelfEntry.FilePath);
        }
        else
        {
            // The user cancelled: keep the highlight on the file that is still open.
            SelectOpenFileInBookshelf();
        }
    }

    /// <summary>Opens Explorer with the given file selected.</summary>
    private void ShowFileInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                // "/select," opens the folder and highlights the file.
                Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            else
            {
                string? folderPath = Path.GetDirectoryName(filePath);
                if (folderPath == null || !Directory.Exists(folderPath))
                {
                    MessageBox.Show(this, $"Neither the file nor its folder exists any more:\n{filePath}",
                        "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(folderPath) { UseShellExecute = true });
            }
        }
        catch (Exception openError)
        {
            MessageBox.Show(this, $"Could not open the folder:\n{filePath}\n\n{openError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Expands dropped paths into Markdown files: files are taken as they are (if they are Markdown),
    /// folders contribute the Markdown files directly inside them (not in subfolders).
    /// </summary>
    private static List<string> CollectMarkdownFiles(IEnumerable<string> droppedPaths)
    {
        var markdownFilePaths = new List<string>();
        foreach (string droppedPath in droppedPaths)
        {
            if (Directory.Exists(droppedPath))
            {
                try
                {
                    markdownFilePaths.AddRange(Directory.EnumerateFiles(droppedPath).Where(IsMarkdownFile));
                }
                catch (Exception)
                {
                    // A folder we may not read is simply skipped.
                }
            }
            else if (File.Exists(droppedPath) && IsMarkdownFile(droppedPath))
            {
                markdownFilePaths.Add(droppedPath);
            }
        }
        return markdownFilePaths;
    }

    /// <summary>True if a drag-and-drop happened over the bookshelf panel (rather than over the document).</summary>
    private bool IsOverBookshelfPanel(DragEventArgs dragDetails)
    {
        return BookshelfPanel.IsVisible
            && dragDetails.OriginalSource is DependencyObject dropTarget
            && dropTarget is Visual
            && BookshelfPanel.IsAncestorOf(dropTarget);
    }

    // ------------------------------------------------------------------
    // Properties panel
    // ------------------------------------------------------------------

    /// <summary>
    /// Fills the properties panel for the open document: file information (location, size, dates)
    /// and text statistics (characters, words, headings, reading time...). Called after every render,
    /// so the numbers follow along while editing.
    /// </summary>
    private void UpdatePropertiesPanel()
    {
        // Nothing to do while the panel is hidden; it is refreshed when shown again.
        if (!appSettings.IsBookshelfVisible)
        {
            return;
        }

        PropertiesGrid.Children.Clear();
        PropertiesGrid.RowDefinitions.Clear();

        string documentText = MarkdownEditor.Text;
        var documentStatistics = DocumentStatistics.Calculate(documentText);

        // The button under the bookshelf shows the file in Explorer, or for a query opens its settings page.
        ShowInFolderButton.Content = currentQueryEntry != null ? "Edit query..." : "Show in folder";
        ShowInFolderButton.ToolTip = currentQueryEntry != null ? "Change the SQL, the connection or how the result is shown (Ctrl+E)" : "Open the folder of this file in Explorer";

        // --- File (or Query) ---
        if (currentQueryEntry?.Query != null)
        {
            ShowInFolderButton.IsEnabled = true;
            AddQueryProperties(currentQueryEntry);
        }
        else if (currentFilePath == null)
        {
            AddPropertySectionHeader("File");
            AddPropertyRow("Location", "Not saved yet");
            ShowInFolderButton.IsEnabled = false;
        }
        else
        {
            AddPropertySectionHeader("File");
            ShowInFolderButton.IsEnabled = true;
            AddPropertyRow("Name", Path.GetFileName(currentFilePath));
            AddPropertyRow("Folder", Path.GetDirectoryName(currentFilePath) ?? string.Empty);

            var fileDetails = new FileInfo(currentFilePath);
            if (fileDetails.Exists)
            {
                AddPropertyRow("Size", FormatFileSize(fileDetails.Length));
                AddPropertyRow("Created", FormatDate(fileDetails.CreationTime));
                AddPropertyRow("Modified", FormatDate(fileDetails.LastWriteTime));
                AddPropertyRow("Read-only", fileDetails.IsReadOnly ? "Yes" : "No");
            }
            else
            {
                AddPropertyRow("Status", "File not found on disk");
            }

            // Every shelf the file is on, e.g. "Standard, Documentation".
            var shelvesWithFile = appSettings.Bookshelves
                .Where(bookshelf => bookshelf.ContainsFile(currentFilePath))
                .Select(bookshelf => bookshelf.Name)
                .ToList();
            AddPropertyRow("Bookshelves", shelvesWithFile.Count > 0
                ? string.Join(", ", shelvesWithFile)
                : "None (Ctrl+D adds it to this shelf)");
        }
        if (HasUnsavedChanges)
        {
            AddPropertyRow("Changes", "Unsaved edits (counts below include them)");
        }

        // --- Text ---
        AddPropertySectionHeader("Text");
        if (documentStatistics.Title != null)
        {
            AddPropertyRow("Title", documentStatistics.Title);
        }
        AddPropertyRow("Characters", documentStatistics.CharacterCount.ToString("N0"));
        AddPropertyRow("Without spaces", documentStatistics.CharacterCountWithoutSpaces.ToString("N0"));
        AddPropertyRow("Words", documentStatistics.WordCount.ToString("N0"));
        AddPropertyRow("Lines", $"{documentStatistics.LineCount:N0} ({documentStatistics.BlankLineCount:N0} blank)");
        AddPropertyRow("Paragraphs", documentStatistics.ParagraphCount.ToString("N0"));
        AddPropertyRow("Reading time", FormatReadingTime(documentStatistics.EstimatedReadingTime));
        AddPropertyRow("Line endings", documentStatistics.LineEndingStyle);

        // --- Structure ---
        AddPropertySectionHeader("Structure");
        AddPropertyRow("Headings", FormatHeadingCounts(documentStatistics));
        AddPropertyRow("Links", documentStatistics.LinkCount.ToString("N0"));
        AddPropertyRow("Images", documentStatistics.ImageCount.ToString("N0"));
        AddPropertyRow("Code blocks", documentStatistics.CodeBlockCount.ToString("N0"));
        AddPropertyRow("Tables", documentStatistics.TableCount.ToString("N0"));
        if (documentStatistics.TaskCount > 0)
        {
            AddPropertyRow("Tasks", $"{documentStatistics.CompletedTaskCount:N0} of {documentStatistics.TaskCount:N0} done");
        }
    }

    /// <summary>Adds a bold section title ("File", "Text", ...) spanning both columns of the properties grid.</summary>
    private void AddPropertySectionHeader(string sectionTitle)
    {
        int rowIndex = PropertiesGrid.RowDefinitions.Count;
        PropertiesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headerText = new TextBlock
        {
            Text = sectionTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
            // Extra space above every section except the first.
            Margin = new Thickness(0, rowIndex == 0 ? 2 : 10, 0, 2),
        };
        Grid.SetRow(headerText, rowIndex);
        Grid.SetColumnSpan(headerText, 2);
        PropertiesGrid.Children.Add(headerText);
    }

    /// <summary>
    /// Adds one "label: value" row to the properties grid. The value is a read-only text box,
    /// so it can be selected and copied (handy for the folder path).
    /// </summary>
    private void AddPropertyRow(string propertyLabel, string propertyValue)
    {
        int rowIndex = PropertiesGrid.RowDefinitions.Count;
        PropertiesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var labelText = new TextBlock
        {
            Text = propertyLabel,
            Foreground = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)),
            Margin = new Thickness(0, 1, 10, 1),
        };
        Grid.SetRow(labelText, rowIndex);
        Grid.SetColumn(labelText, 0);

        var valueText = new TextBox
        {
            Text = propertyValue,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1, 0, 1),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetRow(valueText, rowIndex);
        Grid.SetColumn(valueText, 1);

        PropertiesGrid.Children.Add(labelText);
        PropertiesGrid.Children.Add(valueText);
    }

    /// <summary>Formats a byte count as e.g. "12.3 KB (12,595 bytes)".</summary>
    private static string FormatFileSize(long sizeInBytes)
    {
        string[] unitNames = { "bytes", "KB", "MB", "GB" };
        double scaledSize = sizeInBytes;
        int unitIndex = 0;
        while (scaledSize >= 1024 && unitIndex < unitNames.Length - 1)
        {
            scaledSize /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{sizeInBytes:N0} bytes"
            : $"{scaledSize:0.#} {unitNames[unitIndex]} ({sizeInBytes:N0} bytes)";
    }

    /// <summary>Formats a date as "2026-11-06 09:15" (ISO style, unambiguous in every language).</summary>
    private static string FormatDate(DateTime dateTime) => dateTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Formats the reading time estimate as "less than a minute", "about 4 min" or "about 1 h 20 min".</summary>
    private static string FormatReadingTime(TimeSpan readingTime)
    {
        int totalMinutes = (int)Math.Round(readingTime.TotalMinutes);
        if (totalMinutes < 1)
        {
            return "Less than a minute";
        }
        if (totalMinutes < 60)
        {
            return $"About {totalMinutes} min";
        }
        return $"About {totalMinutes / 60} h {totalMinutes % 60} min";
    }

    /// <summary>Formats the heading counts as e.g. "7 (H1: 1, H2: 4, H3: 2)".</summary>
    private static string FormatHeadingCounts(DocumentStatistics documentStatistics)
    {
        if (documentStatistics.HeadingCount == 0)
        {
            return "0";
        }

        var levelSummaries = documentStatistics.HeadingCountsByLevel
            .Select((headingCount, levelIndex) => (HeadingCount: headingCount, Level: levelIndex + 1))
            .Where(levelCount => levelCount.HeadingCount > 0)
            .Select(levelCount => $"H{levelCount.Level}: {levelCount.HeadingCount}");

        return $"{documentStatistics.HeadingCount} ({string.Join(", ", levelSummaries)})";
    }

    // ------------------------------------------------------------------
    // Event handlers
    // ------------------------------------------------------------------

    /// <summary>View → Bookshelf and properties (Ctrl+B).</summary>
    private void ToggleBookshelfCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        SetBookshelfVisible(!appSettings.IsBookshelfVisible);
    }

    /// <summary>File → Add to bookshelf (Ctrl+D) is available when a saved file that is not on the shelf yet is open.</summary>
    private void AddToBookshelfCommand_CanExecute(object sender, CanExecuteRoutedEventArgs canExecuteDetails)
    {
        // Files cannot be added by hand to a shelf that shows a folder (or the Archive), the Trash, nor to a shelf of queries.
        canExecuteDetails.CanExecute = currentFilePath != null && currentBookshelf.CanEditEntriesByHand && !currentBookshelf.IsQueryShelf && !IsOnBookshelf(currentFilePath);
    }

    /// <summary>File → Add to bookshelf (Ctrl+D): puts the open file on the shelf and shows the panel.</summary>
    private void AddToBookshelfCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (currentFilePath == null)
        {
            return;
        }

        AddFilesToBookshelf(new[] { currentFilePath });
        if (!appSettings.IsBookshelfVisible)
        {
            SetBookshelfVisible(true);
        }
    }

    /// <summary>"Add..." button and File menu: picks one or more Markdown files from anywhere and puts them on the shelf.</summary>
    private void AddFilesToBookshelfButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // In the Trash and the Archive the button reads "Restore" and puts the selected items back.
        if (currentBookshelf.IsTrashShelf)
        {
            RestoreTrashEntries(SelectedBookshelfEntries);
            return;
        }
        if (currentBookshelf.IsArchiveShelf)
        {
            RestoreFromArchive(SelectedBookshelfEntries);
            return;
        }

        // On a query shelf the button reads "New..." and adds a query instead.
        if (currentBookshelf.IsQueryShelf)
        {
            AddNewQueryToCurrentShelf();
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Title = "Add files to the bookshelf",
            Filter = MarkdownFileDialogFilter,
            Multiselect = true,
            InitialDirectory = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null,
        };

        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        AddFilesToBookshelf(openDialog.FileNames);
        if (!appSettings.IsBookshelfVisible)
        {
            SetBookshelfVisible(true);
        }
    }

    /// <summary>"Remove" button and context menu: takes the selected files off the shelf.</summary>
    private void RemoveFromBookshelfButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        RemoveFromBookshelf(SelectedBookshelfEntries);
    }

    /// <summary>A single click on a bookshelf file opens it.</summary>
    private void BookshelfList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs clickDetails)
    {
        // Ctrl/Shift-click is for selecting several files (e.g. to remove them), not for opening.
        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        // Find the list item under the mouse; clicks on empty space or the scroll bar are ignored.
        if (clickDetails.OriginalSource is DependencyObject clickedElement
            && ItemsControl.ContainerFromElement(BookshelfList, clickedElement) is ListBoxItem clickedListItem
            && clickedListItem.DataContext is BookshelfEntry clickedEntry)
        {
            OpenBookshelfEntry(clickedEntry);
        }
    }

    /// <summary>
    /// Enter opens the selected file, Delete takes the selected files off the shelf, Ctrl+Delete moves them to
    /// the trash, Alt+Up / Alt+Down move the selected file up or down the list.
    /// </summary>
    private void BookshelfList_KeyDown(object sender, KeyEventArgs keyDetails)
    {
        // With Alt held, WPF reports the key as Key.System and the real key in SystemKey.
        if (keyDetails.Key == Key.System && keyDetails.SystemKey is Key.Up or Key.Down)
        {
            MoveSelectedBookshelfEntryByOneStep(keyDetails.SystemKey == Key.Up ? -1 : +1);
            keyDetails.Handled = true;
            return;
        }

        if (keyDetails.Key == Key.Enter && BookshelfList.SelectedItem is BookshelfEntry selectedEntry)
        {
            OpenBookshelfEntry(selectedEntry);
            keyDetails.Handled = true;
        }
        else if (keyDetails.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.Control && !currentBookshelf.IsTrashShelf)
        {
            MoveEntriesToTrash(SelectedBookshelfEntries);
            keyDetails.Handled = true;
        }
        else if (keyDetails.Key == Key.Delete)
        {
            RemoveFromBookshelf(SelectedBookshelfEntries);
            keyDetails.Handled = true;
        }
    }

    /// <summary>Context menu → Open.</summary>
    private void OpenBookshelfEntryMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (BookshelfList.SelectedItem is BookshelfEntry selectedEntry)
        {
            OpenBookshelfEntry(selectedEntry);
        }
    }

    /// <summary>Context menu → Show in folder.</summary>
    private void ShowBookshelfEntryInFolderMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (BookshelfList.SelectedItem is BookshelfEntry selectedEntry)
        {
            ShowFileInExplorer(selectedEntry.FilePath);
        }
    }

    /// <summary>Context menu → Copy path: puts the full paths of the selected files on the clipboard, one per line.</summary>
    private void CopyBookshelfEntryPathMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var selectedPaths = SelectedBookshelfEntries.Select(bookshelfEntry => bookshelfEntry.FilePath).ToList();
        if (selectedPaths.Count == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, selectedPaths));
        }
        catch (Exception)
        {
            // Another program may be holding the clipboard; nothing useful to do about it.
        }
    }

    /// <summary>"Show in folder" button in the properties panel.</summary>
    private void ShowCurrentFileInFolderButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // For a query result the button is "Edit query...".
        if (currentQueryEntry != null)
        {
            EditQuery(currentQueryEntry);
        }
        else if (currentFilePath != null)
        {
            ShowFileInExplorer(currentFilePath);
        }
    }

    /// <summary>
    /// When the user comes back to the window, files may have been moved or edited elsewhere,
    /// so the "file not found" marks and the file properties are refreshed.
    /// </summary>
    private void Window_Activated(object? sender, EventArgs activationDetails)
    {
        if (!appSettings.IsBookshelfVisible)
        {
            return;
        }

        // A folder shelf is re-read, in case the folder watcher missed something (e.g. on a network drive).
        if (currentBookshelf.IsFolderShelf)
        {
            RefreshFolderShelfAfterChange();
            return;
        }

        // Only rebuild the list when a file appeared or disappeared, so the list does not jump around.
        bool anyFileExistenceChanged = currentBookshelf.Entries
            .Any(bookshelfEntry => bookshelfEntry.HasFileExistenceChanged);
        if (anyFileExistenceChanged)
        {
            RefreshBookshelfList();
        }
        UpdatePropertiesPanel();
    }
}
