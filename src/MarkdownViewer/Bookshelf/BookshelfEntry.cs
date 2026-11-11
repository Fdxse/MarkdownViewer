using System.IO;
using System.Text.Json.Serialization;
using MarkdownViewer.Queries;
using MarkdownViewer.Trash;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// One Markdown file on the bookshelf. Only the path and the date it was added are stored in
/// the settings file; the other properties are worked out from the path when displayed.
/// On a query bookshelf the entry holds a saved SQL Server <see cref="Query"/> instead of a file.
/// </summary>
public class BookshelfEntry
{
    /// <summary>Start of the made-up <see cref="FilePath"/> of a query entry, e.g. "query:3f2b...".</summary>
    public const string QueryPathPrefix = "query:";

    /// <summary>
    /// Full path of the Markdown file. The file can be anywhere on disk or on a network share.
    /// For a query entry this is a unique made-up id ("query:" + a GUID), so the list, the drag-and-drop
    /// ordering and the selection can treat queries and files the same way.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>The saved query, for an entry on a query bookshelf; null (and left out of the settings file) for a file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SavedQuery? Query { get; set; }

    /// <summary>True if this entry is a saved query rather than a file.</summary>
    [JsonIgnore]
    public bool IsQuery => Query != null;

    /// <summary>
    /// Set for the entries of the Trash list only: the trashed item the entry stands for. A trashed file's
    /// <see cref="FilePath"/> is where the file is now, inside the Trash folder. Never stored in the settings file.
    /// </summary>
    [JsonIgnore]
    public TrashItem? TrashItem { get; set; }

    /// <summary>True if the entry has no file on disk: a saved query, or a trashed query or bookshelf.</summary>
    [JsonIgnore]
    public bool HasNoFile => IsQuery || (TrashItem != null && TrashItem.Kind != TrashItemKind.File);

    /// <summary>Makes a bookshelf entry for a query, with a new unique id.</summary>
    public static BookshelfEntry CreateForQuery(SavedQuery savedQuery) => new()
    {
        FilePath = QueryPathPrefix + Guid.NewGuid().ToString("N"),
        AddedOn = DateTime.Now,
        Query = savedQuery,
    };

    /// <summary>Tooltip in the list: the full path of a file, or the server and database of a query.</summary>
    [JsonIgnore]
    public string ToolTipText => TrashItem != null
        ? TrashItem.DescriptionText
        : Query == null
        ? FilePath
        : string.IsNullOrWhiteSpace(Query.DatabaseName) ? $"SQL Server query on {Query.ServerName}" : $"SQL Server query on {Query.ServerName} / {Query.DatabaseName}";

    /// <summary>When the file was put on the bookshelf.</summary>
    public DateTime AddedOn { get; set; }

    /// <summary>File name shown in the list, e.g. "README.md".</summary>
    [JsonIgnore]
    public string FileName => TrashItem?.DisplayName ?? Query?.Name ?? Path.GetFileName(FilePath);

    /// <summary>
    /// Text shown in the list. Normally the file name; on a folder shelf with subfolders it includes the
    /// subfolder (e.g. "2025\notes.md"). Not stored in the settings file.
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get => displayNameOverride ?? FileName;
        set => displayNameOverride = value;
    }

    /// <summary>Set when the display name differs from the plain file name.</summary>
    private string? displayNameOverride;

    /// <summary>Folder that holds the file, shown in grey under the file name.</summary>
    [JsonIgnore]
    public string FolderPath => HasNoFile ? string.Empty : Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>
    /// False if the file has been moved or deleted (or its drive is not connected).
    /// Checked when the list is refreshed, so missing files can be greyed out.
    /// </summary>
    [JsonIgnore]
    public bool FileExists { get; private set; } = true;

    /// <summary>Re-checks whether the file still exists.</summary>
    public void RefreshFileExists()
    {
        // A query (or a trashed query or bookshelf) has no file, so it is never "missing".
        FileExists = HasNoFile || File.Exists(FilePath);
    }

    /// <summary>True if the file appeared or disappeared since <see cref="RefreshFileExists"/> last looked (never for a query).</summary>
    [JsonIgnore]
    public bool HasFileExistenceChanged => !HasNoFile && FileExists != File.Exists(FilePath);

    /// <summary>True if this entry points at the given path (Windows paths are not case-sensitive).</summary>
    public bool HasPath(string otherFilePath)
    {
        return string.Equals(FilePath, otherFilePath, StringComparison.OrdinalIgnoreCase);
    }
}
