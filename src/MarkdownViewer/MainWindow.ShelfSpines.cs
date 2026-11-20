using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// The bookshelf at the top of the bookshelf panel: every shelf is drawn as a tall, thin book standing on a plank.
/// Clicking a book shows that shelf (this replaces the old shelf drop-down). Right-clicking a book, or the
/// "⋯" menu, lets the user set the book's colour, the text on it and its order number (books are shown from the
/// lowest order number on the left to the highest on the right). The Archive and Trash books always come last.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tag of the "Book colour, text and order" item in the shelf menu.</summary>
    private const string EditSpineMenuTag = "EditSpine";

    /// <summary>Width of one book spine.</summary>
    private const double SpineWidth = 28;

    /// <summary>Height of the shortest book; each book is up to <see cref="SpineHeightVariation"/> taller.</summary>
    private const double ShortestSpineHeight = 104;

    /// <summary>How much taller than the shortest a book can be, so the books do not all look the same.</summary>
    private const int SpineHeightVariation = 14;

    /// <summary>How far the shown shelf's book is pulled up out of the row, so it is clear which shelf is shown.</summary>
    private const double SelectedSpineLift = 8;

    /// <summary>Colour of the plank the books stand on.</summary>
    private static readonly SolidColorBrush PlankBrush = CreateFrozenSpineBrush(Color.FromRgb(0x8B, 0x6B, 0x47));

    /// <summary>Colour of the outline around the shown shelf's book.</summary>
    private static readonly SolidColorBrush SelectedSpineOutlineBrush = CreateFrozenSpineBrush(Color.FromRgb(0xE8, 0xC5, 0x47));

    /// <summary>Colour of the thin outline around the other books.</summary>
    private static readonly SolidColorBrush SpineOutlineBrush = CreateFrozenSpineBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00));

    /// <summary>
    /// Redraws the books (after shelves were added, removed, renamed or re-coloured, or another shelf is shown) and
    /// updates the shown shelf's name under them.
    /// </summary>
    private void RefreshShelfSpines()
    {
        ShelfSpinePanel.Items.Clear();

        var shelvesInOrder = GetShelvesForBookshelf();
        bool hasAddedGapBeforeBuiltInShelves = false;
        foreach (var bookshelf in shelvesInOrder)
        {
            // A small empty stretch of plank between the user's shelves and Archive/Trash.
            if (bookshelf.IsBuiltInShelf && !hasAddedGapBeforeBuiltInShelves)
            {
                ShelfSpinePanel.Items.Add(CreatePlankGap());
                hasAddedGapBeforeBuiltInShelves = true;
            }
            ShelfSpinePanel.Items.Add(CreateSpineOnPlank(bookshelf));
        }

        // The full name of the shown shelf (the book may show a shorter text, or the name may not fit on it).
        CurrentShelfTitle.Text = currentBookshelf.ToString();
    }

    /// <summary>One book standing on its own piece of plank (the pieces line up into one plank per row).</summary>
    private FrameworkElement CreateSpineOnPlank(NamedBookshelf bookshelf)
    {
        bool isShownShelf = ReferenceEquals(bookshelf, currentBookshelf);
        Color spineColour = ShelfSpine.GetSpineColour(bookshelf);
        Color textColour = ShelfSpine.GetTextColour(spineColour);
        var bandBrush = CreateFrozenSpineBrush(ShelfSpine.GetBandColour(spineColour));

        // The text on the spine, turned to run from top to bottom like on a real book.
        var spineTextBlock = new TextBlock
        {
            Text = bookshelf.SpineDisplayText,
            Foreground = CreateFrozenSpineBrush(textColour),
            FontWeight = isShownShelf ? FontWeights.Bold : FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new RotateTransform(90),
        };

        double spineHeight = ShelfSpine.GetSpineHeight(bookshelf, ShortestSpineHeight, SpineHeightVariation);

        // Two thin bands across the spine near the top and bottom, like the decoration on a bound book.
        var spineFace = new Grid();
        spineFace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
        spineFace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        spineFace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(9) });
        var topBand = new Border { Height = 3, Background = bandBrush, VerticalAlignment = VerticalAlignment.Bottom };
        var bottomBand = new Border { Height = 3, Background = bandBrush, VerticalAlignment = VerticalAlignment.Top };
        Grid.SetRow(topBand, 0);
        Grid.SetRow(spineTextBlock, 1);
        Grid.SetRow(bottomBand, 2);
        spineFace.Children.Add(topBand);
        spineFace.Children.Add(spineTextBlock);
        spineFace.Children.Add(bottomBand);

        // The text can only be as long as the space between the bands (it is turned, so its width is the spine's height).
        spineTextBlock.MaxWidth = spineHeight - 26;

        var spineButton = new Button
        {
            Style = (Style)FindResource("ShelfSpineButtonStyle"),
            Width = SpineWidth,
            Height = spineHeight,
            Background = CreateFrozenSpineBrush(spineColour),
            BorderBrush = isShownShelf ? SelectedSpineOutlineBrush : SpineOutlineBrush,
            BorderThickness = new Thickness(isShownShelf ? 2 : 1),
            Content = spineFace,
            VerticalAlignment = VerticalAlignment.Bottom,
            Tag = bookshelf,
            ToolTip = CreateSpineToolTip(bookshelf),
            // The shown shelf's book is pulled up a little out of the row.
            RenderTransform = new TranslateTransform(0, isShownShelf ? -SelectedSpineLift : 0),
        };
        spineButton.Click += ShelfSpineButton_Click;
        spineButton.ContextMenu = CreateSpineContextMenu(bookshelf);

        // The piece of plank: a thick bottom border. The book stands at the bottom of the row so all books line up.
        return new Border
        {
            BorderBrush = PlankBrush,
            BorderThickness = new Thickness(0, 0, 0, 5),
            Padding = new Thickness(1, SelectedSpineLift, 1, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = spineButton,
        };
    }

    /// <summary>An empty piece of plank, used as a gap before the Archive and Trash books.</summary>
    private static FrameworkElement CreatePlankGap()
    {
        return new Border
        {
            Width = 10,
            BorderBrush = PlankBrush,
            BorderThickness = new Thickness(0, 0, 0, 5),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>The tooltip of a book: the shelf's name and kind, its order number, and how to change the book.</summary>
    private static string CreateSpineToolTip(NamedBookshelf bookshelf)
    {
        // ToString adds "(folder)", "(queries)", "(post-its)" or the Trash count to the name.
        string toolTipText = bookshelf.ToString();
        if (!bookshelf.IsBuiltInShelf)
        {
            toolTipText += $"\nOrder number: {bookshelf.SpineOrder}\nRight-click to change the colour, text or order number.";
        }
        return toolTipText;
    }

    /// <summary>The right-click menu of a book: change its colour, text and order (not for Archive and Trash).</summary>
    private ContextMenu CreateSpineContextMenu(NamedBookshelf bookshelf)
    {
        var editSpineMenuItem = new MenuItem
        {
            Header = "_Book colour, text and order...",
            Tag = bookshelf,
            IsEnabled = !bookshelf.IsBuiltInShelf,
        };
        editSpineMenuItem.Click += (_, _) => EditShelfSpine(bookshelf);

        var spineContextMenu = new ContextMenu();
        spineContextMenu.Items.Add(editSpineMenuItem);
        return spineContextMenu;
    }

    /// <summary>A book was clicked: shows that shelf.</summary>
    private void ShelfSpineButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (sender is not Button { Tag: NamedBookshelf clickedShelf } || ReferenceEquals(clickedShelf, currentBookshelf))
        {
            return;
        }
        ShowBookshelf(clickedShelf);
    }

    /// <summary>Shelf menu → Book colour, text and order: edits the shown shelf's book.</summary>
    private void EditShelfSpineMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        EditShelfSpine(currentBookshelf);
    }

    /// <summary>
    /// Opens the dialog for a shelf's book (colour, text, order number) and, on OK, saves the settings and redraws
    /// the books. Archive and Trash cannot be changed (they are not stored in the settings).
    /// </summary>
    private void EditShelfSpine(NamedBookshelf bookshelf)
    {
        if (bookshelf.IsBuiltInShelf)
        {
            return;
        }

        var spineDialog = new ShelfSpineWindow(bookshelf) { Owner = this };
        if (spineDialog.ShowDialog() != true)
        {
            return;
        }

        bookshelf.SpineColour = spineDialog.ChosenSpineColour;
        bookshelf.SpineText = spineDialog.ChosenSpineText;
        bookshelf.SpineOrder = spineDialog.ChosenSpineOrder;
        SaveSettingsOrWarn();
        RefreshShelfSpines();
    }

    /// <summary>Makes a frozen (faster, shareable) brush of a colour.</summary>
    private static SolidColorBrush CreateFrozenSpineBrush(Color brushColour)
    {
        var colourBrush = new SolidColorBrush(brushColour);
        colourBrush.Freeze();
        return colourBrush;
    }
}
