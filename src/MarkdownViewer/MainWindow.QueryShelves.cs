using System.Windows;
using System.Windows.Input;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Queries;

namespace MarkdownViewer;

/// <summary>
/// Query bookshelves: a shelf whose entries are saved SQL Server queries instead of files. Opening a query runs it
/// and shows the result as a Markdown document, so search, zoom, print and Save As work as for any document.
/// Instead of the Markdown editor, a query has a settings page (<see cref="QuerySettingsWindow"/>), opened with
/// Ctrl+E, the "Edit query" button in the properties panel, or right-click → Edit query.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tag of the context menu items that only make sense for files (Show in folder, Copy path).</summary>
    private const string FileOnlyMenuTag = "FileOnly";

    /// <summary>Tag of the context menu items that only make sense for queries (Edit query).</summary>
    private const string QueryOnlyMenuTag = "QueryOnly";

    /// <summary>Text shown in an empty query shelf.</summary>
    private const string EmptyQueryShelfHintText =
        "This query bookshelf is empty. Click New... to add a SQL Server query; opening it runs the query and shows the result.";

    /// <summary>The query whose result is shown, or null when a file (or the welcome page) is shown.</summary>
    private BookshelfEntry? currentQueryEntry;

    /// <summary>The last run of <see cref="currentQueryEntry"/>, for the properties panel; null if it failed or has not run.</summary>
    private QueryRunResult? currentQueryRunResult;

    /// <summary>Cancels the query that is running, when another one is started (or the same one again).</summary>
    private CancellationTokenSource? runningQueryCancellation;

    // ------------------------------------------------------------------
    // Creating query shelves and queries
    // ------------------------------------------------------------------

    /// <summary>Shelf menu → New query bookshelf: asks for a name, shows the new shelf and offers to add the first query.</summary>
    private void NewQueryShelfMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        string? newShelfName = AskForShelfName("New query bookshelf",
            "Name of the new query bookshelf, for example \"Reports\" or \"Sales database\":", string.Empty, shelfBeingRenamed: null);
        if (newShelfName == null)
        {
            return;
        }

        var newShelf = new NamedBookshelf { Name = newShelfName, IsQueryShelf = true };
        appSettings.Bookshelves.Add(newShelf);
        SaveSettingsOrWarn();
        ShowBookshelf(newShelf);

        // An empty query shelf is not much use: go straight to the first query.
        AddNewQueryToCurrentShelf();
    }

    /// <summary>
    /// Opens the settings page for a new query and, if the user saves it, puts it on the shown query shelf and runs it.
    /// The connection of the selected (or last) query on the shelf is filled in, since queries on one shelf
    /// usually share a server.
    /// </summary>
    private void AddNewQueryToCurrentShelf()
    {
        if (!currentBookshelf.IsQueryShelf)
        {
            return;
        }

        var connectionSourceQuery = (BookshelfList.SelectedItem as BookshelfEntry)?.Query
            ?? currentBookshelf.Entries.LastOrDefault()?.Query;
        var newQuery = new SavedQuery();
        if (connectionSourceQuery != null)
        {
            newQuery.EncryptedConnectionString = connectionSourceQuery.EncryptedConnectionString;
            newQuery.ServerName = connectionSourceQuery.ServerName;
            newQuery.DatabaseName = connectionSourceQuery.DatabaseName;
        }

        var settingsWindow = new QuerySettingsWindow(newQuery) { Owner = this };
        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        var newEntry = BookshelfEntry.CreateForQuery(settingsWindow.EditedQuery);
        currentBookshelf.Entries.Add(newEntry);
        SaveSettingsOrWarn();
        RefreshBookshelfList();

        if (ConfirmDiscardUnsavedChanges())
        {
            _ = RunQueryAndShowResultAsync(newEntry);
        }
    }

    /// <summary>
    /// Opens the settings page of a query. If the user saves, the changes are stored and the query is run again
    /// (and shown, even if another document was open).
    /// </summary>
    private void EditQuery(BookshelfEntry queryEntry)
    {
        if (queryEntry.Query == null)
        {
            return;
        }

        var settingsWindow = new QuerySettingsWindow(queryEntry.Query) { Owner = this };
        if (settingsWindow.ShowDialog() != true)
        {
            return;
        }

        queryEntry.Query = settingsWindow.EditedQuery;
        SaveSettingsOrWarn();
        RefreshBookshelfList();

        bool isShownQuery = ReferenceEquals(queryEntry, currentQueryEntry);
        if (isShownQuery || ConfirmDiscardUnsavedChanges())
        {
            _ = RunQueryAndShowResultAsync(queryEntry);
        }
    }

    // ------------------------------------------------------------------
    // Running queries
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs a query in the background and shows its result (or what went wrong) as the open document.
    /// Starting another run cancels this one. The caller has already dealt with unsaved changes.
    /// </summary>
    private async Task RunQueryAndShowResultAsync(BookshelfEntry queryEntry)
    {
        var savedQuery = queryEntry.Query;
        if (savedQuery == null)
        {
            return;
        }

        // Only the newest run may show its result, so stop any query that is still running.
        runningQueryCancellation?.Cancel();
        runningQueryCancellation = null;
        // The cancelled run leaves the cursor alone (it is no longer the newest), so reset it here.
        Mouse.OverrideCursor = null;

        if (!savedQuery.TryGetConnectionString(out string connectionString, out string? connectionProblem))
        {
            ShowQueryProblem(queryEntry, "The query has no usable connection.", connectionProblem ?? string.Empty);
            return;
        }

        if (!savedQuery.AllowAllStatements)
        {
            string? readOnlyProblem = SqlReadOnlyCheck.FindProblem(savedQuery.SqlText);
            if (readOnlyProblem != null)
            {
                ShowQueryProblem(queryEntry, "The query was not run, because it may change data.",
                    readOnlyProblem + "\n\nOnly SELECT queries are run, unless \"Allow more than SELECT\" is ticked under Edit query.");
                return;
            }
        }

        var thisRunCancellation = new CancellationTokenSource();
        runningQueryCancellation = thisRunCancellation;

        // The window stays usable while the query runs; the arrow-with-hourglass cursor shows something is happening.
        Mouse.OverrideCursor = Cursors.AppStarting;
        DocumentInfoStatusText.Text = $"Running \"{savedQuery.Name}\" on {savedQuery.ServerName}...";
        try
        {
            QueryRunResult runResult = await SqlQueryRunner.RunAsync(savedQuery, connectionString, thisRunCancellation.Token);
            if (thisRunCancellation.IsCancellationRequested)
            {
                return;
            }

            currentQueryRunResult = runResult;
            string resultMarkdown = QueryMarkdownBuilder.Build(savedQuery, runResult);
            ShowQueryDocument(queryEntry, resultMarkdown);
            // Store the words of the result, so global search can find this query by its result without running it.
            RecordQueryResultInSearchIndex(queryEntry, resultMarkdown, runResult.RanAt);
        }
        catch (Exception) when (thisRunCancellation.IsCancellationRequested)
        {
            // A newer run replaced this one; its result is what the user wants to see. Nothing to report.
        }
        catch (Exception queryError)
        {
            ShowQueryProblem(queryEntry, "The query could not be run.", QueryErrorText.Describe(queryError));
        }
        finally
        {
            if (ReferenceEquals(runningQueryCancellation, thisRunCancellation))
            {
                runningQueryCancellation = null;
                Mouse.OverrideCursor = null;
                UpdateTitleAndStatus();
            }
            thisRunCancellation.Dispose();
        }
    }

    /// <summary>Shows an error page for a query that could not run, so the previous document is not left on screen.</summary>
    private void ShowQueryProblem(BookshelfEntry queryEntry, string problemTitle, string problemDetails)
    {
        currentQueryRunResult = null;
        ShowQueryDocument(queryEntry, QueryMarkdownBuilder.BuildErrorDocument(queryEntry.Query!, problemTitle, problemDetails));
    }

    /// <summary>Shows Markdown made from a query as the open document. Queries have a settings page instead of the editor, so the editor is closed.</summary>
    private void ShowQueryDocument(BookshelfEntry queryEntry, string markdownText)
    {
        if (isEditMode)
        {
            SetEditMode(false);
        }
        SetDocumentText(markdownText, filePath: null, queryEntry: queryEntry);
    }

    // ------------------------------------------------------------------
    // Hooks used by the rest of the window
    // ------------------------------------------------------------------

    /// <summary>
    /// Opens a query from the bookshelf (runs it). Clicking the query that is already shown does nothing;
    /// F5 runs it again.
    /// </summary>
    private void OpenQueryEntry(BookshelfEntry queryEntry)
    {
        if (ReferenceEquals(queryEntry, currentQueryEntry))
        {
            return;
        }

        if (ConfirmDiscardUnsavedChanges())
        {
            _ = RunQueryAndShowResultAsync(queryEntry);
        }
        else
        {
            SelectOpenFileInBookshelf();
        }
    }

    /// <summary>
    /// Tells the user that files cannot be put on a query shelf. Returns true if the shown shelf is a
    /// query shelf (so the caller should stop).
    /// </summary>
    private bool RejectFilesOnQueryShelf()
    {
        if (!currentBookshelf.IsQueryShelf)
        {
            return false;
        }

        MessageBox.Show(this,
            $"\"{currentBookshelf.Name}\" is a query bookshelf: it holds SQL Server queries, not files.\n\n" +
            "Click New... to add a query, or switch to another bookshelf to add files.",
            "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    /// <summary>Context menu → Edit query (query shelves only).</summary>
    private void EditQueryMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (BookshelfList.SelectedItem is BookshelfEntry { IsQuery: true } selectedEntry)
        {
            EditQuery(selectedEntry);
        }
    }

    /// <summary>Adds the "Query" section of the properties panel for the shown query.</summary>
    private void AddQueryProperties(BookshelfEntry queryEntry)
    {
        var savedQuery = queryEntry.Query!;
        AddPropertySectionHeader("Query");
        AddPropertyRow("Name", savedQuery.Name);
        AddPropertyRow("Server", savedQuery.ServerName);
        if (!string.IsNullOrWhiteSpace(savedQuery.DatabaseName))
        {
            AddPropertyRow("Database", savedQuery.DatabaseName);
        }
        AddPropertyRow("Returns", savedQuery.ResultMode == QueryResultMode.Markdown
            ? "Markdown"
            : string.IsNullOrWhiteSpace(savedQuery.RowTemplate) ? "Rows, shown as a table" : "Rows, shown through the row template");

        if (currentQueryRunResult != null)
        {
            string rowCountText = $"{currentQueryRunResult.TotalRowCount:N0}";
            if (currentQueryRunResult.WasCutShortAtRowLimit)
            {
                rowCountText += $" (stopped at the limit of {savedQuery.MaximumRowCount:N0})";
            }
            AddPropertyRow("Rows", rowCountText);
            AddPropertyRow("Run at", FormatDate(currentQueryRunResult.RanAt));
            AddPropertyRow("Took", $"{currentQueryRunResult.Duration.TotalSeconds:0.00} s");
        }
        else
        {
            AddPropertyRow("Status", "Not run (see the document)");
        }

        var shelvesWithQuery = appSettings.Bookshelves
            .Where(bookshelf => bookshelf.Entries.Contains(queryEntry))
            .Select(bookshelf => bookshelf.Name);
        AddPropertyRow("Bookshelf", string.Join(", ", shelvesWithQuery));
    }
}
