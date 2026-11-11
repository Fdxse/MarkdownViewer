using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarkdownViewer.Links;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer.Macros;

/// <summary>
/// User settings (macros, links and the bookshelves), stored as JSON in %APPDATA%\MarkdownViewer\settings.json so they survive
/// updates of the exe. The file can also be edited by hand. Connection strings of saved queries are stored encrypted
/// (see MarkdownViewer.Queries.ConnectionSecrets), so this file never holds a database password as plain text.
/// </summary>
public class AppSettings
{
    /// <summary>Folder that holds the settings file.</summary>
    public static readonly string SettingsFolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkdownViewer");

    /// <summary>Full path of the settings file.</summary>
    public static readonly string SettingsFilePath = Path.Combine(SettingsFolderPath, "settings.json");

    /// <summary>Indented JSON with readable property names, so the file is easy to edit by hand.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Text macros that are expanded while typing in the editor.</summary>
    public List<MacroDefinition> Macros { get; set; } = new();

    /// <summary>Saved name + URL entries shown in the Links menu.</summary>
    public List<SavedLink> Links { get; set; } = new();
    /// <summary>
    /// The bookshelves, each with its own Markdown files from anywhere on disk.
    /// The first one is always the Standard shelf, which is shown when the app starts.
    /// </summary>
    public List<NamedBookshelf> Bookshelves { get; set; } = new();

    /// <summary>
    /// The single bookshelf of older versions. Only read, to move its files to the Standard shelf;
    /// it is null afterwards and then left out of the settings file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BookshelfEntry>? BookshelfEntries { get; set; }

    /// <summary>The Standard shelf (always present after <see cref="Load"/> or <see cref="CreateDefaults"/>).</summary>
    [JsonIgnore]
    public NamedBookshelf StandardBookshelf => Bookshelves.First(bookshelf => bookshelf.IsStandardShelf);

    /// <summary>True if the bookshelf and properties panel is shown (View → Bookshelf, Ctrl+B).</summary>
    public bool IsBookshelfVisible { get; set; } = true;

    /// <summary>Width of the bookshelf panel in pixels, remembered between runs.</summary>
    public double BookshelfPanelWidth { get; set; } = 280;

    /// <summary>
    /// True once the bookshelf is shown in its stored order (which the user arranges by drag and drop).
    /// False for settings from older versions, which always sorted the list by name.
    /// </summary>
    public bool HasCustomBookshelfOrder { get; set; }

    /// <summary>
    /// Heading of the section in todo.md that new tasks are added to (Todo → Add task). Added if missing.
    /// </summary>
    public string TodoSectionHeading { get; set; } = MarkdownViewer.Todo.TodoDocument.DefaultSectionHeading;

    /// <summary>Number of days an item stays in the app's Trash before it is moved on to the Windows Recycle Bin. 0 keeps items forever.</summary>
    public const int DefaultTrashKeepDays = 30;

    /// <summary>
    /// How many days things stay in the app's Trash (next to the exe) before they are moved to the Windows Recycle Bin
    /// at startup. 0 (or less) keeps them in the Trash until it is emptied by hand.
    /// </summary>
    public int TrashKeepDays { get; set; } = DefaultTrashKeepDays;

    /// <summary>Settings used the first time the app runs: one example macro for work item links.</summary>
    public static AppSettings CreateDefaults() => new()
    {
        Bookshelves = { new NamedBookshelf { Name = NamedBookshelf.StandardShelfName } },
        Macros =
        {
            new MacroDefinition
            {
                Trigger = "@{n}",
                Replacement = "[Task {n}](https://devops/dev/_workitem_edit/{n})",
            },
        },
    };

    /// <summary>
    /// Reads the settings file. Returns the defaults if it does not exist yet,
    /// and also if it cannot be read (in that case <paramref name="loadError"/> says why).
    /// </summary>
    public static AppSettings Load(out string? loadError)
    {
        loadError = null;
        if (!File.Exists(SettingsFilePath))
        {
            return CreateDefaults();
        }

        try
        {
            string settingsJson = File.ReadAllText(SettingsFilePath);
            var loadedSettings = JsonSerializer.Deserialize<AppSettings>(settingsJson, JsonOptions) ?? CreateDefaults();
            // A file with "Macros": null should not crash the editor later on.
            loadedSettings.Macros ??= new List<MacroDefinition>();
            // Same for "Links": null (older settings files have no Links entry at all, which is fine too).
            loadedSettings.Links ??= new List<SavedLink>();
            // Older settings files have no todo section yet; an empty value falls back to the default too.
            if (string.IsNullOrWhiteSpace(loadedSettings.TodoSectionHeading))
            {
                loadedSettings.TodoSectionHeading = MarkdownViewer.Todo.TodoDocument.DefaultSectionHeading;
            }
            loadedSettings.RepairBookshelves();
            return loadedSettings;
        }
        catch (Exception readError)
        {
            loadError = $"Could not read the settings file:\n{SettingsFilePath}\n\n{readError.Message}";
            return CreateDefaults();
        }
    }

    /// <summary>
    /// Makes the bookshelves valid whatever the file contained: moves the old single bookshelf to the
    /// Standard shelf, makes sure Standard exists and comes first, drops nameless or duplicate shelves
    /// and entries without a path (e.g. from a hand-edited file).
    /// </summary>
    private void RepairBookshelves()
    {
        Bookshelves ??= new List<NamedBookshelf>();
        Bookshelves.RemoveAll(bookshelf => bookshelf == null || string.IsNullOrWhiteSpace(bookshelf.Name));

        // Keep only the first shelf of each name.
        var seenShelfNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        Bookshelves.RemoveAll(bookshelf => !seenShelfNames.Add(bookshelf.Name.Trim()));

        // Standard always exists and is always first.
        var standardShelf = Bookshelves.FirstOrDefault(bookshelf => bookshelf.IsStandardShelf)
            ?? new NamedBookshelf { Name = NamedBookshelf.StandardShelfName };
        Bookshelves.Remove(standardShelf);
        Bookshelves.Insert(0, standardShelf);

        // Settings from before multiple shelves: their files go onto the Standard shelf.
        if (BookshelfEntries != null)
        {
            foreach (var oldEntry in BookshelfEntries.Where(oldEntry => oldEntry != null && !standardShelf.ContainsFile(oldEntry.FilePath ?? string.Empty)))
            {
                standardShelf.Entries.Add(oldEntry);
            }
            BookshelfEntries = null;
        }

        foreach (var bookshelf in Bookshelves)
        {
            bookshelf.Entries ??= new List<BookshelfEntry>();
            bookshelf.Entries.RemoveAll(bookshelfEntry => bookshelfEntry == null || string.IsNullOrWhiteSpace(bookshelfEntry.FilePath));

            // A shelf is either a folder shelf or a query shelf, never both (the folder wins), and Standard is a normal shelf.
            if (bookshelf.IsFolderShelf || bookshelf.IsStandardShelf)
            {
                bookshelf.IsQueryShelf = false;
            }
            // Only a folder shelf can be shown as a post-it board.
            if (!bookshelf.IsFolderShelf)
            {
                bookshelf.IsPostItBoard = false;
            }
            // Query shelves hold only queries, other shelves only files.
            bookshelf.Entries.RemoveAll(bookshelfEntry => bookshelfEntry.IsQuery != bookshelf.IsQueryShelf);
            if (bookshelf.IsQueryShelf)
            {
                RepairQueryEntries(bookshelf);
            }
        }
    }

    /// <summary>
    /// Makes the queries on a query shelf usable whatever the file contained: every query gets a unique id,
    /// a name, and sensible limits.
    /// </summary>
    private static void RepairQueryEntries(NamedBookshelf queryShelf)
    {
        var seenQueryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var queryEntry in queryShelf.Entries)
        {
            var savedQuery = queryEntry.Query!;
            bool hasUsableId = queryEntry.FilePath.StartsWith(BookshelfEntry.QueryPathPrefix, StringComparison.OrdinalIgnoreCase);
            if (!hasUsableId || !seenQueryIds.Add(queryEntry.FilePath))
            {
                queryEntry.FilePath = BookshelfEntry.QueryPathPrefix + Guid.NewGuid().ToString("N");
                seenQueryIds.Add(queryEntry.FilePath);
            }

            savedQuery.Name = string.IsNullOrWhiteSpace(savedQuery.Name) ? "Query" : savedQuery.Name;
            savedQuery.SqlText ??= string.Empty;
            savedQuery.RowTemplate ??= string.Empty;
            savedQuery.ServerName ??= string.Empty;
            savedQuery.DatabaseName ??= string.Empty;
            if (savedQuery.MaximumRowCount < 1)
            {
                savedQuery.MaximumRowCount = MarkdownViewer.Queries.SavedQuery.DefaultMaximumRowCount;
            }
            if (savedQuery.TimeoutSeconds < 1)
            {
                savedQuery.TimeoutSeconds = MarkdownViewer.Queries.SavedQuery.DefaultTimeoutSeconds;
            }
        }
    }

    /// <summary>The settings as indented JSON, exactly as <see cref="Save"/> writes them (also used by the zip backup).</summary>
    public string ToJson()
    {
        // Folder shelves are saved without their files: those are read from the folder each time.
        // The Archive shelf is added by the app at every start, so it is not saved at all.
        var settingsToWrite = (AppSettings)MemberwiseClone();
        settingsToWrite.Bookshelves = Bookshelves
            .Where(bookshelf => !bookshelf.IsBuiltInShelf)
            .Select(bookshelf => bookshelf.IsFolderShelf ? bookshelf.CloneWithoutEntries() : bookshelf)
            .ToList();
        return JsonSerializer.Serialize(settingsToWrite, JsonOptions);
    }

    /// <summary>Writes the settings file, creating its folder if needed. Throws on failure.</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsFolderPath);
        File.WriteAllText(SettingsFilePath, ToJson());
    }
}
