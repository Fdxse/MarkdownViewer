using System.IO;
using System.IO.Compression;
using System.Text;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// Creates a "zip all" backup: one .zip file holding every Markdown file on every bookshelf plus the
/// settings file, so the whole setup can be moved to another computer by unzipping it there.
/// Uses only the built-in System.IO.Compression (no extra packages).
///
/// Layout inside the zip:
/// <code>
///   settings.json                      the application settings (macros, links, bookshelf)
///   backup-contents.txt                which original path each file came from, and what was missing
///   files/C/Notes/README.md            C:\Notes\README.md (drive letter becomes the first folder)
///   files/UNC/server/share/Plan.md     \\server\share\Plan.md (network shares go under UNC)
/// </code>
/// Keeping the original folder structure means two files with the same name from different
/// folders (e.g. several README.md) never overwrite each other.
/// </summary>
public static class BookshelfBackup
{
    /// <summary>Name of the settings file inside the zip.</summary>
    public const string SettingsEntryName = "settings.json";

    /// <summary>Name of the text file inside the zip that lists where every file came from.</summary>
    public const string ContentsListEntryName = "backup-contents.txt";

    /// <summary>Folder inside the zip that holds the Markdown files.</summary>
    public const string FilesFolderName = "files";

    /// <summary>Result of a backup, shown to the user afterwards.</summary>
    /// <param name="ZipFilePath">Full path of the zip file that was written.</param>
    /// <param name="BackedUpFileCount">Number of Markdown files that were put in the zip.</param>
    /// <param name="SkippedFiles">Bookshelf files that could not be included, with the reason for each.</param>
    public record BackupResult(string ZipFilePath, int BackedUpFileCount, IReadOnlyList<string> SkippedFiles);

    /// <summary>
    /// Builds a file name like "MarkdownViewer-backup-2026-11-06_093512.zip" for the given moment.
    /// Seconds are included so two backups made within the same minute do not collide.
    /// </summary>
    public static string CreateBackupFileName(DateTime backupTime)
    {
        return $"MarkdownViewer-backup-{backupTime:yyyy-MM-dd_HHmmss}.zip";
    }

    /// <summary>
    /// Turns an absolute Windows path into a relative path inside the zip that keeps the folder structure:
    /// "C:\Notes\a.md" → "files/C/Notes/a.md" and "\\server\share\a.md" → "files/UNC/server/share/a.md".
    /// Zip entries always use forward slashes.
    /// </summary>
    public static string GetEntryNameForFile(string originalFilePath)
    {
        // Normalise both kinds of separators so the result is the same whichever one the path uses.
        string normalisedPath = originalFilePath.Replace('\\', '/');

        string pathWithoutRoot;
        if (normalisedPath.StartsWith("//"))
        {
            // Network share: \\server\share\folder\file.md
            pathWithoutRoot = "UNC/" + normalisedPath.TrimStart('/');
        }
        else if (normalisedPath.Length >= 2 && normalisedPath[1] == ':')
        {
            // Drive path: C:\folder\file.md → C/folder/file.md
            pathWithoutRoot = normalisedPath[0] + "/" + normalisedPath[2..].TrimStart('/');
        }
        else
        {
            // Anything else (should not happen for bookshelf entries, which are full paths): just drop leading slashes.
            pathWithoutRoot = normalisedPath.TrimStart('/');
        }

        // Remove "." and ".." segments and empty segments so an entry can never point outside its folder when unzipped.
        var safePathSegments = pathWithoutRoot
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(pathSegment => pathSegment != "." && pathSegment != "..");

        return FilesFolderName + "/" + string.Join("/", safePathSegments);
    }

    /// <summary>
    /// Writes the backup zip to <paramref name="zipFilePath"/> (an existing file with that name is replaced).
    /// The zip is first written under a temporary name and only renamed when complete,
    /// so a failed backup never leaves a half-written zip that looks finished.
    /// Missing or unreadable bookshelf files are skipped and listed in the result (and in backup-contents.txt).
    /// Throws if the zip itself cannot be written (e.g. no write access to the folder).
    /// </summary>
    /// <param name="bookshelfFilePaths">Full paths of the Markdown files on the bookshelf.</param>
    /// <param name="settingsJson">Current settings as JSON text (taken from memory, so it includes unsaved changes).</param>
    /// <param name="zipFilePath">Full path of the zip to write, normally next to the exe (see <see cref="CreateBackupFileName"/>).</param>
    /// <param name="backupTime">Time stored in the contents list and on the settings entry.</param>
    public static BackupResult CreateBackup(IEnumerable<string> bookshelfFilePaths, string settingsJson, string zipFilePath, DateTime backupTime)
    {
        string temporaryZipFilePath = zipFilePath + ".partial";

        var skippedFiles = new List<string>();
        // One line per file for backup-contents.txt: "files/C/Notes/a.md  <-  C:\Notes\a.md"
        var contentsLines = new List<string>();
        // Entry names already used, compared case-insensitively like Windows does, to catch duplicates.
        var usedEntryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int backedUpFileCount = 0;

        try
        {
            using (var zipFileStream = new FileStream(temporaryZipFilePath, FileMode.Create, FileAccess.Write))
            using (var zipArchive = new ZipArchive(zipFileStream, ZipArchiveMode.Create))
            {
                // 1. The settings file, at the top level of the zip.
                WriteTextEntry(zipArchive, SettingsEntryName, settingsJson, backupTime);

                // 2. Every bookshelf file, keeping its folder structure under "files/".
                foreach (string originalFilePath in bookshelfFilePaths)
                {
                    string entryName = GetEntryNameForFile(originalFilePath);

                    // The bookshelf never holds the same path twice, but guard against it anyway.
                    if (!usedEntryNames.Add(entryName))
                    {
                        continue;
                    }

                    if (!File.Exists(originalFilePath))
                    {
                        skippedFiles.Add($"{originalFilePath} (file not found)");
                        continue;
                    }

                    try
                    {
                        AddFileEntry(zipArchive, originalFilePath, entryName);
                        contentsLines.Add($"{entryName}  <-  {originalFilePath}");
                        backedUpFileCount++;
                    }
                    catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
                    {
                        // E.g. the file is locked by another program or access was denied: skip it, keep going.
                        skippedFiles.Add($"{originalFilePath} ({readError.Message})");
                    }
                }

                // 3. A plain text list of what is in the zip and where it came from, for restoring by hand.
                WriteTextEntry(zipArchive, ContentsListEntryName, BuildContentsList(contentsLines, skippedFiles, backupTime), backupTime);
            }

            // The zip is complete: give it its final name.
            File.Move(temporaryZipFilePath, zipFilePath, overwrite: true);
        }
        catch
        {
            // Do not leave a broken ".partial" file lying around next to the exe.
            TryDeleteFile(temporaryZipFilePath);
            throw;
        }

        return new BackupResult(zipFilePath, backedUpFileCount, skippedFiles);
    }

    /// <summary>
    /// Copies one file into the zip. The file is opened with read/write sharing so a document
    /// that is open in an editor can still be backed up. Its last-modified time is kept.
    /// </summary>
    private static void AddFileEntry(ZipArchive zipArchive, string originalFilePath, string entryName)
    {
        using var sourceStream = new FileStream(originalFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var zipEntry = zipArchive.CreateEntry(entryName, CompressionLevel.Optimal);
        zipEntry.LastWriteTime = GetZipSafeTime(File.GetLastWriteTime(originalFilePath));
        using var entryStream = zipEntry.Open();
        sourceStream.CopyTo(entryStream);
    }

    /// <summary>Writes a text entry (UTF-8 without byte order mark) into the zip.</summary>
    private static void WriteTextEntry(ZipArchive zipArchive, string entryName, string textContent, DateTime entryTime)
    {
        var zipEntry = zipArchive.CreateEntry(entryName, CompressionLevel.Optimal);
        zipEntry.LastWriteTime = GetZipSafeTime(entryTime);
        using var entryWriter = new StreamWriter(zipEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        entryWriter.Write(textContent);
    }

    /// <summary>Zip files can only store dates from 1980 to 2107; clamp anything outside that range.</summary>
    private static DateTimeOffset GetZipSafeTime(DateTime fileTime)
    {
        var earliestZipTime = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var latestZipTime = new DateTime(2107, 12, 31, 0, 0, 0, DateTimeKind.Local);
        if (fileTime < earliestZipTime)
        {
            return earliestZipTime;
        }

        return fileTime > latestZipTime ? latestZipTime : fileTime;
    }

    /// <summary>Text of backup-contents.txt: instructions, the file list, and any skipped files.</summary>
    private static string BuildContentsList(List<string> contentsLines, List<string> skippedFiles, DateTime backupTime)
    {
        var contentsText = new StringBuilder();
        contentsText.AppendLine($"Markdown Viewer backup made {backupTime:yyyy-MM-dd HH:mm:ss}");
        contentsText.AppendLine();
        contentsText.AppendLine("To restore on another computer:");
        contentsText.AppendLine("  1. Unzip this file into a new folder.");
        contentsText.AppendLine("  2. Copy settings.json to %APPDATA%\\MarkdownViewer\\settings.json (close the app first).");
        contentsText.AppendLine("  3. The Markdown files are under \"files\", in their original folder structure");
        contentsText.AppendLine("     (\"files/C/...\" was C:\\..., \"files/UNC/server/...\" was \\\\server\\...).");
        contentsText.AppendLine("     If you put them somewhere else, re-add them to the bookshelf from there.");
        contentsText.AppendLine();
        contentsText.AppendLine($"Files in this backup ({contentsLines.Count}), as  <zip path>  <-  <original path>:");
        foreach (string contentsLine in contentsLines)
        {
            contentsText.AppendLine("  " + contentsLine);
        }

        if (skippedFiles.Count > 0)
        {
            contentsText.AppendLine();
            contentsText.AppendLine($"Not included ({skippedFiles.Count}):");
            foreach (string skippedFile in skippedFiles)
            {
                contentsText.AppendLine("  " + skippedFile);
            }
        }

        return contentsText.ToString();
    }

    /// <summary>Deletes a file if it exists, ignoring any error (used for clean-up only).</summary>
    private static void TryDeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
        }
        catch (Exception)
        {
            // Clean-up is best effort; the original error is what matters.
        }
    }
}
