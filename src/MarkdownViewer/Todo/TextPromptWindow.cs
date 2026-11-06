using System.Windows;
using System.Windows.Controls;
using MarkdownViewer.Macros;

namespace MarkdownViewer.Todo;

/// <summary>
/// A small dialog with one line of explanation, a text box and OK/Cancel.
/// Used to type a new task and to change the todo section heading. Built in code because it is so small.
/// </summary>
public sealed class TextPromptWindow : Window
{
    /// <summary>The box the user types into.</summary>
    private readonly TextBox answerTextBox;

    /// <summary>Expands macros such as "@730" in the typed text; null when macros should not apply.</summary>
    private readonly MacroExpander? macroExpander;

    /// <summary>
    /// What the user typed (read after <see cref="Window.ShowDialog"/> returned true).
    /// Any macro trigger still left in it, e.g. "@730" typed last with nothing after it, is expanded too.
    /// </summary>
    public string AnswerText => macroExpander?.ExpandAll(answerTextBox.Text) ?? answerTextBox.Text;

    /// <summary>Creates the dialog.</summary>
    /// <param name="windowTitle">Title bar text.</param>
    /// <param name="promptText">Explanation shown above the text box.</param>
    /// <param name="initialText">Text already in the box (selected, so typing replaces it).</param>
    /// <param name="macroExpander">If given, macros expand while typing in the box, the same way as in the editor.</param>
    public TextPromptWindow(string windowTitle, string promptText, string initialText, MacroExpander? macroExpander = null)
    {
        this.macroExpander = macroExpander;

        Title = windowTitle;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var promptTextBlock = new TextBlock
        {
            Text = promptText,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };

        answerTextBox = new TextBox
        {
            Text = initialText,
            Padding = new Thickness(3),
        };
        answerTextBox.TextChanged += AnswerTextBox_TextChanged;

        // Enter = OK (IsDefault), Esc = Cancel (IsCancel).
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
        okButton.Click += (_, _) => DialogResult = true;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);

        var dialogLayout = new StackPanel { Margin = new Thickness(12) };
        dialogLayout.Children.Add(promptTextBlock);
        dialogLayout.Children.Add(answerTextBox);
        dialogLayout.Children.Add(buttonRow);
        Content = dialogLayout;

        // Put the cursor in the box straight away so the user can just start typing.
        Loaded += (_, _) =>
        {
            answerTextBox.Focus();
            answerTextBox.SelectAll();
        };
    }

    /// <summary>
    /// Expands a macro as soon as the user types a space or punctuation right after its trigger
    /// (e.g. "@730 "), so the result is visible before pressing OK. Ctrl+Z undoes the expansion.
    /// </summary>
    private void AnswerTextBox_TextChanged(object sender, TextChangedEventArgs changeDetails)
    {
        if (macroExpander == null || !macroExpander.HasMacros || changeDetails.Changes.Count != 1)
        {
            return;
        }

        // Only react to plain typing of one character (not a paste or a replacement).
        var textChange = changeDetails.Changes.First();
        if (textChange.RemovedLength != 0 || textChange.AddedLength != 1 || textChange.Offset == 0)
        {
            return;
        }

        string boxText = answerTextBox.Text;
        int typedCharacterIndex = textChange.Offset;
        char typedCharacter = boxText[typedCharacterIndex];
        if (!char.IsWhiteSpace(typedCharacter) && !MacroExpander.TerminatorCharacters.Contains(typedCharacter))
        {
            return;
        }

        if (!macroExpander.TryExpandAtEnd(boxText[..typedCharacterIndex], out int triggerStartIndex, out int triggerLength, out string expandedText))
        {
            return;
        }

        string triggerText = boxText.Substring(triggerStartIndex, triggerLength);

        // The text cannot be changed from inside TextChanged, so replace the trigger right after.
        Dispatcher.BeginInvoke(() =>
        {
            // Make sure nothing else changed the text in the meantime.
            if (answerTextBox.Text.Length < triggerStartIndex + triggerLength ||
                answerTextBox.Text.Substring(triggerStartIndex, triggerLength) != triggerText)
            {
                return;
            }

            // Replacing through the selection keeps it as one undo step.
            answerTextBox.Select(triggerStartIndex, triggerLength);
            answerTextBox.SelectedText = expandedText;
            // Cursor back after the separator the user typed.
            answerTextBox.Select(triggerStartIndex + expandedText.Length + 1, 0);
        });
    }
}
