using System.IO;
using System.Text.Json;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Storage;

namespace MarkdownViewer.Trash;

/// <summary>
/// The app's own trash: the "Trash" folder next to the exe. Files moved to the trash are moved into it (so they no
/// longer show up anywhere else), and queries and bookshelves are written into it as JSON. Everything can be put
/// back with <see cref="RestoreFile"/> and friends. Items older than a number of days are moved on to the Windows
/// Recycle Bin by <see cref="MoveExpiredItemsToRecycleBin"/>.
/// <para>
/// Each item has its own folder (see <see cref="TrashItem"/>); there is no central list that could get out of step.
/// The folder is read again by <see cref="Reload"/>, so items the user deletes by hand in Explorer simply disappear.
/// </para>
/// </summary>
public class TrashBin
{
    /// <summary>Name of the trash folder next to the exe.</summary>
    public const string FolderName = "Trash";

    /// <summary>Indented JSON, so item.json is readable if someone ever needs to restore an item by hand.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Full path of the trash folder.</summary>
    public string TrashFolderPath { get; }

    /// <summary>The items in the trash, newest first. Filled by <see cref="Reload"/>.</summary>
    public List<TrashItem> Items { get; private set; } = new();

    /// <summary>Creates the trash for the given folder and reads what is in it.</summary>
    public TrashBin(string trashFolderPath)
    {
        TrashFolderPath = trashFolderPath;
        Reload();
    }

    /// <summary>Reads every item folder in the trash folder. Folders without a readable item.json are skipped.</summary>
    public void Reload()
    {
        var foundItems = new List<TrashItem>();
        try
        {
            if (Directory.Exists(TrashFolderPath))
            {
                foreach (string itemFolderPath in Directory.EnumerateDirectories(TrashFolderPath))
                {
                    var trashItem = TryReadItem(itemFolderPath);
                    if (trashItem != null)
                    {
                        foundItems.Add(trashItem);
                    }
                }
            }
        }
        catch (Exception)
        {
            // An unreadable trash folder just looks empty.
        }

        Items = foundItems.OrderByDescending(trashItem => trashItem.DeletedOn).ToList();
    }

    /// <summary>Reads one item folder's item.json; null if it is missing or broken.</summary>
    private static TrashItem? TryReadItem(string itemFolderPath)
    {
        try
        {
            string descriptionJson = File.ReadAllText(Path.Combine(itemFolderPath, TrashItem.DescriptionFileName));
            var trashItem = JsonSerializer.Deserialize<TrashItem>(descriptionJson, JsonOptions);
            if (trashItem == null)
            {
                return null;
            }

            trashItem.ItemFolderPath = itemFolderPath;
            trashItem.ShelfNames ??= new List<string>();
            return trashItem;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Putting things in the trash
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves a file into the trash. <paramref name="shelfNames"/> are the normal bookshelves it was on.
    /// Throws if the file cannot be moved (it is then left where it was).
    /// </summary>
    public TrashItem MoveFileToTrash(string filePath, IEnumerable<string> shelfNames)
    {
        string fullFilePath = Path.GetFullPath(filePath);
        var trashItem = new TrashItem
        {
            Kind = TrashItemKind.File,
            Name = Path.GetFileName(fullFilePath),
            OriginalPath = fullFilePath,
            DeletedOn = DateTime.Now,
            ShelfNames = shelfNames.ToList(),
        };

        CreateItemFolder(trashItem);
        try
        {
            // File.Move also works between drives (it copies and then deletes the original).
            File.Move(fullFilePath, trashItem.StoredFilePath!);
            WriteDescription(trashItem);
        }
        catch (Exception)
        {
            // Put the file back if it was already moved, and remove the half-made item folder.
            if (File.Exists(trashItem.StoredFilePath) && !File.Exists(fullFilePath))
            {
                TryMoveFile(trashItem.StoredFilePath!, fullFilePath);
            }
            TryDeleteFolder(trashItem.ItemFolderPath);
            throw;
        }

        Items.Insert(0, trashItem);
        return trashItem;
    }

    /// <summary>Puts a saved query in the trash (written to its item.json). Throws if it cannot be written.</summary>
    public TrashItem AddQuery(BookshelfEntry queryEntry, string queryShelfName)
    {
        var trashItem = new TrashItem
        {
            Kind = TrashItemKind.Query,
            Name = queryEntry.Query?.Name ?? "Query",
            DeletedOn = DateTime.Now,
            ShelfNames = new List<string> { queryShelfName },
            QueryEntry = queryEntry,
        };
        CreateItemFolder(trashItem);
        WriteDescriptionOrRemoveFolder(trashItem);
        Items.Insert(0, trashItem);
        return trashItem;
    }

    /// <summary>
    /// Puts a whole bookshelf in the trash (written to its item.json). The files on a normal shelf are not touched:
    /// only the list of them goes to the trash. Throws if it cannot be written.
    /// </summary>
    public TrashItem AddBookshelf(NamedBookshelf bookshelf)
    {
        var trashItem = new TrashItem
        {
            Kind = TrashItemKind.Bookshelf,
            Name = bookshelf.Name,
            DeletedOn = DateTime.Now,
            // A folder shelf's files are read from its folder, so only its settings are kept.
            Bookshelf = bookshelf.IsFolderShelf ? bookshelf.CloneWithoutEntries() : bookshelf,
        };
        CreateItemFolder(trashItem);
        WriteDescriptionOrRemoveFolder(trashItem);
        Items.Insert(0, trashItem);
        return trashItem;
    }

    /// <summary>
    /// Makes a new, unused folder for the item, named after the time and the item so it is recognisable in
    /// Explorer and in the Windows Recycle Bin, e.g. "2026-11-11 153012 notes.md".
    /// </summary>
    private void CreateItemFolder(TrashItem trashItem)
    {
        Directory.CreateDirectory(TrashFolderPath);
        string folderName = StorageFolders.MakeSafeName($"{trashItem.DeletedOn:yyyy-MM-dd HHmmss} {trashItem.DisplayName}", "item");
        string itemFolderPath = Path.Combine(TrashFolderPath, folderName);
        int copyNumber = 2;
        while (Directory.Exists(itemFolderPath) || File.Exists(itemFolderPath))
        {
            itemFolderPath = Path.Combine(TrashFolderPath, $"{folderName} ({copyNumber})");
            copyNumber++;
        }

        Directory.CreateDirectory(itemFolderPath);
        trashItem.ItemFolderPath = itemFolderPath;
    }

    /// <summary>Writes the item's item.json.</summary>
    private static void WriteDescription(TrashItem trashItem)
    {
        File.WriteAllText(Path.Combine(trashItem.ItemFolderPath, TrashItem.DescriptionFileName), JsonSerializer.Serialize(trashItem, JsonOptions));
    }

    /// <summary>Writes the item's item.json; if that fails, removes the empty item folder again and throws.</summary>
    private static void WriteDescriptionOrRemoveFolder(TrashItem trashItem)
    {
        try
        {
            WriteDescription(trashItem);
        }
        catch (Exception)
        {
            TryDeleteFolder(trashItem.ItemFolderPath);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Taking things out again
    // ------------------------------------------------------------------

    /// <summary>
    /// Moves a trashed file to <paramref name="targetFilePath"/> (normally its original path; the caller decides what
    /// to do if a file is already there) and removes the item from the trash. Creates the folder if it is gone.
    /// Throws if the file cannot be moved.
    /// </summary>
    public void RestoreFile(TrashItem trashItem, string targetFilePath)
    {
        string? targetFolderPath = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(targetFolderPath))
        {
            Directory.CreateDirectory(targetFolderPath);
        }

        File.Move(trashItem.StoredFilePath!, targetFilePath);
        Forget(trashItem);
    }

    /// <summary>
    /// Removes an item's folder from the trash, e.g. after a query or bookshelf was restored into the settings.
    /// A folder that cannot be removed is left behind (it would only show up again after a restart).
    /// </summary>
    public void Forget(TrashItem trashItem)
    {
        TryDeleteFolder(trashItem.ItemFolderPath);
        Items.Remove(trashItem);
    }

    /// <summary>Deletes an item for good (not to the Recycle Bin). Throws if its folder cannot be deleted.</summary>
    public void DeleteForGood(TrashItem trashItem)
    {
        if (Directory.Exists(trashItem.ItemFolderPath))
        {
            Directory.Delete(trashItem.ItemFolderPath, recursive: true);
        }
        Items.Remove(trashItem);
    }

    /// <summary>
    /// Moves every item that has been in the trash longer than <paramref name="keepDays"/> days to the Windows Recycle
    /// Bin (its whole folder, so item.json goes along and says where it came from). 0 or less keeps items forever.
    /// Items that cannot be moved stay in the trash and are tried again next time. Returns how many were moved.
    /// </summary>
    public int MoveExpiredItemsToRecycleBin(int keepDays)
    {
        if (keepDays <= 0)
        {
            return 0;
        }

        DateTime oldestKeptDate = DateTime.Now.AddDays(-keepDays);
        int movedItemCount = 0;
        foreach (var expiredItem in Items.Where(trashItem => trashItem.DeletedOn < oldestKeptDate).ToList())
        {
            try
            {
                WindowsRecycleBin.Send(expiredItem.ItemFolderPath);
                Items.Remove(expiredItem);
                movedItemCount++;
            }
            catch (Exception)
            {
                // Try again at the next start.
            }
        }
        return movedItemCount;
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------

    /// <summary>Deletes a folder with everything in it, ignoring errors.</summary>
    private static void TryDeleteFolder(string folderPath)
    {
        try
        {
            if (Directory.Exists(folderPath))
            {
                Directory.Delete(folderPath, recursive: true);
            }
        }
        catch (Exception)
        {
            // Nothing more to do: the folder is just left behind.
        }
    }

    /// <summary>Moves a file, ignoring errors (used only to undo a half-finished move).</summary>
    private static void TryMoveFile(string sourceFilePath, string targetFilePath)
    {
        try
        {
            File.Move(sourceFilePath, targetFilePath);
        }
        catch (Exception)
        {
            // The file stays in the trash folder, where the user can still find it.
        }
    }
}
