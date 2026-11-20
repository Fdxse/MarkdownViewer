using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkdownViewer.PostIts;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// The dialog for one shelf's book in the bookshelf: the text on the book, its colour and its order number.
/// Shows a live preview of the book. Built in code because it is small (like <see cref="MarkdownViewer.Todo.TextPromptWindow"/>).
/// </summary>
public sealed class ShelfSpineWindow : Window
{
    /// <summary>The text on the book; empty means the shelf name.</summary>
    private readonly TextBox spineTextBox;

    /// <summary>The colour as "#RRGGBB".</summary>
    private readonly TextBox colourHexTextBox;

    /// <summary>The order number (a whole number; lower numbers are further left).</summary>
    private readonly TextBox orderNumberTextBox;

    /// <summary>The preview of the book, redrawn while the user types.</summary>
    private readonly Border previewSpine;

    /// <summary>The text on the preview book.</summary>
    private readonly TextBlock previewSpineText;

    /// <summary>The shelf being edited (only its name is read, for the default spine text).</summary>
    private readonly NamedBookshelf editedBookshelf;

    /// <summary>The chosen colour as "#RRGGBB" (read after <see cref="Window.ShowDialog"/> returned true).</summary>
    public string ChosenSpineColour { get; private set; } = string.Empty;

    /// <summary>The chosen spine text, or null to show the shelf name (read after <see cref="Window.ShowDialog"/> returned true).</summary>
    public string? ChosenSpineText { get; private set; }

    /// <summary>The chosen order number (read after <see cref="Window.ShowDialog"/> returned true).</summary>
    public int ChosenSpineOrder { get; private set; }

    /// <summary>Creates the dialog, filled in with the shelf's current book settings.</summary>
    public ShelfSpineWindow(NamedBookshelf bookshelf)
    {
        editedBookshelf = bookshelf;

        Title = $"Book for \"{bookshelf.Name}\"";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // --- The fields (left column) ---
        var fieldsPanel = new StackPanel();

        fieldsPanel.Children.Add(CreateFieldLabel("_Text on the book (leave empty to show the bookshelf name):"));
        spineTextBox = new TextBox { Text = bookshelf.SpineText ?? string.Empty, Padding = new Thickness(3) };
        spineTextBox.TextChanged += (_, _) => UpdatePreview();
        fieldsPanel.Children.Add(spineTextBox);

        fieldsPanel.Children.Add(CreateFieldLabel("_Colour (#RRGGBB), or pick one below:"));
        colourHexTextBox = new TextBox
        {
            Text = bookshelf.SpineColour ?? ShelfSpine.DefaultSpineColours[0],
            Padding = new Thickness(3),
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        colourHexTextBox.TextChanged += (_, _) => UpdatePreview();
        fieldsPanel.Children.Add(colourHexTextBox);
        fieldsPanel.Children.Add(CreateColourSwatches());

        fieldsPanel.Children.Add(CreateFieldLabel("_Order number (lower numbers are further to the left):"));
        orderNumberTextBox = new TextBox
        {
            Text = (bookshelf.SpineOrder ?? ShelfSpine.OrderNumberStep).ToString(),
            Padding = new Thickness(3),
            Width = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        fieldsPanel.Children.Add(orderNumberTextBox);
        fieldsPanel.Children.Add(new TextBlock
        {
            Text = "Tip: the order numbers are given out in steps of 10, so you can put a book between two others with, for example, 15.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 4, 0, 0),
        });

        // Labels with "_" give Alt+letter shortcuts; point them at their boxes.
        ((Label)fieldsPanel.Children[0]).Target = spineTextBox;
        ((Label)fieldsPanel.Children[2]).Target = colourHexTextBox;
        ((Label)fieldsPanel.Children[5]).Target = orderNumberTextBox;

        // --- The preview (right column): a book like the ones in the bookshelf ---
        previewSpineText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new RotateTransform(90),
            MaxWidth = 100,
        };
        previewSpine = new Border
        {
            Width = 28,
            Height = 126,
            CornerRadius = new CornerRadius(3, 3, 1, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            Child = previewSpineText,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 26, 4, 0),
        };

        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(fieldsPanel, 0);
        Grid.SetColumn(previewSpine, 1);
        contentGrid.Children.Add(fieldsPanel);
        contentGrid.Children.Add(previewSpine);

        // --- OK / Cancel ---
        var okButton = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
        okButton.Click += OkButton_Click;
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0) };
        var buttonsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttonsPanel.Children.Add(okButton);
        buttonsPanel.Children.Add(cancelButton);

        var windowPanel = new StackPanel { Margin = new Thickness(12) };
        windowPanel.Children.Add(contentGrid);
        windowPanel.Children.Add(buttonsPanel);
        Content = windowPanel;

        UpdatePreview();
        Loaded += (_, _) =>
        {
            spineTextBox.Focus();
            spineTextBox.SelectAll();
        };
    }

    /// <summary>A label above a field.</summary>
    private static Label CreateFieldLabel(string labelText)
    {
        return new Label { Content = labelText, Padding = new Thickness(0), Margin = new Thickness(0, 8, 0, 3) };
    }

    /// <summary>A row of clickable colour squares: the default book colours plus a few lighter ones.</summary>
    private WrapPanel CreateColourSwatches()
    {
        // Lighter colours for those who prefer them (the text turns dark on them automatically).
        string[] lighterColours = { "#C9A66B", "#B8D4E8", "#C8E0B8", "#E8C0C0", "#E0D0F0", "#F0E6C8" };

        var swatchesPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (string swatchHex in ShelfSpine.DefaultSpineColours.Concat(lighterColours))
        {
            var swatchButton = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                Background = new SolidColorBrush(ShelfSpine.ParseColour(swatchHex)),
                BorderBrush = Brushes.Gray,
                ToolTip = swatchHex,
                Tag = swatchHex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            // Clicking a swatch puts its value in the colour box (which updates the preview).
            swatchButton.Click += (_, _) => colourHexTextBox.Text = swatchHex;
            swatchesPanel.Children.Add(swatchButton);
        }
        return swatchesPanel;
    }

    /// <summary>Redraws the preview book from what is typed in the boxes.</summary>
    private void UpdatePreview()
    {
        // The constructor fills the boxes before every control exists; skip until they all do.
        if (previewSpine == null || previewSpineText == null || colourHexTextBox == null || spineTextBox == null)
        {
            return;
        }

        Color spineColour = ShelfSpine.ParseColour(colourHexTextBox.Text);
        previewSpine.Background = new SolidColorBrush(spineColour);
        previewSpineText.Foreground = new SolidColorBrush(ShelfSpine.GetTextColour(spineColour));
        previewSpineText.Text = string.IsNullOrWhiteSpace(spineTextBox.Text) ? editedBookshelf.Name : spineTextBox.Text.Trim();
    }

    /// <summary>OK: checks the colour and the order number, then closes the dialog.</summary>
    private void OkButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        string? validHex = PostItColour.NormalizeHex(colourHexTextBox.Text);
        if (validHex == null)
        {
            MessageBox.Show(this, "Please type the colour as a six-digit hex value such as #2E4A7A, or click one of the colours.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            colourHexTextBox.Focus();
            return;
        }

        if (!int.TryParse(orderNumberTextBox.Text.Trim(), out int typedOrderNumber))
        {
            MessageBox.Show(this, "Please type the order number as a whole number, for example 10, 15 or 20.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
            orderNumberTextBox.Focus();
            return;
        }

        ChosenSpineColour = validHex;
        ChosenSpineText = string.IsNullOrWhiteSpace(spineTextBox.Text) ? null : spineTextBox.Text.Trim();
        ChosenSpineOrder = typedOrderNumber;
        DialogResult = true;
    }
}
