using System.IO;

namespace MarkdownViewer.Storage;

/// <summary>
/// Where the app keeps its own folders (Trash and Archive): next to the exe, like todo.md and search-index.json,
/// so they travel with the exe. If the exe's folder cannot be written to (e.g. the exe was put in Program Files),
/// they go to %APPDATA%\MarkdownViewer next to settings.json instead.
/// </summary>
public static class StorageFolders
{
    /// <summary>
    /// The exe's folder. Environment.ProcessPath is the real .exe even for a single-file app;
    /// the base directory is only a fallback.
    /// </summary>
    public static string ExeFolderPath => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Full path of a folder with the given name next to the exe, or in %APPDATA%\MarkdownViewer if the exe's
    /// folder is read-only. The folder is created if it does not exist yet.
    /// </summary>
    public static string ChooseFolder(string folderName)
    {
        string besideExeFolderPath = Path.Combine(ExeFolderPath, folderName);
        if (CanWriteInFolder(besideExeFolderPath))
        {
            return besideExeFolderPath;
        }

        string appDataFolderPath = Path.Combine(MarkdownViewer.Macros.AppSettings.SettingsFolderPath, folderName);
        try
        {
            Directory.CreateDirectory(appDataFolderPath);
        }
        catch (Exception)
        {
            // Reported later, when something is actually moved into the folder.
        }
        return appDataFolderPath;
    }

    /// <summary>True if the folder exists or can be created, and a file can be written in it.</summary>
    private static bool CanWriteInFolder(string folderPath)
    {
        try
        {
            Directory.CreateDirectory(folderPath);

            // Try to create (and immediately delete) a small file in it.
            string probeFilePath = Path.Combine(folderPath, ".write-test");
            File.WriteAllText(probeFilePath, string.Empty);
            File.Delete(probeFilePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Turns any text (a file or shelf name) into a name Windows accepts for a file or folder: characters that are
    /// not allowed become "-", and a trailing dot or space is dropped. An empty result falls back to <paramref name="fallbackName"/>.
    /// </summary>
    public static string MakeSafeName(string wantedName, string fallbackName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        string safeName = new string(wantedName.Trim()
            .Select(nameCharacter => invalidCharacters.Contains(nameCharacter) ? '-' : nameCharacter)
            .ToArray())
            .TrimEnd('.', ' ');
        return safeName.Length == 0 ? fallbackName : safeName;
    }

    /// <summary>
    /// A path in the folder for the given file name that no file uses yet: "notes.md", else "notes (2).md",
    /// "notes (3).md"... so moving a file there never overwrites another one.
    /// </summary>
    public static string GetUnusedFilePath(string folderPath, string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string fileExtension = Path.GetExtension(fileName);
        string candidatePath = Path.Combine(folderPath, fileName);
        int copyNumber = 2;
        while (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            candidatePath = Path.Combine(folderPath, $"{baseName} ({copyNumber}){fileExtension}");
            copyNumber++;
        }
        return candidatePath;
    }
}
