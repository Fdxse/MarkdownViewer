using System.IO;
using System.Text.Json.Serialization;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// One bookshelf: a name (e.g. "Standard", "Documentation", "Post-its") and its files.
/// There are two kinds:
/// <list type="bullet">
/// <item>a normal shelf, holding the files the user put on it, in the order the user arranged them;</item>
/// <item>a folder shelf (<see cref="FolderPath"/> set), which always lists the Markdown files in one folder
/// (optionally with its subfolders). Its <see cref="Entries"/> are read from disk, never edited by hand.</item>
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

    /// <summary>True if this shelf lists a folder instead of files the user added.</summary>
    [JsonIgnore]
    public bool IsFolderShelf => !string.IsNullOrWhiteSpace(FolderPath);

    /// <summary>For a folder shelf: true if the scan stopped at <see cref="MaximumFolderShelfFileCount"/> files.</summary>
    [JsonIgnore]
    public bool WasFolderListCutShort { get; private set; }

    /// <summary>True for the Standard shelf. Worked out from the name, so not stored in the settings file.</summary>
    [JsonIgnore]
    public bool IsStandardShelf => HasName(StandardShelfName);

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
        return true;
    }

    /// <summary>A copy of this shelf without its files, used when saving a folder shelf (its files are read from disk instead).</summary>
    public NamedBookshelf CloneWithoutEntries() => new()
    {
        Name = Name,
        FolderPath = FolderPath,
        IncludeSubfolders = IncludeSubfolders,
    };

    /// <summary>The shelf name, which is what the drop-down shows (folder shelves are marked as such).</summary>
    public override string ToString() => IsFolderShelf ? $"{Name} (folder)" : Name;
}
