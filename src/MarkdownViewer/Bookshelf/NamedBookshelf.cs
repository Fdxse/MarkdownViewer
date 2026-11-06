using System.Text.Json.Serialization;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// One bookshelf: a name (e.g. "Standard", "Documentation", "Post-its") and its files, in the
/// order the user arranged them. The same file may be on several shelves.
/// </summary>
public class NamedBookshelf
{
    /// <summary>Name of the shelf that always exists, is shown at startup and cannot be renamed or deleted.</summary>
    public const string StandardShelfName = "Standard";

    /// <summary>Name shown in the shelf drop-down. Unique among the shelves (ignoring upper/lower case).</summary>
    public string Name { get; set; } = StandardShelfName;

    /// <summary>The files on this shelf, in display order.</summary>
    public List<BookshelfEntry> Entries { get; set; } = new();

    /// <summary>True for the Standard shelf. Worked out from the name, so not stored in the settings file.</summary>
    [JsonIgnore]
    public bool IsStandardShelf => HasName(StandardShelfName);

    /// <summary>True if this shelf has the given name (ignoring upper/lower case).</summary>
    public bool HasName(string otherShelfName)
    {
        return string.Equals(Name, otherShelfName.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>True if the given file is on this shelf.</summary>
    public bool ContainsFile(string filePath)
    {
        return Entries.Any(bookshelfEntry => bookshelfEntry.HasPath(filePath));
    }

    /// <summary>The shelf name, which is what the drop-down shows.</summary>
    public override string ToString() => Name;
}
