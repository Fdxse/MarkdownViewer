using System.IO;
using System.Text.Json.Serialization;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// One Markdown file on the bookshelf. Only the path and the date it was added are stored in
/// the settings file; the other properties are worked out from the path when displayed.
/// </summary>
public class BookshelfEntry
{
    /// <summary>Full path of the Markdown file. The file can be anywhere on disk or on a network share.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>When the file was put on the bookshelf.</summary>
    public DateTime AddedOn { get; set; }

    /// <summary>File name shown in the list, e.g. "README.md".</summary>
    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);

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
    public string FolderPath => Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>
    /// False if the file has been moved or deleted (or its drive is not connected).
    /// Checked when the list is refreshed, so missing files can be greyed out.
    /// </summary>
    [JsonIgnore]
    public bool FileExists { get; private set; } = true;

    /// <summary>Re-checks whether the file still exists.</summary>
    public void RefreshFileExists()
    {
        FileExists = File.Exists(FilePath);
    }

    /// <summary>True if this entry points at the given path (Windows paths are not case-sensitive).</summary>
    public bool HasPath(string otherFilePath)
    {
        return string.Equals(FilePath, otherFilePath, StringComparison.OrdinalIgnoreCase);
    }
}
