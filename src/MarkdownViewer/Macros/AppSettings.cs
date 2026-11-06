using System.IO;
using System.Text.Json;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer.Macros;

/// <summary>
/// User settings (macros and the bookshelf), stored as JSON in %APPDATA%\MarkdownViewer\settings.json so they survive
/// updates of the exe. The file can also be edited by hand.
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

    /// <summary>Markdown files collected on the bookshelf, from anywhere on disk.</summary>
    public List<BookshelfEntry> BookshelfEntries { get; set; } = new();

    /// <summary>True if the bookshelf and properties panel is shown (View → Bookshelf, Ctrl+B).</summary>
    public bool IsBookshelfVisible { get; set; } = true;

    /// <summary>Width of the bookshelf panel in pixels, remembered between runs.</summary>
    public double BookshelfPanelWidth { get; set; } = 280;

    /// <summary>Settings used the first time the app runs: one example macro for work item links.</summary>
    public static AppSettings CreateDefaults() => new()
    {
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
            loadedSettings.BookshelfEntries ??= new List<BookshelfEntry>();
            // Drop entries without a path, e.g. from a hand-edited file.
            loadedSettings.BookshelfEntries.RemoveAll(bookshelfEntry => bookshelfEntry == null || string.IsNullOrWhiteSpace(bookshelfEntry.FilePath));
            return loadedSettings;
        }
        catch (Exception readError)
        {
            loadError = $"Could not read the settings file:\n{SettingsFilePath}\n\n{readError.Message}";
            return CreateDefaults();
        }
    }

    /// <summary>Writes the settings file, creating its folder if needed. Throws on failure.</summary>
    public void Save()
    {
        Directory.CreateDirectory(SettingsFolderPath);
        string settingsJson = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsFilePath, settingsJson);
    }
}
