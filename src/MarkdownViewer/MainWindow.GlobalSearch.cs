using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Queries;
using MarkdownViewer.Search;

namespace MarkdownViewer;

/// <summary>
/// Global search (Search → Search all documents, Ctrl+Shift+F): searches every file on every bookshelf, the todo list
/// and every saved query at once, using the lookup file search-index.json next to the exe (see <see cref="SearchIndex"/>).
/// Also holds the version number shown in the title bar.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// The app's version as shown in the title bar, e.g. "v1.0.42". The release build gets its number from the
    /// build pipeline (-p:Version=1.0.run-number); a local build shows v1.0.0.
    /// </summary>
    private static readonly string AppVersionText = BuildAppVersionText();

    /// <summary>The lookup file for global search; loaded and brought up to date in the background at startup.</summary>
    private SearchIndex searchIndex = null!;

    /// <summary>The search window while it is open (it stays open while results are opened), otherwise null.</summary>
    private GlobalSearchWindow? globalSearchWindow;

    /// <summary>Number of index refreshes that are running or waiting, so the search window can say "checking for changes".</summary>
    private int runningSearchIndexRefreshCount;

    /// <summary>Works out <see cref="AppVersionText"/> from the exe's version (major.minor.build; the fourth part is always 0).</summary>
    private static string BuildAppVersionText()
    {
        Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
        return assemblyVersion == null ? string.Empty : $"v{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
    }

    // ------------------------------------------------------------------
    // Setting up and keeping the index up to date
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads search-index.json and then checks it against the bookshelves, both in the background, so startup is
    /// not slowed down. Called once from the constructor, after the bookshelves are set up.
    /// </summary>
    private void InitializeGlobalSearch()
    {
        searchIndex = new SearchIndex(SearchIndex.ChooseIndexFilePath());

        // The index reports changes from background threads; the search window must be updated on the UI thread.
        searchIndex.IndexChanged += (_, _) => Dispatcher.BeginInvoke(() => globalSearchWindow?.RefreshResults());

        _ = LoadAndRefreshSearchIndexAsync();
    }

    /// <summary>Reads the stored lookup file, then brings it up to date (only changed files are read).</summary>
    private async Task LoadAndRefreshSearchIndexAsync()
    {
        await searchIndex.LoadAsync();
        await RefreshSearchIndexAsync(forceRereadAllFiles: false);
    }

    /// <summary>
    /// Brings the index in line with the bookshelves as they are now. Normally only files whose modified time or
    /// size changed are read; <paramref name="forceRereadAllFiles"/> reads every file again (Refresh index).
    /// </summary>
    private async Task RefreshSearchIndexAsync(bool forceRereadAllFiles)
    {
        // What to index is worked out here on the UI thread, because the bookshelves belong to it.
        var (listedSources, folderSources) = CollectSearchIndexSources();

        runningSearchIndexRefreshCount++;
        globalSearchWindow?.SetIndexRefreshing(true);
        try
        {
            await searchIndex.RefreshAsync(listedSources, folderSources, forceRereadAllFiles);
        }
        catch (Exception)
        {
            // The index is a convenience: a failed refresh leaves the previous index in place.
        }
        finally
        {
            runningSearchIndexRefreshCount--;
            globalSearchWindow?.SetIndexRefreshing(runningSearchIndexRefreshCount > 0);
        }
    }

    /// <summary>
    /// Everything the index should contain: the files on normal bookshelves, the queries on query bookshelves,
    /// todo.md, and the folder bookshelves (whose files are listed in the background).
    /// </summary>
    private (List<SearchIndexSource> ListedSources, List<SearchIndexFolderSource> FolderSources) CollectSearchIndexSources()
    {
        var listedSources = new List<SearchIndexSource>();
        var folderSources = new List<SearchIndexFolderSource>();
        foreach (var bookshelf in appSettings.Bookshelves)
        {
            if (bookshelf.IsFolderShelf)
            {
                folderSources.Add(new SearchIndexFolderSource(bookshelf.Name, bookshelf.FolderPath!, bookshelf.IncludeSubfolders));
                continue;
            }

            foreach (var bookshelfEntry in bookshelf.Entries)
            {
                if (bookshelfEntry.Query is SavedQuery savedQuery)
                {
                    listedSources.Add(new SearchIndexSource(bookshelfEntry.FilePath, SearchDocumentKind.Query, savedQuery.Name,
                        new List<string> { bookshelf.Name }, BuildQueryDefinitionText(savedQuery)));
                }
                else
                {
                    listedSources.Add(new SearchIndexSource(bookshelfEntry.FilePath, SearchDocumentKind.File, bookshelfEntry.FileName,
                        new List<string> { bookshelf.Name }));
                }
            }
        }

        // The todo list is searched too, even though it is on no bookshelf.
        listedSources.Add(new SearchIndexSource(TodoFilePath, SearchDocumentKind.File, Path.GetFileName(TodoFilePath),
            new List<string> { SearchIndex.TodoListShelfName }));
        // The journal too, which also lives next to the exe.
        listedSources.Add(new SearchIndexSource(JournalFilePath, SearchDocumentKind.File, Path.GetFileName(JournalFilePath),
            new List<string> { SearchIndex.JournalShelfName }));
        return (listedSources, folderSources);
    }

    /// <summary>The searchable text of a query itself: its SQL, row template, server and database (never the connection string).</summary>
    private static string BuildQueryDefinitionText(SavedQuery savedQuery)
    {
        return string.Join('\n', savedQuery.SqlText, savedQuery.RowTemplate, savedQuery.ServerName, savedQuery.DatabaseName);
    }

    /// <summary>Called after a file was saved in the app, so a search finds the new text straight away.</summary>
    private void UpdateSavedFileInSearchIndex(string savedFilePath)
    {
        searchIndex.UpdateFile(savedFilePath);
    }

    /// <summary>Called after a query ran successfully, so the words of its result can be searched later without running it.</summary>
    private void RecordQueryResultInSearchIndex(BookshelfEntry queryEntry, string resultMarkdown, DateTime ranAt)
    {
        if (queryEntry.Query is SavedQuery savedQuery)
        {
            searchIndex.RecordQueryResult(queryEntry.FilePath, savedQuery.Name, BuildQueryDefinitionText(savedQuery), resultMarkdown, ranAt);
        }
    }

    // ------------------------------------------------------------------
    // Menu and window
    // ------------------------------------------------------------------

    /// <summary>Search → Search all documents (Ctrl+Shift+F): opens the search window (or brings it forward) and checks for changed files.</summary>
    private void GlobalSearchCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (globalSearchWindow == null)
        {
            globalSearchWindow = new GlobalSearchWindow(searchIndex) { Owner = this };
            globalSearchWindow.DocumentChosen += (chosenHit, searchTerms) => _ = OpenSearchHitAsync(chosenHit, searchTerms);
            globalSearchWindow.RefreshIndexRequested += () => _ = RefreshSearchIndexFromButtonAsync();
            globalSearchWindow.Closed += (_, _) => globalSearchWindow = null;
            globalSearchWindow.SetIndexRefreshing(runningSearchIndexRefreshCount > 0);
            globalSearchWindow.Show();
        }
        else
        {
            globalSearchWindow.Activate();
            globalSearchWindow.FocusSearchBox();
        }

        // Pick up files that changed since the last check (only those are read again).
        _ = RefreshSearchIndexAsync(forceRereadAllFiles: false);
    }

    /// <summary>Search → Refresh search index: reads every document again and rewrites search-index.json.</summary>
    private void RefreshSearchIndexMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        _ = RefreshSearchIndexFromButtonAsync();
    }

    /// <summary>The work of the Refresh index button and menu item, with a note in the status bar when it is done.</summary>
    private async Task RefreshSearchIndexFromButtonAsync()
    {
        DocumentInfoStatusText.Text = "Refreshing the search index...";
        await RefreshSearchIndexAsync(forceRereadAllFiles: true);
        DocumentInfoStatusText.Text = $"Search index refreshed: {searchIndex.DocumentCount:N0} documents in {searchIndex.IndexFilePath}";
    }

    // ------------------------------------------------------------------
    // Opening a result
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens a search result in the main window and selects the first place a search term occurs.
    /// A query is run again (its result is not stored, only its words).
    /// </summary>
    private async Task OpenSearchHitAsync(SearchHit chosenHit, IReadOnlyList<string> searchTerms)
    {
        var chosenDocument = chosenHit.Document;
        if (chosenDocument.Kind == SearchDocumentKind.Query)
        {
            var queryEntry = appSettings.Bookshelves
                .SelectMany(bookshelf => bookshelf.Entries)
                .FirstOrDefault(bookshelfEntry => bookshelfEntry.IsQuery && bookshelfEntry.HasPath(chosenDocument.Key));
            if (queryEntry == null)
            {
                MessageBox.Show(globalSearchWindow ?? (Window)this, $"The query \"{chosenDocument.Title}\" is no longer on any bookshelf.",
                    "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!ReferenceEquals(queryEntry, currentQueryEntry))
            {
                if (!ConfirmDiscardUnsavedChanges())
                {
                    return;
                }
                await RunQueryAndShowResultAsync(queryEntry);
            }
        }
        else
        {
            bool isAlreadyOpen = currentFilePath != null && string.Equals(Path.GetFullPath(currentFilePath), chosenDocument.Key, StringComparison.OrdinalIgnoreCase);
            if (!isAlreadyOpen)
            {
                if (!File.Exists(chosenDocument.Key))
                {
                    MessageBox.Show(globalSearchWindow ?? (Window)this,
                        $"The file was not found. It may have been moved, renamed or deleted:\n{chosenDocument.Key}",
                        "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!ConfirmDiscardUnsavedChanges())
                {
                    return;
                }
                LoadFile(chosenDocument.Key);
            }
        }

        // Wait until the document has been laid out, then select the hit and scroll to it.
        await Dispatcher.InvokeAsync(() => SelectFirstSearchTermInDocument(searchTerms), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Selects the first occurrence of a search term in the shown document and scrolls it into view. Tries the terms in
    /// the order they were typed. Text split by formatting (e.g. half bold) is not found, in which case nothing happens.
    /// </summary>
    private void SelectFirstSearchTermInDocument(IReadOnlyList<string> searchTerms)
    {
        var shownDocument = DocumentReader.Document;
        if (shownDocument == null)
        {
            return;
        }

        foreach (string searchTerm in searchTerms)
        {
            // Walk through the document one stretch of plain text at a time.
            TextPointer? currentPosition = shownDocument.ContentStart;
            while (currentPosition != null)
            {
                if (currentPosition.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string textStretch = currentPosition.GetTextInRun(LogicalDirection.Forward);
                    int foundIndex = GlobalSearcher.IndexOfTerm(textStretch, searchTerm);
                    if (foundIndex >= 0)
                    {
                        TextPointer? hitStart = currentPosition.GetPositionAtOffset(foundIndex);
                        TextPointer? hitEnd = hitStart?.GetPositionAtOffset(searchTerm.Length);
                        if (hitStart != null && hitEnd != null)
                        {
                            DocumentReader.Selection?.Select(hitStart, hitEnd);
                            (hitStart.Parent as FrameworkContentElement)?.BringIntoView();
                            return;
                        }
                    }
                }
                currentPosition = currentPosition.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
    }
}
