using System.Windows;
using System.Windows.Input;
using MarkdownViewer.Tables;

namespace MarkdownViewer;

/// <summary>
/// Tools → Table converter (Ctrl+Shift+L): turns a pasted table (Excel, Word, web page, CSV or Markdown)
/// into a Markdown table and inserts it into the open document at the cursor.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Opens the table converter. A table selected in the editor is loaded into it and replaced on insert,
    /// so an existing table can be reformatted (lined up, swapped...). Otherwise the table goes in at the cursor.
    /// </summary>
    private void TableConverterCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        // A query result is generated and the post-it board is not a document, so there the table can only be copied.
        bool canInsert = currentQueryEntry == null && PostItBoardView.Visibility != Visibility.Visible;
        bool hasEditorSelection = canInsert && isEditMode && MarkdownEditor.SelectionLength > 0;

        string insertButtonText = !canInsert ? "Insert (not possible here)"
            : hasEditorSelection ? "Replace selection"
            : "Insert into document";

        var converterWindow = new TableConverterWindow(hasEditorSelection ? MarkdownEditor.SelectedText : null, insertButtonText) { Owner = this };
        if (!canInsert)
        {
            converterWindow.DisableInsert("Open a document to insert the table; you can still copy it from here.");
        }

        if (converterWindow.ShowDialog() != true || !canInsert)
        {
            return;
        }

        InsertMarkdownTableAtCursor(converterWindow.MarkdownTable);
    }

    /// <summary>
    /// Puts the table into the editor at the cursor (replacing any selection), on lines of its own with a blank line
    /// before and after, since Markdown only sees a table that is not glued to a paragraph. Switches to edit mode
    /// so the change is visible and can be undone with Ctrl+Z; the document is not saved.
    /// </summary>
    private void InsertMarkdownTableAtCursor(string markdownTable)
    {
        if (!isEditMode)
        {
            // Without edit mode the cursor sits wherever it was last; the end of the document is the clearest place then.
            SetEditMode(true);
            MarkdownEditor.Select(MarkdownEditor.Text.Length, 0);
        }

        string editorText = MarkdownEditor.Text;
        int insertStart = MarkdownEditor.SelectionStart;
        int insertEnd = insertStart + MarkdownEditor.SelectionLength;
        string textBefore = editorText[..insertStart];
        string textAfter = editorText[insertEnd..];

        // Line breaks needed before the table: none at the start of the document, otherwise enough for one blank line.
        string leadingBreaks = textBefore.Length == 0 ? string.Empty
            : textBefore.EndsWith("\n\n") || textBefore.EndsWith("\r\n\r\n") ? string.Empty
            : textBefore.EndsWith('\n') ? "\r\n"
            : "\r\n\r\n";

        // Line breaks needed after it: same idea, so following text does not become a table row.
        string trailingBreaks = textAfter.Length == 0 ? "\r\n"
            : textAfter.StartsWith("\n\n") || textAfter.StartsWith("\r\n\r\n") ? string.Empty
            : textAfter.StartsWith('\n') || textAfter.StartsWith("\r\n") ? "\r\n"
            : "\r\n\r\n";

        // SelectedText keeps the change in the editor's undo history (Ctrl+Z takes it back).
        string insertedText = leadingBreaks + markdownTable + trailingBreaks;
        MarkdownEditor.SelectedText = insertedText;
        MarkdownEditor.Select(insertStart + insertedText.Length, 0);
        MarkdownEditor.Focus();

        DocumentInfoStatusText.Text = "Table inserted (Ctrl+Z undoes it; save with Ctrl+S)";
    }
}
