using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.PostIts;

namespace MarkdownViewer;

/// <summary>
/// Colours of post-it notes. Every note is the standard yellow until the user picks another colour from
/// the palette (right-click a note → Colour). The palette is a fixed list of named colours, each with a
/// category, that the user extends in Tools → Post-it colours. A note's colour is saved per board in
/// <see cref="NamedBookshelf.PostItNoteColourIds"/>, by the file's display name, like its place on the board.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------------
    // Painting a note
    // ------------------------------------------------------------------

    /// <summary>The palette colour of a note's file, or null for the standard yellow.</summary>
    private PostItColour? GetPostItNoteColour(BookshelfEntry bookshelfEntry)
    {
        string? colourId = null;
        currentBookshelf.PostItNoteColourIds?.TryGetValue(bookshelfEntry.DisplayName, out colourId);
        // A colour that was removed from the palette is not found, so the note falls back to yellow.
        return appSettings.FindPostItColour(colourId);
    }

    /// <summary>
    /// Gives a note its paper colour: the paper itself, the darker glue strip on top, the tilt handle and the fade
    /// over text that runs past the bottom. Null paints the standard yellow.
    /// </summary>
    private static void PaintPostItNote(Border postItNote, PostItNoteState noteState, PostItColour? noteColour)
    {
        Color paperColour = PostItColour.ParseColourOrStandardYellow(noteColour?.Hex);
        var glueStripBrush = PostItColour.CreateFrozenBrush(PostItColour.MakeGlueStripColour(paperColour));

        postItNote.Background = PostItColour.CreateFrozenBrush(paperColour);
        postItNote.BorderBrush = glueStripBrush;
        noteState.TiltHandle.Background = glueStripBrush;

        // From fully transparent to the paper colour, top to bottom.
        var overflowFadeBrush = new LinearGradientBrush(Color.FromArgb(0, paperColour.R, paperColour.G, paperColour.B), paperColour, 90);
        overflowFadeBrush.Freeze();
        noteState.OverflowFade.Background = overflowFadeBrush;

        // The file path, plus the category, when the mouse rests on the note.
        postItNote.ToolTip = noteColour == null || string.IsNullOrWhiteSpace(noteColour.Category)
            ? noteState.BookshelfEntry.FilePath
            : $"{noteColour.Category}\n{noteState.BookshelfEntry.FilePath}";
    }

    // ------------------------------------------------------------------
    // The Colour menu of a note
    // ------------------------------------------------------------------

    /// <summary>
    /// The "Colour" submenu of a note's right-click menu: the standard yellow, then each colour of the palette
    /// (with its category), the current one ticked, and a way to the palette settings at the bottom.
    /// </summary>
    private MenuItem CreatePostItColourMenu(BookshelfEntry bookshelfEntry, Border postItNote)
    {
        var colourMenu = new MenuItem { Header = "_Colour" };

        // The items are built when the submenu opens, so they always show the current palette and the current choice.
        colourMenu.SubmenuOpened += (_, _) => FillPostItColourMenu(colourMenu, bookshelfEntry, postItNote);
        // A placeholder, so WPF shows the arrow and opens the submenu at all.
        colourMenu.Items.Add(new MenuItem { Header = "…" });
        return colourMenu;
    }

    /// <summary>Fills the Colour submenu of one note.</summary>
    private void FillPostItColourMenu(MenuItem colourMenu, BookshelfEntry bookshelfEntry, Border postItNote)
    {
        colourMenu.Items.Clear();
        var currentColour = GetPostItNoteColour(bookshelfEntry);

        // The standard yellow always comes first.
        var standardYellowMenuItem = CreatePostItColourMenuItem(PostItColour.StandardYellowName, PostItColour.StandardYellowHex, isCurrent: currentColour == null);
        standardYellowMenuItem.Click += (_, _) => SetPostItNoteColour(bookshelfEntry, postItNote, newColour: null);
        colourMenu.Items.Add(standardYellowMenuItem);

        // Then the palette, in the order of the settings page.
        foreach (var paletteColour in appSettings.PostItColours ?? new List<PostItColour>())
        {
            bool isCurrentColour = currentColour != null && currentColour.Id == paletteColour.Id;
            var paletteMenuItem = CreatePostItColourMenuItem(paletteColour.MenuText, paletteColour.Hex, isCurrentColour);
            paletteMenuItem.Click += (_, _) => SetPostItNoteColour(bookshelfEntry, postItNote, paletteColour);
            colourMenu.Items.Add(paletteMenuItem);
        }

        colourMenu.Items.Add(new Separator());
        var editColoursMenuItem = new MenuItem { Header = "_Edit colours..." };
        editColoursMenuItem.Click += PostItColourSettingsMenuItem_Click;
        colourMenu.Items.Add(editColoursMenuItem);
    }

    /// <summary>One colour in the Colour submenu: a small swatch as its icon, and its name, in bold with a tick if the note has it.</summary>
    private static MenuItem CreatePostItColourMenuItem(string menuText, string hexValue, bool isCurrent)
    {
        // A single "_" in a menu header marks the access key, so double it to show it literally.
        string escapedMenuText = menuText.Replace("_", "__");
        return new MenuItem
        {
            // The tick is part of the text (not IsChecked), because a checked menu item hides its icon, i.e. the swatch.
            Header = isCurrent ? escapedMenuText + "  ✓" : escapedMenuText,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            Icon = new Border
            {
                Width = 16,
                Height = 12,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = PostItColour.CreateFrozenBrush(PostItColour.ParseColourOrStandardYellow(hexValue)),
            },
        };
    }

    /// <summary>
    /// Gives one note a colour (null = back to the standard yellow), saves it and repaints the note at once,
    /// without redrawing the board.
    /// </summary>
    private void SetPostItNoteColour(BookshelfEntry bookshelfEntry, Border postItNote, PostItColour? newColour)
    {
        var colourIdByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Keep the colours of the other files on the shelf; colours of files that are gone (deleted, renamed) are dropped.
        if (currentBookshelf.PostItNoteColourIds != null)
        {
            var namesOnShelf = new HashSet<string>(currentBookshelf.Entries.Select(shelfEntry => shelfEntry.DisplayName), StringComparer.OrdinalIgnoreCase);
            foreach (var savedColour in currentBookshelf.PostItNoteColourIds)
            {
                if (namesOnShelf.Contains(savedColour.Key))
                {
                    colourIdByName.TryAdd(savedColour.Key, savedColour.Value);
                }
            }
        }

        // The standard yellow is simply "no entry", which keeps the settings file small.
        colourIdByName.Remove(bookshelfEntry.DisplayName);
        if (newColour != null)
        {
            colourIdByName[bookshelfEntry.DisplayName] = newColour.Id;
        }

        currentBookshelf.PostItNoteColourIds = colourIdByName.Count > 0 ? colourIdByName : null;
        SaveSettingsOrWarn();

        if (postItNote.Tag is PostItNoteState noteState)
        {
            PaintPostItNote(postItNote, noteState, newColour);
        }
        UpdatePostItColourLegend();
    }

    // ------------------------------------------------------------------
    // The legend above the board
    // ------------------------------------------------------------------

    /// <summary>
    /// Shows, above the notes, which colour means which category, for each palette colour used on the board.
    /// Hidden while every note is the standard yellow.
    /// </summary>
    private void UpdatePostItColourLegend()
    {
        PostItBoardColourLegend.Children.Clear();

        // The colours of the notes on the board, in palette order.
        var usedColourIds = new HashSet<string>(
            GetPostItNotesOnBoard()
                .Select(postItNote => GetPostItNoteColour(((PostItNoteState)postItNote.Tag).BookshelfEntry)?.Id)
                .OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        var usedColours = (appSettings.PostItColours ?? new List<PostItColour>())
            .Where(paletteColour => usedColourIds.Contains(paletteColour.Id))
            .ToList();

        foreach (var usedColour in usedColours)
        {
            var legendSwatch = new Border
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 5, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                Background = PostItColour.CreateFrozenBrush(PostItColour.ParseColourOrStandardYellow(usedColour.Hex)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var legendText = new TextBlock
            {
                // The category is what the colour means; a colour without one shows its name instead.
                Text = string.IsNullOrWhiteSpace(usedColour.Category) ? usedColour.Name : usedColour.Category,
                Foreground = PostItBoardSubtitleText.Foreground,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var legendEntry = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 16, 2),
                ToolTip = usedColour.MenuText,
            };
            legendEntry.Children.Add(legendSwatch);
            legendEntry.Children.Add(legendText);
            PostItBoardColourLegend.Children.Add(legendEntry);
        }

        PostItBoardColourLegend.Visibility = usedColours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // The settings page
    // ------------------------------------------------------------------

    /// <summary>
    /// Tools → Post-it colours (also at the bottom of a note's Colour menu): lets the user register colours with a
    /// category, saves them, and repaints the board so changed colours show at once.
    /// </summary>
    private void PostItColourSettingsMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var colourSettingsWindow = new PostItColourSettingsWindow(appSettings.PostItColours ?? new List<PostItColour>()) { Owner = this };
        if (colourSettingsWindow.ShowDialog() != true)
        {
            return;
        }

        appSettings.PostItColours = colourSettingsWindow.EditedColours;
        SaveSettingsOrWarn();

        // Repaint the notes of the board (also while a document covers it): a changed hex value shows at once,
        // a removed colour turns its notes yellow.
        if (currentBookshelf.IsPostItBoard)
        {
            foreach (var postItNote in GetPostItNotesOnBoard())
            {
                var noteState = (PostItNoteState)postItNote.Tag;
                PaintPostItNote(postItNote, noteState, GetPostItNoteColour(noteState.BookshelfEntry));
            }
            UpdatePostItColourLegend();
        }
    }
}
