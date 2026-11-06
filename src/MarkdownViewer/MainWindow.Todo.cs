using System.IO;
using System.Windows;
using System.Windows.Input;
using MarkdownViewer.Macros;
using MarkdownViewer.Todo;

namespace MarkdownViewer;

/// <summary>
/// The todo list: a plain Markdown file (todo.md) next to the exe, plus clickable task check boxes in any document.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Full path of todo.md in the exe's folder. Environment.ProcessPath is the real .exe even for a
    /// single-file app; the base directory is only a fallback.
    /// </summary>
    private static string TodoFilePath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
        TodoDocument.FileName);

    /// <summary>True if the document shown right now is the todo list.</summary>
    private bool IsTodoListOpen =>
        currentFilePath != null && string.Equals(Path.GetFullPath(currentFilePath), TodoFilePath, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Task check boxes (any document)
    // ------------------------------------------------------------------

    /// <summary>
    /// Called when a task check box in the viewer is clicked: changes "[ ]" to "[x]" (or back) on that line
    /// and saves the file straight away. An unsaved new document is only changed, not saved.
    /// </summary>
    /// <param name="sourceLineIndex">0-based line of the task in the Markdown text.</param>
    /// <param name="isNowChecked">The check box state after the click.</param>
    private void HandleTaskCheckBoxToggled(int sourceLineIndex, bool isNowChecked)
    {
        // While typing in the editor, the preview may still show an older version of the text, so the line
        // numbers might not match. Refresh the preview instead and let the user click again.
        if (previewRefreshTimer.IsEnabled)
        {
            previewRefreshTimer.Stop();
            Dispatcher.BeginInvoke(() => RenderPreview());
            return;
        }

        string? updatedText = TodoDocument.SetTaskChecked(MarkdownEditor.Text, sourceLineIndex, isNowChecked);
        if (updatedText == null)
        {
            // The line no longer holds a task: show the document as it really is.
            Dispatcher.BeginInvoke(() => RenderPreview());
            return;
        }

        ReplaceEditorTextKeepingCaret(updatedText);

        // Save at once, as asked for the todo list. (Other edits in the editor are saved along with it.)
        if (currentFilePath != null)
        {
            SaveToFile(currentFilePath);
        }
    }

    /// <summary>
    /// Replaces the whole editor text as a normal edit (so it counts as a change and can be saved),
    /// keeping the cursor where it was as far as possible.
    /// </summary>
    private void ReplaceEditorTextKeepingCaret(string updatedText)
    {
        int previousCaretIndex = MarkdownEditor.CaretIndex;
        MarkdownEditor.Text = updatedText;
        MarkdownEditor.CaretIndex = Math.Min(previousCaretIndex, updatedText.Length);
    }

    // ------------------------------------------------------------------
    // Todo menu
    // ------------------------------------------------------------------

    /// <summary>
    /// Todo → Add task (Ctrl+Shift+T): asks for a task and puts it at the top of the configured section in todo.md.
    /// The document that is open stays open; if it is the todo list itself, it updates in place.
    /// </summary>
    private void AddTodoTaskCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        string sectionHeading = TodoDocument.NormaliseSectionHeading(appSettings.TodoSectionHeading);
        var taskPrompt = new TextPromptWindow(
            "Add task",
            $"New task. It is added at the top of \"{sectionHeading}\" in {TodoDocument.FileName}:",
            initialText: string.Empty,
            // Macros such as "@730" expand in the task text just like in the editor.
            macroExpander: macroExpander)
        { Owner = this };

        if (taskPrompt.ShowDialog() != true || string.IsNullOrWhiteSpace(taskPrompt.AnswerText))
        {
            return;
        }

        if (IsTodoListOpen)
        {
            // Work on the editor text so any unsaved edits in the todo list are kept, then save.
            ReplaceEditorTextKeepingCaret(TodoDocument.InsertTask(MarkdownEditor.Text, sectionHeading, taskPrompt.AnswerText));
            previewRefreshTimer.Stop();
            RenderPreview();
            SaveToFile(currentFilePath!);
            return;
        }

        try
        {
            // ReadAllText detects UTF-8/UTF-16 byte order marks; a missing file starts out empty.
            string todoText = File.Exists(TodoFilePath) ? File.ReadAllText(TodoFilePath) : string.Empty;
            string updatedTodoText = TodoDocument.InsertTask(todoText, sectionHeading, taskPrompt.AnswerText);
            File.WriteAllText(TodoFilePath, updatedTodoText, SaveEncoding);
            DocumentInfoStatusText.Text = $"Task added to {TodoDocument.FileName} (Ctrl+T opens it)";
        }
        catch (Exception writeError)
        {
            MessageBox.Show(this, $"Could not add the task to:\n{TodoFilePath}\n\n{writeError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Todo → Open todo list (Ctrl+T): opens todo.md, creating it with an empty todo section if it does not exist.</summary>
    private void OpenTodoListCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (IsTodoListOpen || !ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        if (!File.Exists(TodoFilePath))
        {
            try
            {
                string sectionHeading = TodoDocument.NormaliseSectionHeading(appSettings.TodoSectionHeading);
                File.WriteAllText(TodoFilePath, sectionHeading + Environment.NewLine + Environment.NewLine, SaveEncoding);
            }
            catch (Exception createError)
            {
                MessageBox.Show(this, $"Could not create the todo list:\n{TodoFilePath}\n\n{createError.Message}",
                    "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        LoadFile(TodoFilePath);
    }

    /// <summary>Todo → Section for new tasks: changes the heading that new tasks are added under, and saves the setting.</summary>
    private void TodoSectionMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var headingPrompt = new TextPromptWindow(
            "Section for new tasks",
            $"Heading in {TodoDocument.FileName} that new tasks go under, e.g. \"## Todo\". " +
            "It is matched by its text (any level); if no such heading exists it is added at the end of the file.",
            TodoDocument.NormaliseSectionHeading(appSettings.TodoSectionHeading))
        { Owner = this };

        if (headingPrompt.ShowDialog() != true || string.IsNullOrWhiteSpace(headingPrompt.AnswerText.Trim('#', ' ')))
        {
            return;
        }

        appSettings.TodoSectionHeading = TodoDocument.NormaliseSectionHeading(headingPrompt.AnswerText);
        try
        {
            appSettings.Save();
        }
        catch (Exception saveError)
        {
            // The new heading is still used for this session.
            MessageBox.Show(this, $"The section is changed now, but could not be saved:\n{AppSettings.SettingsFilePath}\n\n{saveError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
