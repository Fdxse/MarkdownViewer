using System.IO;
using System.Windows;
using System.Windows.Input;
using MarkdownViewer.Journal;
using MarkdownViewer.Todo;

namespace MarkdownViewer;

/// <summary>
/// The journal: one plain Markdown file (journal.md) next to the exe, like todo.md.
/// Ctrl+J asks for a note and adds it at the top under a "### date time" heading; Ctrl+Shift+J opens the journal.
/// </summary>
public partial class MainWindow
{
    /// <summary>Full path of journal.md in the exe's folder (the same folder as todo.md).</summary>
    private static string JournalFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        JournalDocument.FileName);

    /// <summary>True if the document shown right now is the journal.</summary>
    private bool IsJournalOpen =>
        currentFilePath != null && string.Equals(Path.GetFullPath(currentFilePath), JournalFilePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Journal → New entry (Ctrl+J): asks for a note and puts it at the top of journal.md under a heading with
    /// the current date and time. The document that is open stays open; if it is the journal itself, it updates in place.
    /// </summary>
    private void AddJournalEntryCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        var entryPrompt = new TextPromptWindow(
            "New journal entry",
            $"What happened? The entry is stamped with the date and time and added at the top of {JournalDocument.FileName}. " +
            "Enter starts a new line, Ctrl+Enter saves.",
            initialText: string.Empty,
            // Macros such as "@730" expand in the note just like in the editor.
            macroExpander: macroExpander,
            isMultiLine: true)
        { Owner = this };

        if (entryPrompt.ShowDialog() != true || string.IsNullOrWhiteSpace(entryPrompt.AnswerText))
        {
            return;
        }

        // Stamp the time the entry was saved (the moment OK was pressed).
        DateTime entryTime = DateTime.Now;

        if (IsJournalOpen)
        {
            // Work on the editor text so any unsaved edits in the journal are kept, then save.
            ReplaceEditorTextKeepingCaret(JournalDocument.InsertEntry(MarkdownEditor.Text, entryTime, entryPrompt.AnswerText));
            previewRefreshTimer.Stop();
            RenderPreview();
            SaveToFile(currentFilePath!);
            return;
        }

        try
        {
            // ReadAllText detects UTF-8/UTF-16 byte order marks; a missing file starts out empty.
            string journalText = File.Exists(JournalFilePath) ? File.ReadAllText(JournalFilePath) : string.Empty;
            string updatedJournalText = JournalDocument.InsertEntry(journalText, entryTime, entryPrompt.AnswerText);
            File.WriteAllText(JournalFilePath, updatedJournalText, SaveEncoding);
            // Let global search find the new entry straight away.
            UpdateSavedFileInSearchIndex(JournalFilePath);
            DocumentInfoStatusText.Text = $"Entry added to {JournalDocument.FileName} (Ctrl+Shift+J opens it)";
        }
        catch (Exception writeError)
        {
            MessageBox.Show(this, $"Could not add the entry to the journal:\n{JournalFilePath}\n\n{writeError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Journal → Open journal (Ctrl+Shift+J): opens journal.md, creating it with a title if it does not exist.</summary>
    private void OpenJournalCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (IsJournalOpen || !ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        if (!File.Exists(JournalFilePath))
        {
            try
            {
                File.WriteAllText(JournalFilePath, JournalDocument.DocumentTitle + Environment.NewLine + Environment.NewLine, SaveEncoding);
            }
            catch (Exception createError)
            {
                MessageBox.Show(this, $"Could not create the journal:\n{JournalFilePath}\n\n{createError.Message}",
                    "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        LoadFile(JournalFilePath);
    }
}
