using System.IO;
using System.Text.Json.Serialization;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer.Trash;

/// <summary>What kind of thing was put in the trash.</summary>
public enum TrashItemKind
{
    /// <summary>A Markdown file (from any bookshelf, a folder bookshelf, a post-it board or the archive).</summary>
    File,

    /// <summary>A saved SQL Server query from a query bookshelf.</summary>
    Query,

    /// <summary>A whole bookshelf (its definition and, for normal and query shelves, its entries).</summary>
    Bookshelf,
}

/// <summary>
/// One thing in the trash. Each item has its own folder in the Trash folder, e.g.
/// "Trash\2026-11-11 153012 notes.md\", holding the file itself (for a file) and item.json with this information,
/// so an item can always be put back where it came from, even after the app was moved or reinstalled.
/// </summary>
public class TrashItem
{
    /// <summary>Name of the file in each item folder that describes the item.</summary>
    public const string DescriptionFileName = "item.json";

    /// <summary>File, query or bookshelf (written as a word in item.json, so it is readable).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TrashItemKind>))]
    public TrashItemKind Kind { get; set; }

    /// <summary>The file name (e.g. "notes.md"), the query's name or the bookshelf's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>For a file: the full path it had before it was put in the trash, where Restore puts it back.</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>When it was put in the trash. Items older than the "keep" setting go to the Windows Recycle Bin.</summary>
    public DateTime DeletedOn { get; set; }

    /// <summary>
    /// For a file: the normal bookshelves it was on, so Restore can put it back on them.
    /// For a query: the one query bookshelf it was on.
    /// </summary>
    public List<string> ShelfNames { get; set; } = new();

    /// <summary>For a query: the query itself (its SQL and encrypted connection, exactly as in settings.json).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BookshelfEntry? QueryEntry { get; set; }

    /// <summary>For a bookshelf: the whole shelf, with its files or queries (a folder shelf without its files, which stay in the folder).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NamedBookshelf? Bookshelf { get; set; }

    /// <summary>The item's own folder in the Trash folder. Worked out when the trash is read, not stored.</summary>
    [JsonIgnore]
    public string ItemFolderPath { get; set; } = string.Empty;

    /// <summary>For a file: where the file is now, inside the item's folder. Null for a query or bookshelf.</summary>
    [JsonIgnore]
    public string? StoredFilePath => Kind == TrashItemKind.File ? Path.Combine(ItemFolderPath, Name) : null;

    /// <summary>The name shown in the Trash list: "notes.md", "Query: Open orders" or "Bookshelf: Projects".</summary>
    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        TrashItemKind.Query => $"Query: {Name}",
        TrashItemKind.Bookshelf => $"Bookshelf: {Name}",
        _ => Name,
    };

    /// <summary>Tooltip in the Trash list: when it was deleted and where it came from.</summary>
    [JsonIgnore]
    public string DescriptionText
    {
        get
        {
            string deletedText = $"Deleted {DeletedOn:yyyy-MM-dd HH:mm}";
            return Kind switch
            {
                TrashItemKind.File => $"{deletedText} from {OriginalPath}",
                TrashItemKind.Query => ShelfNames.Count > 0 ? $"{deletedText} from the query bookshelf \"{ShelfNames[0]}\"" : deletedText,
                _ => Bookshelf?.IsFolderShelf == true
                    ? $"{deletedText}. A bookshelf showing the folder {Bookshelf.FolderPath}"
                    : Bookshelf?.Entries.Count == 1
                        ? $"{deletedText}. A bookshelf with 1 entry"
                        : $"{deletedText}. A bookshelf with {Bookshelf?.Entries.Count ?? 0} entries",
            };
        }
    }
}
