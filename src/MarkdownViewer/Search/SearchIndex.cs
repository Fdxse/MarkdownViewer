using System.IO;
using System.Text.Json;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer.Search;

/// <summary>
/// The lookup file for global search: search-index.json next to the exe (like todo.md), holding the different words
/// (each only once, see <see cref="SearchWordList"/>) of every document on every bookshelf, the todo list, and every
/// saved query with its last result. The full text is not stored.
/// <para>
/// It is kept up to date by <see cref="RefreshAsync"/>, which runs at startup and each time the search window opens:
/// it compares each file's modified time and size with what is stored and reads only files that changed.
/// Saving in the app updates a file straight away (<see cref="UpdateFile"/>), and opening a query stores its result
/// (<see cref="RecordQueryResult"/>). Finding documents only reads this index, so it is quick even with many files on
/// slow network shares, and it never runs a query against a database. Only the matching lines shown for the best
/// results are read from the files themselves.
/// </para>
/// The index can always be deleted: it is rebuilt from the bookshelves the next time the app starts.
/// All public members may be called from any thread.
/// </summary>
public sealed class SearchIndex
{
    /// <summary>Name of the lookup file.</summary>
    public const string FileName = "search-index.json";

    /// <summary>Format version written to the file, so a later version of the app can tell old files apart.</summary>
    private const int CurrentFormatVersion = 1;

    /// <summary>Files larger than this are indexed by name only, so one huge log file cannot bloat the index.</summary>
    public const long MaximumIndexedFileSizeInBytes = 5 * 1024 * 1024;

    /// <summary>Shelf name shown for todo.md in the results.</summary>
    public const string TodoListShelfName = "Todo list";

    /// <summary>Shelf name of archived files (the Archive bookshelf, which shows the Archive folder next to the exe).</summary>
    public const string ArchiveShelfName = MarkdownViewer.Archiving.DocumentArchive.FolderName;

    /// <summary>
    /// Compact JSON: the file is not meant to be edited by hand, and indenting would only make it bigger.
    /// Letters such as "å" are written as they are instead of as "\u00E5", which also keeps the file small.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Guards <see cref="documentsByKey"/> and <see cref="LastRefreshedAt"/>.</summary>
    private readonly object indexLock = new();

    /// <summary>Makes sure only one save writes the file at a time (saves run in the background).</summary>
    private readonly SemaphoreSlim saveLock = new(1, 1);

    /// <summary>Makes sure only one refresh runs at a time; a second request waits and then runs with the newest bookshelves.</summary>
    private readonly SemaphoreSlim refreshLock = new(1, 1);

    /// <summary>The indexed documents by key (file paths are not case-sensitive on Windows, so neither are the keys).</summary>
    private Dictionary<string, SearchIndexDocument> documentsByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path of the lookup file (next to the exe, or in %APPDATA% if the exe's folder is read-only).</summary>
    public string IndexFilePath { get; }

    /// <summary>When the last refresh finished, or null if none has finished since the app started.</summary>
    public DateTime? LastRefreshedAt { get; private set; }

    /// <summary>Raised (on a background thread) after a refresh, a saved file or a query result changed the index.</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Creates the index for the given lookup file. Call <see cref="LoadAsync"/> to read what is already stored.</summary>
    public SearchIndex(string indexFilePath)
    {
        IndexFilePath = indexFilePath;
    }

    /// <summary>
    /// Works out where the lookup file goes: next to the exe as asked, unless that folder cannot be written
    /// (e.g. the exe was put in Program Files), in which case it goes to %APPDATA%\MarkdownViewer next to settings.json.
    /// </summary>
    public static string ChooseIndexFilePath()
    {
        string exeFolderPath = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        string besideExePath = Path.Combine(exeFolderPath, FileName);
        if (CanWriteFile(besideExePath))
        {
            return besideExePath;
        }
        return Path.Combine(MarkdownViewer.Macros.AppSettings.SettingsFolderPath, FileName);
    }

    /// <summary>True if the file already exists and can be written, or a new file can be created in its folder.</summary>
    private static bool CanWriteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                using var existingFileStream = new FileStream(filePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                return true;
            }

            // Try to create (and immediately delete) a small file next to it.
            string probeFilePath = filePath + ".write-test";
            File.WriteAllText(probeFilePath, string.Empty);
            File.Delete(probeFilePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Reading the index
    // ------------------------------------------------------------------

    /// <summary>Number of documents in the index.</summary>
    public int DocumentCount
    {
        get
        {
            lock (indexLock)
            {
                return documentsByKey.Count;
            }
        }
    }

    /// <summary>
    /// A copy of every document, safe to search on a background thread while the index keeps changing.
    /// Copies are cheap: the word lists themselves are shared, not duplicated.
    /// </summary>
    public List<SearchIndexDocument> GetDocumentsSnapshot()
    {
        lock (indexLock)
        {
            return documentsByKey.Values.Select(indexedDocument => indexedDocument.Clone()).ToList();
        }
    }

    // ------------------------------------------------------------------
    // Loading and saving the lookup file
    // ------------------------------------------------------------------

    /// <summary>The shape of search-index.json.</summary>
    private sealed class IndexFileContents
    {
        /// <summary>Format version (<see cref="CurrentFormatVersion"/>).</summary>
        public int FormatVersion { get; set; }

        /// <summary>When the file was written.</summary>
        public DateTime SavedAt { get; set; }

        /// <summary>Every indexed document.</summary>
        public List<SearchIndexDocument> Documents { get; set; } = new();
    }

    /// <summary>
    /// Reads the lookup file, if there is one. A missing, broken or newer-format file is simply ignored:
    /// the next refresh rebuilds the index from the bookshelves.
    /// </summary>
    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(IndexFilePath))
            {
                return;
            }

            await using var indexFileStream = File.OpenRead(IndexFilePath);
            var storedContents = await JsonSerializer.DeserializeAsync<IndexFileContents>(indexFileStream, JsonOptions);
            if (storedContents == null || storedContents.FormatVersion != CurrentFormatVersion)
            {
                return;
            }

            var loadedDocuments = new Dictionary<string, SearchIndexDocument>(StringComparer.OrdinalIgnoreCase);
            foreach (var storedDocument in storedContents.Documents.Where(storedDocument => !string.IsNullOrEmpty(storedDocument.Key)))
            {
                loadedDocuments[storedDocument.Key] = storedDocument;
            }

            lock (indexLock)
            {
                documentsByKey = loadedDocuments;
            }
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A damaged lookup file is not worth bothering the user about: it is rebuilt by the next refresh.
        }
    }

    /// <summary>
    /// Writes the lookup file in the background. It is written to a temporary file first and then moved into place,
    /// so a crash halfway through never leaves a broken index behind. Failures are ignored (searching still works
    /// from memory; the index is just rebuilt at the next start).
    /// </summary>
    private async Task SaveAsync()
    {
        await saveLock.WaitAsync();
        try
        {
            var contentsToWrite = new IndexFileContents
            {
                FormatVersion = CurrentFormatVersion,
                SavedAt = DateTime.Now,
                Documents = GetDocumentsSnapshot()
                    .OrderBy(indexedDocument => indexedDocument.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };

            string? indexFolderPath = Path.GetDirectoryName(IndexFilePath);
            if (!string.IsNullOrEmpty(indexFolderPath))
            {
                Directory.CreateDirectory(indexFolderPath);
            }

            string temporaryFilePath = IndexFilePath + ".tmp";
            await using (var temporaryFileStream = File.Create(temporaryFilePath))
            {
                await JsonSerializer.SerializeAsync(temporaryFileStream, contentsToWrite, JsonOptions);
            }
            File.Move(temporaryFilePath, IndexFilePath, overwrite: true);
        }
        catch (Exception)
        {
            // See the summary: not saving the lookup file only means it is rebuilt next time.
        }
        finally
        {
            saveLock.Release();
        }
    }

    // ------------------------------------------------------------------
    // Keeping the index up to date
    // ------------------------------------------------------------------

    /// <summary>
    /// Brings the index in line with the bookshelves, in the background: lists the folder bookshelves, re-reads only
    /// files whose modified time or size changed, drops documents that are no longer on any shelf, and saves the
    /// lookup file if anything changed.
    /// </summary>
    /// <param name="listedSources">Files and queries on normal and query bookshelves, plus todo.md.</param>
    /// <param name="folderSources">Folder bookshelves, whose files are listed here.</param>
    /// <param name="forceRereadAllFiles">True to read every file again even if it looks unchanged (Search → Rebuild index).</param>
    public async Task RefreshAsync(IReadOnlyList<SearchIndexSource> listedSources, IReadOnlyList<SearchIndexFolderSource> folderSources, bool forceRereadAllFiles = false)
    {
        await refreshLock.WaitAsync();
        try
        {
            bool hasAnythingChanged = await Task.Run(() => RefreshOnBackgroundThread(listedSources, folderSources, forceRereadAllFiles));
            lock (indexLock)
            {
                LastRefreshedAt = DateTime.Now;
            }

            if (hasAnythingChanged)
            {
                await SaveAsync();
            }
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>The work of <see cref="RefreshAsync"/>. Returns true if the index changed.</summary>
    private bool RefreshOnBackgroundThread(IReadOnlyList<SearchIndexSource> listedSources, IReadOnlyList<SearchIndexFolderSource> folderSources, bool forceRereadAllFiles)
    {
        // 1. Everything that should be in the index, with all the shelves each document is on.
        var wantedSourcesByKey = new Dictionary<string, SearchIndexSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var listedSource in listedSources)
        {
            AddOrMergeSource(wantedSourcesByKey, listedSource);
        }
        foreach (var folderSource in folderSources)
        {
            foreach (string folderFilePath in ListFolderShelfFiles(folderSource))
            {
                AddOrMergeSource(wantedSourcesByKey, new SearchIndexSource(
                    folderFilePath, SearchDocumentKind.File, Path.GetFileName(folderFilePath), new List<string> { folderSource.ShelfName }));
            }
        }

        // 2. Build the new index, reusing the stored text of every file that has not changed.
        Dictionary<string, SearchIndexDocument> previousDocumentsByKey;
        lock (indexLock)
        {
            previousDocumentsByKey = documentsByKey;
        }

        var refreshedDocumentsByKey = new Dictionary<string, SearchIndexDocument>(StringComparer.OrdinalIgnoreCase);
        bool hasAnythingChanged = false;
        foreach (var wantedSource in wantedSourcesByKey.Values)
        {
            previousDocumentsByKey.TryGetValue(wantedSource.Key, out var previousDocument);
            SearchIndexDocument? refreshedDocument = wantedSource.Kind == SearchDocumentKind.Query
                ? BuildQueryDocument(wantedSource, previousDocument)
                : BuildFileDocument(wantedSource, previousDocument, forceRereadAllFiles);

            // A file that does not exist (moved, deleted, drive not connected) is left out until it is back.
            if (refreshedDocument == null)
            {
                continue;
            }

            refreshedDocumentsByKey[wantedSource.Key] = refreshedDocument;
            if (previousDocument == null || !HaveSameContents(previousDocument, refreshedDocument))
            {
                hasAnythingChanged = true;
            }
        }

        // Documents that are no longer on any shelf (or whose file disappeared) are dropped.
        if (previousDocumentsByKey.Keys.Any(previousKey => !refreshedDocumentsByKey.ContainsKey(previousKey)))
        {
            hasAnythingChanged = true;
        }

        lock (indexLock)
        {
            // Keep query results recorded while this refresh was running (the query was opened in the meantime).
            foreach (var (documentKey, currentDocument) in documentsByKey)
            {
                if (currentDocument.Kind == SearchDocumentKind.Query
                    && refreshedDocumentsByKey.TryGetValue(documentKey, out var refreshedQueryDocument)
                    && currentDocument.LastQueryRunAt > refreshedQueryDocument.LastQueryRunAt.GetValueOrDefault(DateTime.MinValue))
                {
                    refreshedQueryDocument.LastQueryResultWords = currentDocument.LastQueryResultWords;
                    refreshedQueryDocument.LastQueryRunAt = currentDocument.LastQueryRunAt;
                }
            }
            documentsByKey = refreshedDocumentsByKey;
        }
        return hasAnythingChanged;
    }

    /// <summary>Adds a source to the wanted list, or adds its shelf names to the one already there (a file can be on several shelves).</summary>
    private static void AddOrMergeSource(Dictionary<string, SearchIndexSource> wantedSourcesByKey, SearchIndexSource newSource)
    {
        if (!wantedSourcesByKey.TryGetValue(newSource.Key, out var existingSource))
        {
            wantedSourcesByKey[newSource.Key] = newSource with { ShelfNames = new List<string>(newSource.ShelfNames) };
            return;
        }

        foreach (string shelfName in newSource.ShelfNames)
        {
            if (!existingSource.ShelfNames.Contains(shelfName, StringComparer.CurrentCultureIgnoreCase))
            {
                existingSource.ShelfNames.Add(shelfName);
            }
        }
    }

    /// <summary>Lists the Markdown files of a folder bookshelf, the same way the bookshelf itself does (including its file limit).</summary>
    private static IEnumerable<string> ListFolderShelfFiles(SearchIndexFolderSource folderSource)
    {
        // A throw-away copy of the shelf does the scanning, so the shown shelf is never touched from this thread.
        var scanningShelf = new NamedBookshelf
        {
            Name = folderSource.ShelfName,
            FolderPath = folderSource.FolderPath,
            IncludeSubfolders = folderSource.IncludeSubfolders,
        };
        scanningShelf.RescanFolder();
        return scanningShelf.Entries.Select(folderEntry => folderEntry.FilePath).ToList();
    }

    /// <summary>
    /// Makes the index document for a file. Reuses the stored words if the file's modified time and size are unchanged;
    /// otherwise reads the file. Returns null if the file does not exist.
    /// </summary>
    private static SearchIndexDocument? BuildFileDocument(SearchIndexSource fileSource, SearchIndexDocument? previousDocument, bool forceReread)
    {
        FileInfo fileDetails;
        try
        {
            fileDetails = new FileInfo(fileSource.Key);
            if (!fileDetails.Exists)
            {
                return null;
            }
        }
        catch (Exception)
        {
            // An invalid path (e.g. hand-edited settings): nothing to index.
            return null;
        }

        var fileDocument = new SearchIndexDocument
        {
            Key = fileSource.Key,
            Kind = SearchDocumentKind.File,
            Title = fileSource.Title,
            ShelfNames = fileSource.ShelfNames,
            FileLastWriteTimeUtc = fileDetails.LastWriteTimeUtc,
            FileSizeInBytes = fileDetails.Length,
        };

        bool isUnchanged = !forceReread
            && previousDocument != null
            && previousDocument.Kind == SearchDocumentKind.File
            && previousDocument.FileLastWriteTimeUtc == fileDetails.LastWriteTimeUtc
            && previousDocument.FileSizeInBytes == fileDetails.Length;
        fileDocument.Words = isUnchanged ? previousDocument!.Words : ReadFileWordsForIndex(fileDetails, previousDocument);
        return fileDocument;
    }

    /// <summary>
    /// Reads a file and makes its word list for the index. Too-large files get no words (they can still be found by
    /// name). If the file cannot be read right now (locked, network hiccup), the previously stored words are kept.
    /// </summary>
    private static string ReadFileWordsForIndex(FileInfo fileDetails, SearchIndexDocument? previousDocument)
    {
        if (fileDetails.Length > MaximumIndexedFileSizeInBytes)
        {
            return string.Empty;
        }

        try
        {
            // FileShare.ReadWrite: an editor that has the file open must not stop it from being indexed.
            using var fileStream = new FileStream(fileDetails.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var fileReader = new StreamReader(fileStream, detectEncodingFromByteOrderMarks: true);
            return SearchWordList.Build(fileReader.ReadToEnd());
        }
        catch (Exception)
        {
            return previousDocument?.Words ?? string.Empty;
        }
    }

    /// <summary>Makes the index document for a query: its current SQL and name, plus the last stored result.</summary>
    private static SearchIndexDocument BuildQueryDocument(SearchIndexSource querySource, SearchIndexDocument? previousDocument)
    {
        return new SearchIndexDocument
        {
            Key = querySource.Key,
            Kind = SearchDocumentKind.Query,
            Title = querySource.Title,
            ShelfNames = querySource.ShelfNames,
            Words = SearchWordList.Build(querySource.QueryDefinitionText),
            LastQueryResultWords = previousDocument?.LastQueryResultWords ?? string.Empty,
            LastQueryRunAt = previousDocument?.LastQueryRunAt,
        };
    }

    /// <summary>True if two versions of a document would be written to the lookup file the same way.</summary>
    private static bool HaveSameContents(SearchIndexDocument firstDocument, SearchIndexDocument secondDocument)
    {
        return firstDocument.Kind == secondDocument.Kind
            && firstDocument.Title == secondDocument.Title
            && firstDocument.ShelfNames.SequenceEqual(secondDocument.ShelfNames)
            && firstDocument.FileLastWriteTimeUtc == secondDocument.FileLastWriteTimeUtc
            && firstDocument.FileSizeInBytes == secondDocument.FileSizeInBytes
            && firstDocument.LastQueryRunAt == secondDocument.LastQueryRunAt
            // string.Equals returns at once when both are the same (reused) text, so this is cheap for unchanged files.
            && string.Equals(firstDocument.Words, secondDocument.Words, StringComparison.Ordinal)
            && string.Equals(firstDocument.LastQueryResultWords, secondDocument.LastQueryResultWords, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-reads one file straight away, e.g. right after it was saved in the app. Only files that are already in
    /// the index are updated (a file that is on no bookshelf is not searched). Runs in the background.
    /// </summary>
    public void UpdateFile(string filePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            SearchIndexDocument? previousDocument;
            lock (indexLock)
            {
                documentsByKey.TryGetValue(fullPath, out previousDocument);
            }
            if (previousDocument == null || previousDocument.Kind != SearchDocumentKind.File)
            {
                return;
            }

            var fileSource = new SearchIndexSource(fullPath, SearchDocumentKind.File, previousDocument.Title, previousDocument.ShelfNames);
            var updatedDocument = BuildFileDocument(fileSource, previousDocument, forceReread: true);
            lock (indexLock)
            {
                if (updatedDocument == null)
                {
                    documentsByKey.Remove(fullPath);
                }
                else
                {
                    documentsByKey[fullPath] = updatedDocument;
                }
            }

            await SaveAsync();
            IndexChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Stores the words of the result of a query that was just opened, so the result can be searched later without running the
    /// query again. Queries that are not in the index yet are added (the next refresh fills in their shelves).
    /// </summary>
    /// <param name="queryKey">The query's bookshelf id ("query:" + GUID).</param>
    /// <param name="queryName">Name of the query, used as the title.</param>
    /// <param name="queryDefinitionText">The query's SQL and row template.</param>
    /// <param name="resultMarkdown">The Markdown document the result was shown as.</param>
    /// <param name="ranAt">When the query ran.</param>
    public void RecordQueryResult(string queryKey, string queryName, string queryDefinitionText, string resultMarkdown, DateTime ranAt)
    {
        lock (indexLock)
        {
            if (!documentsByKey.TryGetValue(queryKey, out var queryDocument))
            {
                queryDocument = new SearchIndexDocument { Key = queryKey, Kind = SearchDocumentKind.Query };
                documentsByKey[queryKey] = queryDocument;
            }

            // Replace rather than change the stored object: snapshots handed out earlier must not change under a search.
            var updatedDocument = queryDocument.Clone();
            updatedDocument.Title = queryName;
            updatedDocument.Words = SearchWordList.Build(queryDefinitionText);
            // Only the different words of the result are kept, not the rows themselves.
            updatedDocument.LastQueryResultWords = SearchWordList.Build(resultMarkdown);
            updatedDocument.LastQueryRunAt = ranAt;
            documentsByKey[queryKey] = updatedDocument;
        }

        _ = Task.Run(async () =>
        {
            await SaveAsync();
            IndexChanged?.Invoke(this, EventArgs.Empty);
        });
    }
}
