using System.Text.Json.Serialization;

namespace MarkdownViewer.Search;

/// <summary>What kind of thing a document in the search index is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SearchDocumentKind
{
    /// <summary>A Markdown file on disk (on a bookshelf, a folder bookshelf, or the todo list).</summary>
    File,

    /// <summary>A saved SQL Server query on a query bookshelf.</summary>
    Query,
}

/// <summary>
/// One document in the search index (search-index.json next to the exe): enough to find it again, plus its different words,
/// so finding documents never has to read every file from disk or run queries against a database.
/// </summary>
public class SearchIndexDocument
{
    /// <summary>
    /// Unique key: the full path of a file, or the bookshelf id of a query ("query:" + a GUID, see
    /// <see cref="MarkdownViewer.Bookshelf.BookshelfEntry.QueryPathPrefix"/>).
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>File or query.</summary>
    public SearchDocumentKind Kind { get; set; }

    /// <summary>Name shown in the results: the file name, or the name of the query.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The bookshelves the document is on (or "Todo list"), shown in grey in the results.</summary>
    public List<string> ShelfNames { get; set; } = new();

    /// <summary>For a file: when it was last changed, used to tell whether it must be read again.</summary>
    public DateTime FileLastWriteTimeUtc { get; set; }

    /// <summary>For a file: its size in bytes, checked together with <see cref="FileLastWriteTimeUtc"/>.</summary>
    public long FileSizeInBytes { get; set; }

    /// <summary>
    /// The searchable words: every different word of the document once, in lower case, sorted and separated by spaces
    /// (see <see cref="SearchWordList"/>). For a file: the words of its text (empty if it is larger than
    /// <see cref="SearchIndex.MaximumIndexedFileSizeInBytes"/>). For a query: the words of its SQL and row template.
    /// The full text is not stored: the lines shown in the results are read from the file itself.
    /// </summary>
    public string Words { get; set; } = string.Empty;

    /// <summary>
    /// For a query: the different words of the result from the last time it was opened in the app, or empty if it has
    /// not been opened since search was added. Searching never runs queries, so this is all that is known of the result.
    /// </summary>
    public string LastQueryResultWords { get; set; } = string.Empty;

    /// <summary>For a query: when <see cref="LastQueryResultWords"/> was made; null if it has never been stored.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastQueryRunAt { get; set; }

    /// <summary>A copy of this document (the index hands out copies, so a search in the background never sees half an update; the word lists themselves are shared, not copied).</summary>
    public SearchIndexDocument Clone()
    {
        var documentCopy = (SearchIndexDocument)MemberwiseClone();
        documentCopy.ShelfNames = new List<string>(ShelfNames);
        return documentCopy;
    }
}

/// <summary>
/// One thing the index should contain, as worked out from the bookshelves on the UI thread. The background refresh
/// turns these into <see cref="SearchIndexDocument"/>s, reading only the files that changed.
/// </summary>
/// <param name="Key">Full file path, or the query's bookshelf id.</param>
/// <param name="Kind">File or query.</param>
/// <param name="Title">Name shown in the results.</param>
/// <param name="ShelfNames">The bookshelves it is on.</param>
/// <param name="QueryDefinitionText">For a query: its SQL and row template (searched together with the last result).</param>
public sealed record SearchIndexSource(string Key, SearchDocumentKind Kind, string Title, List<string> ShelfNames, string QueryDefinitionText = "");

/// <summary>
/// A folder bookshelf to index. Its files are listed in the background, because a big folder (with subfolders,
/// or on a network share) can take a while to scan.
/// </summary>
/// <param name="ShelfName">Name of the folder bookshelf.</param>
/// <param name="FolderPath">The folder it lists.</param>
/// <param name="IncludeSubfolders">True if its subfolders are listed too.</param>
public sealed record SearchIndexFolderSource(string ShelfName, string FolderPath, bool IncludeSubfolders);
