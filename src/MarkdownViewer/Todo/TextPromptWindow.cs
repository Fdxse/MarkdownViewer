using System.Windows;
using System.Windows.Controls;

namespace MarkdownViewer.Todo;

/// <summary>
/// A small dialog with one line of explanation, a text box and OK/Cancel.
/// Used to type a new task and to change the todo section heading. Built in code because it is so small.
/// </summary>
public sealed class TextPromptWindow : Window
{
    /// <summary>The box the user types into.</summary>
    private readonly TextBox answerTextBox;

    /// <summary>What the user typed (read after <see cref="Window.ShowDialog"/> returned true).</summary>
    public string AnswerText => answerTextBox.Text;

    /// <summary>Creates the dialog.</summary>
    /// <param name="windowTitle">Title bar text.</param>
    /// <param name="promptText">Explanation shown above the text box.</param>
    /// <param name="initialText">Text already in the box (selected, so typing replaces it).</param>
    public TextPromptWindow(string windowTitle, string promptText, string initialText)
    {
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
}
