using System.IO;
using System.Text.Json.Serialization;

namespace MarkdownViewer.Bookshelf;

/// <summary>The shelves the app adds by itself at the end of the drop-down; they are never stored in the settings file.</summary>
public enum BuiltInShelfKind
{
    /// <summary>A shelf the user made (or Standard).</summary>
    None,

    /// <summary>The Archive shelf: a folder shelf showing the Archive folder next to the exe, subfolders included.</summary>
    Archive,

    /// <summary>The Trash shelf: lists what is in the app's own trash, with Restore and Delete.</summary>
    Trash,
}

/// <summary>
/// One bookshelf: a name (e.g. "Standard", "Documentation", "Post-its") and its files.
/// There are two kinds:
/// <list type="bullet">
/// <item>a normal shelf, holding the files the user put on it, in the order the user arranged them;</item>
/// <item>a folder shelf (<see cref="FolderPath"/> set), which always lists the Markdown files in one folder
/// (optionally with its subfolders). Its <see cref="Entries"/> are read from disk, never edited by hand;</item>
/// <item>a query shelf (<see cref="IsQueryShelf"/>), whose entries are saved SQL Server queries. Opening one runs
/// the query and shows the result as a Markdown document.</item>
/// <item>a post-it board (<see cref="IsPostItBoard"/>), which is a folder shelf whose files are also shown as
/// small yellow notes in the document area instead of one document at a time.</item>
/// </list>
/// The same file may be on several shelves.
/// </summary>
public class NamedBookshelf
{
    /// <summary>Name of the shelf that always exists, is shown at startup and cannot be renamed or deleted.</summary>
    public const string StandardShelfName = "Standard";

    /// <summary>Name shown in the shelf drop-down. Unique among the shelves (ignoring upper/lower case).</summary>
    public string Name { get; set; } = StandardShelfName;

    /// <summary>
    /// The files on this shelf, in display order. For a folder shelf this is filled by
    /// <see cref="RescanFolder"/> and not stored in the settings file.
    /// </summary>
    public List<BookshelfEntry> Entries { get; set; } = new();

    /// <summary>File extensions a folder shelf lists. Plain .txt files are left out on purpose.</summary>
    public static readonly string[] FolderShelfFileExtensions = { ".md", ".markdown", ".mdown", ".mkd", ".mkdn" };

    /// <summary>Most files a folder shelf lists, so picking a huge folder (e.g. a whole drive with subfolders) cannot freeze the app.</summary>
    public const int MaximumFolderShelfFileCount = 5000;

    /// <summary>For a folder shelf: the folder whose Markdown files are listed. Null for a normal shelf.</summary>
    // Left out of the settings file for normal shelves.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? FolderPath { get; set; }

    /// <summary>For a folder shelf: true to list the Markdown files in all subfolders too.</summary>
    // Left out of the settings file for normal shelves.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeSubfolders { get; set; }

    /// <summary>True for a query bookshelf: its entries are saved SQL Server queries instead of files.</summary>
    // Left out of the settings file for the other kinds of shelf.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsQueryShelf { get; set; }

    /// <summary>
    /// For a folder shelf: true to show its files as a board of yellow post-it notes in the document area.
    /// Only meaningful together with <see cref="FolderPath"/>.
    /// </summary>
    // Left out of the settings file for the other kinds of shelf.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsPostItBoard { get; set; }

    /// <summary>
    /// For a post-it board: the order the user dragged the notes into, as the files' display names
    /// ("notes.md", or "subfolder\notes.md"). Files not in this list (e.g. new ones) come after the
    /// listed ones, sorted by name. Null or empty means plain A-Z order.
    /// </summary>
    // Left out of the settings file until the user rearranges a board.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string>? PostItNoteOrder { get; set; }

    /// <summary>
    /// For a post-it board: where each note lies on the board, how much it is tilted and how high it is in
    /// the stack, by the file's display name. Notes without an entry are laid out in rows in free spots.
    /// Null until the user moves or tilts a note.
    /// </summary>
    // Left out of the settings file until the user moves a note.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Dictionary<string, PostItNotePlacement>? PostItNotePlacements { get; set; }

    /// <summary>True if this shelf lists a folder instead of files the user added.</summary>
    [JsonIgnore]
    public bool IsFolderShelf => !string.IsNullOrWhiteSpace(FolderPath);

    /// <summary>For a folder shelf: true if the scan stopped at <see cref="MaximumFolderShelfFileCount"/> files.</summary>
    [JsonIgnore]
    public bool WasFolderListCutShort { get; private set; }

    /// <summary>True for the Standard shelf. Worked out from the name, so not stored in the settings file.</summary>
    [JsonIgnore]
    public bool IsStandardShelf => HasName(StandardShelfName);

    /// <summary>Archive or Trash for the shelves the app adds by itself; None for every other shelf. Not stored.</summary>
    [JsonIgnore]
    public BuiltInShelfKind BuiltInKind { get; init; }

    /// <summary>True for the Archive shelf.</summary>
    [JsonIgnore]
    public bool IsArchiveShelf => BuiltInKind == BuiltInShelfKind.Archive;

    /// <summary>True for the Trash shelf.</summary>
    [JsonIgnore]
    public bool IsTrashShelf => BuiltInKind == BuiltInShelfKind.Trash;

    /// <summary>True for Archive and Trash, which cannot be renamed, deleted or saved in the settings file.</summary>
    [JsonIgnore]
    public bool IsBuiltInShelf => BuiltInKind != BuiltInShelfKind.None;

    /// <summary>
    /// True if files can be added, removed, sorted and reordered by hand. Not on a folder shelf (its files come from
    /// the folder, which includes the Archive shelf) and not in the Trash (its entries come from the Trash folder).
    /// </summary>
    [JsonIgnore]
    public bool CanEditEntriesByHand => !IsFolderShelf && !IsTrashShelf;

    /// <summary>True if this shelf has the given name (ignoring upper/lower case).</summary>
    public bool HasName(string otherShelfName)
    {
        return string.Equals(Name, otherShelfName.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// True if the given file is on this shelf. For a folder shelf this is worked out from the path
    /// (inside the folder, a Markdown file, and directly in it unless subfolders are included), so it is
    /// right even when the folder has not been scanned recently.
    /// </summary>
    public bool ContainsFile(string filePath)
    {
        if (!IsFolderShelf)
        {
            return Entries.Any(bookshelfEntry => bookshelfEntry.HasPath(filePath));
        }

        if (!HasFolderShelfExtension(filePath))
        {
            return false;
        }

        string shelfFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FolderPath!));
        string fileFolder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty);
        if (string.Equals(fileFolder, shelfFolder, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // In a subfolder: the file's folder starts with "<shelf folder>\".
        return IncludeSubfolders
            && fileFolder.StartsWith(shelfFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True if the file has one of the extensions a folder shelf lists.</summary>
    public static bool HasFolderShelfExtension(string filePath)
    {
        return FolderShelfFileExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// For a folder shelf: reads the Markdown files from disk into <see cref="Entries"/>, sorted by their
    /// path inside the folder. Files in subfolders are shown as "subfolder\file.md". Hidden files and
    /// folders that cannot be read are skipped. Returns false if the folder does not exist (Entries is then empty).
    /// </summary>
    public bool RescanFolder()
    {
        Entries = new List<BookshelfEntry>();
        WasFolderListCutShort = false;
        if (!IsFolderShelf || !Directory.Exists(FolderPath))
        {
            return false;
        }

        var scanOptions = new EnumerationOptions
        {
            RecurseSubdirectories = IncludeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        string shelfFolder = Path.GetFullPath(FolderPath!);
        try
        {
            foreach (string foundFilePath in Directory.EnumerateFiles(shelfFolder, "*", scanOptions))
            {
                if (!HasFolderShelfExtension(foundFilePath))
                {
                    continue;
                }

                if (Entries.Count >= MaximumFolderShelfFileCount)
                {
                    WasFolderListCutShort = true;
                    break;
                }

                Entries.Add(new BookshelfEntry
                {
                    FilePath = foundFilePath,
                    // "notes.md" for files in the folder itself, "2025\notes.md" for files in subfolders.
                    DisplayName = Path.GetRelativePath(shelfFolder, foundFilePath),
                });
            }
        }
        catch (Exception)
        {
            // The folder disappeared or became unreadable while scanning: show what was found so far.
        }

        Entries.Sort((firstEntry, secondEntry) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(firstEntry.DisplayName, secondEntry.DisplayName));
        ApplyPostItNoteOrder();
        return true;
    }

    /// <summary>
    /// For a post-it board with a saved <see cref="PostItNoteOrder"/>: puts <see cref="Entries"/> in that order.
    /// Files that are not in the saved order keep their A-Z order and go after the ones that are.
    /// </summary>
    public void ApplyPostItNoteOrder()
    {
        if (!IsPostItBoard || PostItNoteOrder == null || PostItNoteOrder.Count == 0)
        {
            return;
        }

        // Position of each name in the saved order (the first one wins if a name is somehow listed twice).
        var savedPositionByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int savedPosition = 0; savedPosition < PostItNoteOrder.Count; savedPosition++)
        {
            savedPositionByName.TryAdd(PostItNoteOrder[savedPosition], savedPosition);
        }

        // OrderBy keeps the existing (A-Z) order among files with the same key, i.e. among the unlisted ones at the end.
        Entries = Entries
            .OrderBy(bookshelfEntry => savedPositionByName.TryGetValue(bookshelfEntry.DisplayName, out int savedPosition)
                ? savedPosition
                : int.MaxValue)
            .ToList();
    }

    /// <summary>A copy of this shelf without its files, used when saving a folder shelf (its files are read from disk instead).</summary>
    public NamedBookshelf CloneWithoutEntries() => new()
    {
        Name = Name,
        FolderPath = FolderPath,
        IncludeSubfolders = IncludeSubfolders,
        IsPostItBoard = IsPostItBoard,
        PostItNoteOrder = PostItNoteOrder,
        PostItNotePlacements = PostItNotePlacements,
    };

    /// <summary>The shelf name, which is what the drop-down shows (folder, post-it and query shelves are marked as such).</summary>
    public override string ToString()
    {
        // The built-in shelves show their plain name; the Trash also says how much is in it.
        if (IsArchiveShelf)
        {
            return Name;
        }
        if (IsTrashShelf)
        {
            return Entries.Count > 0 ? $"{Name} ({Entries.Count})" : Name;
        }
        if (IsFolderShelf)
        {
            return IsPostItBoard ? $"{Name} (post-its)" : $"{Name} (folder)";
        }
        return IsQueryShelf ? $"{Name} (queries)" : Name;
    }
}
