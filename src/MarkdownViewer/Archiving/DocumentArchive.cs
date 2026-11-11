using System.IO;
using System.Text.Json;
using MarkdownViewer.Storage;

namespace MarkdownViewer.Archiving;

/// <summary>Where an archived file came from, so it can be put back. Stored in archive.json in the Archive folder.</summary>
public class ArchiveRecord
{
    /// <summary>Where the file is in the Archive folder, relative to it, e.g. "Standard\notes.md".</summary>
    public string ArchivedRelativePath { get; set; } = string.Empty;

    /// <summary>The full path the file had before it was archived; Restore puts it back there.</summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>The normal bookshelves the file was on, so Restore can put it back on them.</summary>
    public List<string> ShelfNames { get; set; } = new();

    /// <summary>When it was archived.</summary>
    public DateTime ArchivedOn { get; set; }
}

/// <summary>
/// The archive: the "Archive" folder next to the exe, for documents that are finished but worth keeping. Archiving a
/// file moves it into a subfolder named after the bookshelf it came from (e.g. "Archive\Projects\notes.md"), so it
/// leaves the everyday shelves and boards but can still be read and searched. The done tasks of the todo list are
/// archived into "Archive\todo-archive.md".
/// <para>
/// archive.json in the folder remembers where each file came from. If a file is moved around by hand in Explorer, its
/// record no longer matches, and Restore simply asks where to put it.
/// </para>
/// </summary>
public class DocumentArchive
{
    /// <summary>Name of the archive folder next to the exe (also the name of the Archive bookshelf).</summary>
    public const string FolderName = "Archive";

    /// <summary>Name of the file that remembers where archived files came from.</summary>
    public const string RecordsFileName = "archive.json";

    /// <summary>Name of the file in the archive folder that receives the done tasks of the todo list.</summary>
    public const string TodoArchiveFileName = "todo-archive.md";

    /// <summary>Indented JSON, so archive.json is readable by hand.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Full path of the archive folder.</summary>
    public string ArchiveFolderPath { get; }

    /// <summary>Full path of todo-archive.md.</summary>
    public string TodoArchiveFilePath => Path.Combine(ArchiveFolderPath, TodoArchiveFileName);

    /// <summary>Creates the archive for the given folder.</summary>
    public DocumentArchive(string archiveFolderPath)
    {
        ArchiveFolderPath = archiveFolderPath;
    }

    /// <summary>True if the file is somewhere inside the archive folder.</summary>
    public bool ContainsFile(string filePath)
    {
        string archiveFolderWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ArchiveFolderPath)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(filePath).StartsWith(archiveFolderWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves a file into the archive, into the subfolder of the bookshelf it came from, and remembers where it was.
    /// A file with the same name already there is never overwritten ("notes (2).md" is used instead).
    /// Returns the file's new path. Throws if the file cannot be moved (it is then left where it was).
    /// </summary>
    /// <param name="filePath">The file to archive.</param>
    /// <param name="fromShelfName">The bookshelf it is archived from; names the subfolder.</param>
    /// <param name="shelfNames">The normal bookshelves it is on, to put it back on when it is restored.</param>
    public string ArchiveFile(string filePath, string fromShelfName, IEnumerable<string> shelfNames)
    {
        string fullFilePath = Path.GetFullPath(filePath);
        string shelfFolderPath = Path.Combine(ArchiveFolderPath, StorageFolders.MakeSafeName(fromShelfName, "Other"));
        Directory.CreateDirectory(shelfFolderPath);

        string archivedFilePath = StorageFolders.GetUnusedFilePath(shelfFolderPath, Path.GetFileName(fullFilePath));
        File.Move(fullFilePath, archivedFilePath);

        // Remember where it came from. If that cannot be saved the file is still archived; Restore then asks where to put it.
        try
        {
            var archiveRecords = ReadRecords();
            string relativePath = Path.GetRelativePath(ArchiveFolderPath, archivedFilePath);
            archiveRecords.RemoveAll(archiveRecord => string.Equals(archiveRecord.ArchivedRelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            archiveRecords.Add(new ArchiveRecord
            {
                ArchivedRelativePath = relativePath,
                OriginalPath = fullFilePath,
                ShelfNames = shelfNames.ToList(),
                ArchivedOn = DateTime.Now,
            });
            WriteRecords(archiveRecords);
        }
        catch (Exception)
        {
            // See above: not worth failing the archiving for.
        }
        return archivedFilePath;
    }

    /// <summary>Where an archived file came from, or null if that is not known (e.g. it was put in the folder by hand).</summary>
    public ArchiveRecord? FindRecord(string archivedFilePath)
    {
        string relativePath = Path.GetRelativePath(ArchiveFolderPath, Path.GetFullPath(archivedFilePath));
        return ReadRecords().FirstOrDefault(archiveRecord =>
            string.Equals(archiveRecord.ArchivedRelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Moves an archived file out of the archive to <paramref name="targetFilePath"/> (the caller has already decided
    /// what to do if a file is there) and forgets its record. Creates the target folder if it is gone.
    /// Throws if the file cannot be moved.
    /// </summary>
    public void RestoreFile(string archivedFilePath, string targetFilePath)
    {
        string? targetFolderPath = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetFolderPath))
        {
            Directory.CreateDirectory(targetFolderPath);
        }
        File.Move(archivedFilePath, targetFilePath);

        try
        {
            string relativePath = Path.GetRelativePath(ArchiveFolderPath, Path.GetFullPath(archivedFilePath));
            var archiveRecords = ReadRecords();
            if (archiveRecords.RemoveAll(archiveRecord => string.Equals(archiveRecord.ArchivedRelativePath, relativePath, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                WriteRecords(archiveRecords);
            }
        }
        catch (Exception)
        {
            // A stale record does no harm: it is replaced if another file ever gets the same name.
        }
    }

    /// <summary>Reads archive.json; an empty list if it does not exist or cannot be read.</summary>
    private List<ArchiveRecord> ReadRecords()
    {
        try
        {
            string recordsFilePath = Path.Combine(ArchiveFolderPath, RecordsFileName);
            if (!File.Exists(recordsFilePath))
            {
                return new List<ArchiveRecord>();
            }
            return JsonSerializer.Deserialize<List<ArchiveRecord>>(File.ReadAllText(recordsFilePath), JsonOptions) ?? new List<ArchiveRecord>();
        }
        catch (Exception)
        {
            return new List<ArchiveRecord>();
        }
    }

    /// <summary>Writes archive.json.</summary>
    private void WriteRecords(List<ArchiveRecord> archiveRecords)
    {
        Directory.CreateDirectory(ArchiveFolderPath);
        File.WriteAllText(Path.Combine(ArchiveFolderPath, RecordsFileName), JsonSerializer.Serialize(archiveRecords, JsonOptions));
    }
}
