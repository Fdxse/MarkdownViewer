using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Rendering;
using MarkdownViewer.Todo;

namespace MarkdownViewer;

/// <summary>
/// Post-it boards: a folder bookshelf whose Markdown files are shown as small yellow notes in the
/// document area, one note per file. A note whose text fits is simply read on the board. A note whose
/// text does not fit gets a "Click to read more" footer, and clicking it opens the file as a normal
/// document. The bar above that document leads back to the board.
/// </summary>
public partial class MainWindow
{
    /// <summary>Tag of the "Show as post-it board" item in the shelf menu (folder shelves only).</summary>
    private const string ShowAsPostItBoardMenuTag = "ShowAsPostItBoard";

    /// <summary>Width of one note on screen, in device-independent pixels.</summary>
    private const double PostItNoteWidth = 230;

    /// <summary>Height of one note on screen (text area plus the file name footer).</summary>
    private const double PostItNoteHeight = 230;

    /// <summary>Height of the footer at the bottom of a note that shows the file name.</summary>
    private const double PostItFooterHeight = 24;

    /// <summary>
    /// The document is laid out larger than the note and then shrunk by this factor, so headings, lists
    /// and code keep their usual proportions but more text fits on the small note.
    /// </summary>
    private const double PostItContentScale = 0.7;

    /// <summary>
    /// Only this many characters of a file are rendered on its note. A longer file can never fit, so the
    /// rest is not worth the rendering time; it is always shown as "Click to read more".
    /// </summary>
    private const int MaximumPostItTextLength = 4000;

    /// <summary>Most notes drawn on one board, so a folder with thousands of files does not freeze the app.</summary>
    private const int MaximumPostItNoteCount = 300;

    /// <summary>Post-it yellow: the colour of the notes.</summary>
    private static readonly Brush PostItNoteBrush = CreateFrozenPostItBrush("#FFF68F");

    /// <summary>A slightly darker yellow for the strip along the top of a note (where the glue would be).</summary>
    private static readonly Brush PostItGlueStripBrush = CreateFrozenPostItBrush("#F7EB6E");

    /// <summary>Muted brown-grey for the file name in a note's footer.</summary>
    private static readonly Brush PostItFooterTextBrush = CreateFrozenPostItBrush("#7A7048");

    /// <summary>Darker text for the "Click to read more" hint, so it stands out from the file name.</summary>
    private static readonly Brush PostItReadMoreBrush = CreateFrozenPostItBrush("#5C5300");

    /// <summary>True while the post-it board covers the document area.</summary>
    private bool isPostItBoardShown;

    /// <summary>
    /// The file paths and save times the notes on the board were drawn from. The board is only redrawn
    /// when this changes, so switching back to the window does not re-render every note each time.
    /// </summary>
    private string postItBoardDrawnFromSignature = string.Empty;

    /// <summary>What a note on the board knows about itself; kept in the note's <see cref="FrameworkElement.Tag"/>.</summary>
    private sealed class PostItNoteState
    {
        /// <summary>The file the note shows.</summary>
        public required BookshelfEntry BookshelfEntry { get; init; }

        /// <summary>True if only the start of a very long file was rendered (so it certainly does not fit).</summary>
        public required bool WasTextCutShort { get; init; }

        /// <summary>Fade at the bottom of the text, shown only when the text runs past the end of the note.</summary>
        public required FrameworkElement OverflowFade { get; init; }

        /// <summary>The "Click to read more" hint in the footer, shown only when the text does not fit.</summary>
        public required FrameworkElement ReadMoreHint { get; init; }

        /// <summary>The note's tilt; its angle is changed when the user tilts the note.</summary>
        public required RotateTransform TiltTransform { get; init; }

        /// <summary>True if the text does not fit on the note, so a click opens it as a document.</summary>
        public bool IsOverflowing { get; set; }
    }

    // ------------------------------------------------------------------
    // Showing and hiding the board
    // ------------------------------------------------------------------

    /// <summary>Called whenever another shelf is shown: a post-it board covers the document area, other shelves do not.</summary>
    private void ShowOrHidePostItBoardForCurrentShelf()
    {
        if (currentBookshelf.IsPostItBoard)
        {
            // Always draw a freshly chosen board from scratch.
            postItBoardDrawnFromSignature = string.Empty;
            ShowPostItBoard();
        }
        else
        {
            HidePostItBoard();
        }
    }

    /// <summary>Puts the board of the shown shelf over the document area, redrawing the notes if the files changed.</summary>
    private void ShowPostItBoard()
    {
        if (!currentBookshelf.IsPostItBoard)
        {
            return;
        }

        // Leave edit mode first: the editor would otherwise sit hidden under the board.
        // (Unsaved typing is kept; opening another note asks about it as usual.)
        if (isEditMode)
        {
            SetEditMode(false);
        }

        isPostItBoardShown = true;
        PostItBoardView.Visibility = Visibility.Visible;
        // The document (and editor) underneath are hidden, so Find, zoom and the mouse wheel cannot reach them by accident.
        ContentGrid.Visibility = Visibility.Collapsed;
        RefreshPostItBoardIfChanged();
        UpdateBackToPostItBoardBar();
    }

    /// <summary>Shows the document area again instead of the board. Safe to call when no board is shown.</summary>
    private void HidePostItBoard()
    {
        isPostItBoardShown = false;
        PostItBoardView.Visibility = Visibility.Collapsed;
        ContentGrid.Visibility = Visibility.Visible;
        UpdateBackToPostItBoardBar();
    }

    /// <summary>Shows the "Back to the post-it board" bar while a post-it board shelf is chosen but a document is shown.</summary>
    private void UpdateBackToPostItBoardBar()
    {
        // currentBookshelf is checked for null because a document can be shown before the bookshelf is set up at startup.
        bool isBoardHiddenBehindDocument = currentBookshelf != null && currentBookshelf.IsPostItBoard && !isPostItBoardShown;
        BackToPostItBoardBar.Visibility = isBoardHiddenBehindDocument ? Visibility.Visible : Visibility.Collapsed;
        BackToPostItBoardText.Text = isBoardHiddenBehindDocument ? $"Post-it board \"{currentBookshelf!.Name}\"" : string.Empty;
    }

    /// <summary>The "Back to the post-it board" button above a document.</summary>
    private void BackToPostItBoardButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        ShowPostItBoard();
    }

    /// <summary>Shelf menu → New post-it board: like a new folder bookshelf, but shown as notes.</summary>
    private void NewPostItBoardMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        CreateFolderShelf(asPostItBoard: true);
    }

    /// <summary>Shelf menu → Show as post-it board (folder shelves only): switches between a plain list and a board.</summary>
    private void ShowAsPostItBoardMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (!currentBookshelf.IsFolderShelf)
        {
            return;
        }

        currentBookshelf.IsPostItBoard = !currentBookshelf.IsPostItBoard;
        SaveSettingsOrWarn();
        // Redraws the drop-down (its "(post-its)" label), restarts the folder watcher and shows or hides the board.
        ShowBookshelf(currentBookshelf);
    }

    // ------------------------------------------------------------------
    // Drawing the notes
    // ------------------------------------------------------------------

    /// <summary>
    /// Redraws the notes if a file was added, removed or saved since they were drawn. Does nothing while
    /// the board is not on screen (it is brought up to date when it is shown again).
    /// </summary>
    private void RefreshPostItBoardIfChanged()
    {
        // While a note is being dragged the board is left alone (redrawing would pull the note away from the mouse);
        // it is brought up to date when the note is dropped.
        if (!isPostItBoardShown || !currentBookshelf.IsPostItBoard || isPostItNoteBeingDragged)
        {
            return;
        }

        string currentSignature = BuildPostItBoardSignature();
        if (currentSignature == postItBoardDrawnFromSignature)
        {
            return;
        }

        postItBoardDrawnFromSignature = currentSignature;
        RebuildPostItNotes();
    }

    /// <summary>
    /// One line per file on the board with its path and last save time, used to notice changes. The lines
    /// are sorted, so moving a note around on the board does not count as a change (and does not redraw it).
    /// </summary>
    private string BuildPostItBoardSignature()
    {
        var signatureLines = new List<string>();
        foreach (var bookshelfEntry in currentBookshelf.Entries.Take(MaximumPostItNoteCount))
        {
            long lastSavedTicks;
            try
            {
                lastSavedTicks = File.GetLastWriteTimeUtc(bookshelfEntry.FilePath).Ticks;
            }
            catch (Exception)
            {
                // A file that cannot be inspected is drawn as an error note; it still takes part in the signature.
                lastSavedTicks = 0;
            }
            signatureLines.Add($"{bookshelfEntry.FilePath}|{lastSavedTicks}");
        }
        signatureLines.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', signatureLines);
    }

    /// <summary>Clears the board and draws one note per Markdown file of the shown shelf, plus the board's title.</summary>
    private void RebuildPostItNotes()
    {
        PostItNotesCanvas.Children.Clear();
        // Lay the (now empty) board out first, so its width is known when new notes are put into rows.
        PostItBoardView.UpdateLayout();

        PostItBoardTitleText.Text = currentBookshelf.Name;
        int fileCount = currentBookshelf.Entries.Count;
        string noteCountText = fileCount == 1 ? "1 note" : $"{fileCount:N0} notes";
        string subfolderText = currentBookshelf.IncludeSubfolders ? " and its subfolders" : string.Empty;
        string cutShortText = fileCount > MaximumPostItNoteCount ? $" (only the first {MaximumPostItNoteCount} are shown)" : string.Empty;
        PostItBoardSubtitleText.Text =
            $"{noteCountText} from {currentBookshelf.FolderPath}{subfolderText}{cutShortText}. " +
            "Click a note marked \"Click to read more\" to open it as a document; double-click any note to open it. " +
            "Drag a note anywhere, also on top of other notes; tilt it with the round handle in its corner (or Shift + mouse wheel). " +
            "Right-click a note for more. Ctrl+N adds a new note to this folder.";

        // No files: say why (an empty folder, or a folder that is gone).
        PostItBoardEmptyHint.Visibility = fileCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        PostItBoardEmptyHint.Text = Directory.Exists(currentBookshelf.FolderPath)
            ? "There are no Markdown files in this folder yet. Save a .md file there and its note appears here by itself."
            : "The folder was not found. It may have been moved, renamed or deleted, or its drive is not connected.";

        var drawnNotes = new List<Border>();
        foreach (var bookshelfEntry in currentBookshelf.Entries.Take(MaximumPostItNoteCount))
        {
            var postItNote = CreatePostItNote(bookshelfEntry);
            drawnNotes.Add(postItNote);
            PostItNotesCanvas.Children.Add(postItNote);
        }
        // Each note to its saved place, tilt and stacking (or into rows if it has none yet).
        PlacePostItNotesOnBoard(drawnNotes);
    }

    /// <summary>Builds one yellow note showing the rendered Markdown of a file, with its file name at the bottom.</summary>
    private Border CreatePostItNote(BookshelfEntry bookshelfEntry)
    {
        // Read the file. A file that cannot be read gets a note saying so instead of breaking the board.
        string noteMarkdown;
        bool wasTextCutShort = false;
        try
        {
            noteMarkdown = File.ReadAllText(bookshelfEntry.FilePath);
            if (noteMarkdown.Length > MaximumPostItTextLength)
            {
                noteMarkdown = noteMarkdown[..MaximumPostItTextLength];
                wasTextCutShort = true;
            }
        }
        catch (Exception readError)
        {
            noteMarkdown = $"*Could not read the file:*\n\n{readError.Message}";
        }

        // The text of the note: the normal document renderer, laid out larger and scaled down onto the note.
        // Links and check boxes are not clickable on a note (the whole note is the click target); open it to use them.
        double textAreaHeight = PostItNoteHeight - PostItFooterHeight;
        var noteRenderer = new MarkdownFlowDocumentRenderer(Path.GetDirectoryName(bookshelfEntry.FilePath), (_, _) => { });
        FlowDocument noteDocument;
        try
        {
            noteDocument = noteRenderer.Render(noteMarkdown);
        }
        catch (Exception renderError)
        {
            noteDocument = new FlowDocument(new Paragraph(new Run($"Could not render the note:\n{renderError.Message}")));
        }
        // Small margins and no white page, so the yellow shows through.
        noteDocument.PagePadding = new Thickness(16, 12, 16, 4);
        noteDocument.Background = Brushes.Transparent;

        var noteTextViewer = new FlowDocumentScrollViewer
        {
            Document = noteDocument,
            // Hidden (not Disabled) keeps the viewer measuring the full text height, which is how overflow is detected.
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            IsSelectionEnabled = false,
            Focusable = false,
            // Clicks go straight through to the note itself.
            IsHitTestVisible = false,
            Width = PostItNoteWidth / PostItContentScale,
            Height = textAreaHeight / PostItContentScale,
            LayoutTransform = new ScaleTransform(PostItContentScale, PostItContentScale),
        };

        // A fade from transparent to yellow over the last lines, shown when the text runs past the bottom.
        var overflowFade = new Border
        {
            Height = 36,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Background = new LinearGradientBrush(Color.FromArgb(0, 0xFF, 0xF6, 0x8F), Color.FromRgb(0xFF, 0xF6, 0x8F), 90),
        };

        // Footer: the file name on the left and, for notes that do not fit, "Click to read more" on the right.
        var readMoreHint = new TextBlock
        {
            Text = "Click to read more ⤢",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = PostItReadMoreBrush,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(readMoreHint, Dock.Right);
        var fileNameText = new TextBlock
        {
            // The name without ".md" reads more like a note title; files in subfolders keep their "subfolder\" prefix.
            Text = Path.ChangeExtension(bookshelfEntry.DisplayName, null),
            FontSize = 11,
            Foreground = PostItFooterTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var noteFooter = new DockPanel
        {
            Height = PostItFooterHeight,
            Margin = new Thickness(11, 0, 10, 2),
            LastChildFill = true,
        };
        noteFooter.Children.Add(readMoreHint);
        noteFooter.Children.Add(fileNameText);

        // Text area (with the fade on top of it) above the footer.
        var textArea = new Grid { Height = textAreaHeight, ClipToBounds = true };
        textArea.Children.Add(noteTextViewer);
        textArea.Children.Add(overflowFade);
        var noteLayout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(noteFooter, Dock.Bottom);
        noteLayout.Children.Add(noteFooter);
        noteLayout.Children.Add(textArea);

        // A round handle in the top-right corner: drag it to tilt the note.
        var tiltHandle = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 2, 4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Background = PostItGlueStripBrush,
            BorderBrush = PostItFooterTextBrush,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Drag to tilt the note (or hold Shift and turn the mouse wheel)",
            Visibility = Visibility.Hidden,
            Child = new TextBlock
            {
                Text = "↻",
                FontSize = 12,
                Foreground = PostItReadMoreBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        tiltHandle.MouseLeftButtonDown += PostItTiltHandle_MouseLeftButtonDown;
        var noteLayoutWithTiltHandle = new Grid();
        noteLayoutWithTiltHandle.Children.Add(noteLayout);
        noteLayoutWithTiltHandle.Children.Add(tiltHandle);

        // The tilt: a small one worked out from the name, until the user tilts the note (then the saved tilt is used).
        var tiltTransform = new RotateTransform(GetPostItTiltAngle(bookshelfEntry.DisplayName));

        var noteState = new PostItNoteState
        {
            BookshelfEntry = bookshelfEntry,
            TiltTransform = tiltTransform,
            WasTextCutShort = wasTextCutShort,
            OverflowFade = overflowFade,
            ReadMoreHint = readMoreHint,
        };

        // The note itself: yellow paper with a darker glue strip on top, a soft shadow and a slight tilt.
        var postItNote = new Border
        {
            Width = PostItNoteWidth,
            Height = PostItNoteHeight,
            Background = PostItNoteBrush,
            BorderBrush = PostItGlueStripBrush,
            BorderThickness = new Thickness(0, 6, 0, 0),
            Child = noteLayoutWithTiltHandle,
            Tag = noteState,
            ToolTip = bookshelfEntry.FilePath,
            Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 3, Direction = 290, Opacity = 0.25 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = tiltTransform,
        };
        tiltHandle.Tag = postItNote;
        postItNote.MouseLeftButtonUp += PostItNote_MouseLeftButtonUp;
        postItNote.MouseLeftButtonDown += PostItNote_MouseLeftButtonDown;
        // Moving and tilting a note (MainWindow.PostItBoardLayout.cs).
        postItNote.MouseMove += PostItNote_MouseMove;
        postItNote.LostMouseCapture += PostItNote_LostMouseCapture;
        postItNote.PreviewMouseWheel += PostItNote_PreviewMouseWheel;
        // The tilt handle only shows while the mouse is over the note, so the board stays clean.
        postItNote.MouseEnter += (_, _) => tiltHandle.Visibility = Visibility.Visible;
        postItNote.MouseLeave += (_, _) => tiltHandle.Visibility = Visibility.Hidden;
        postItNote.ContextMenu = CreatePostItNoteContextMenu(bookshelfEntry, postItNote);

        // Find out whether the text fits once the viewer has laid it out. The scroll viewer inside reports a
        // change of text height (also later, e.g. when a Mermaid diagram finishes drawing), so listen to that.
        noteTextViewer.Loaded += (_, _) =>
        {
            var innerScrollViewer = FindDescendant<ScrollViewer>(noteTextViewer);
            if (innerScrollViewer == null)
            {
                SetPostItNoteOverflow(postItNote, noteState, textOverflows: wasTextCutShort);
                return;
            }

            innerScrollViewer.ScrollChanged += (_, _) => UpdatePostItNoteOverflow(postItNote, noteState, innerScrollViewer);
            UpdatePostItNoteOverflow(postItNote, noteState, innerScrollViewer);
        };

        // Until the layout says otherwise, a cut-short file is known not to fit.
        SetPostItNoteOverflow(postItNote, noteState, textOverflows: wasTextCutShort);
        return postItNote;
    }

    /// <summary>Works out from the viewer's scroll extent whether the note's text is taller than the note.</summary>
    private static void UpdatePostItNoteOverflow(Border postItNote, PostItNoteState noteState, ScrollViewer innerScrollViewer)
    {
        // A pixel of slack, so rounding does not mark a note that fits exactly as overflowing.
        bool textIsTallerThanNote = innerScrollViewer.ExtentHeight > innerScrollViewer.ViewportHeight + 1;
        SetPostItNoteOverflow(postItNote, noteState, textOverflows: noteState.WasTextCutShort || textIsTallerThanNote);
    }

    /// <summary>Marks a note as fitting or not: the fade, the "Click to read more" hint and the hand cursor follow it.</summary>
    private static void SetPostItNoteOverflow(Border postItNote, PostItNoteState noteState, bool textOverflows)
    {
        noteState.IsOverflowing = textOverflows;
        noteState.OverflowFade.Visibility = textOverflows ? Visibility.Visible : Visibility.Collapsed;
        noteState.ReadMoreHint.Visibility = textOverflows ? Visibility.Visible : Visibility.Collapsed;
        postItNote.Cursor = textOverflows ? Cursors.Hand : null;
    }

    /// <summary>
    /// A small tilt between -1.5 and +1.5 degrees so the board looks like real notes. Worked out from the
    /// file name (not random), so each note keeps its tilt when the board is redrawn.
    /// </summary>
    private static double GetPostItTiltAngle(string fileDisplayName)
    {
        int characterSum = fileDisplayName.Sum(fileNameCharacter => (int)fileNameCharacter);
        int tiltStep = characterSum % 7; // 0..6
        return (tiltStep - 3) * 0.5;
    }

    /// <summary>Right-click menu of a note: open it, open it for editing, or show it in Explorer.</summary>
    private ContextMenu CreatePostItNoteContextMenu(BookshelfEntry bookshelfEntry, Border postItNote)
    {
        var openMenuItem = new MenuItem { Header = "_Open as document" };
        openMenuItem.Click += (_, _) => OpenPostItNoteAsDocument(bookshelfEntry, startEditing: false);

        var editMenuItem = new MenuItem { Header = "_Edit", InputGestureText = "Ctrl+E" };
        editMenuItem.Click += (_, _) => OpenPostItNoteAsDocument(bookshelfEntry, startEditing: true);

        var showInFolderMenuItem = new MenuItem { Header = "Show in _folder" };
        showInFolderMenuItem.Click += (_, _) => ShowFileInExplorer(bookshelfEntry.FilePath);

        var noteContextMenu = new ContextMenu();
        noteContextMenu.Items.Add(openMenuItem);
        noteContextMenu.Items.Add(editMenuItem);
        // Archive and Move to trash.
        AddArchiveAndTrashMenuItems(noteContextMenu, bookshelfEntry);
        noteContextMenu.Items.Add(new Separator());
        noteContextMenu.Items.Add(showInFolderMenuItem);
        noteContextMenu.Items.Add(new Separator());
        // Tilting, stacking and tidying up the board.
        noteContextMenu.Items.Add(CreatePostItArrangeMenu(postItNote));
        return noteContextMenu;
    }

    // ------------------------------------------------------------------
    // Clicking a note
    // ------------------------------------------------------------------

    /// <summary>
    /// A double-click opens any note as a document, also one whose text fits. A single press may become a
    /// click or, if the mouse moves with the button held down, a move of the note.
    /// </summary>
    private void PostItNote_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (sender is not Border { Tag: PostItNoteState noteState } pressedNote)
        {
            return;
        }

        if (mouseDetails.ClickCount == 2)
        {
            mouseDetails.Handled = true;
            EndPostItNotePress();
            OpenPostItNoteAsDocument(noteState.BookshelfEntry, startEditing: false);
            return;
        }

        BeginPostItNotePress(pressedNote, mouseDetails);
    }

    /// <summary>A single click opens a note as a document only if its text does not fit on the note.</summary>
    private void PostItNote_MouseLeftButtonUp(object sender, MouseButtonEventArgs mouseDetails)
    {
        // A note that was moved or tilted is now saved, and the release does not count as a click.
        var finishedAction = EndPostItNotePress();
        if (finishedAction is PostItNoteMouseAction.Moving or PostItNoteMouseAction.Tilting)
        {
            mouseDetails.Handled = true;
            return;
        }

        if (sender is Border { Tag: PostItNoteState noteState } && noteState.IsOverflowing)
        {
            mouseDetails.Handled = true;
            OpenPostItNoteAsDocument(noteState.BookshelfEntry, startEditing: false);
        }
    }

    /// <summary>
    /// Opens a note's file as a normal document (which hides the board and shows the "Back to the post-it
    /// board" bar), optionally straight into edit mode.
    /// </summary>
    private void OpenPostItNoteAsDocument(BookshelfEntry bookshelfEntry, bool startEditing)
    {
        bool isAlreadyOpen = currentFilePath != null && bookshelfEntry.HasPath(currentFilePath);
        if (isAlreadyOpen)
        {
            // Already loaded (and maybe edited): just bring it back into view instead of reading it again.
            HidePostItBoard();
        }
        else
        {
            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }
            // LoadFile shows a message if the file cannot be read; the board then stays on screen.
            LoadFile(bookshelfEntry.FilePath);
            if (currentFilePath == null || !bookshelfEntry.HasPath(currentFilePath))
            {
                return;
            }
        }

        if (startEditing && !isEditMode)
        {
            SetEditMode(true);
        }
    }

    // ------------------------------------------------------------------
    // New notes
    // ------------------------------------------------------------------

    /// <summary>
    /// File → New (Ctrl+N) while a post-it board is shown: asks for a name, creates an empty .md file in the
    /// board's folder right away, and opens it in edit mode. Because the file already exists, Ctrl+S saves
    /// straight into the board's folder without a Save As dialog, and the note appears on the board.
    /// The caller has already asked about unsaved changes in the open document.
    /// </summary>
    private void CreateNewPostItNote()
    {
        string boardFolderPath = currentBookshelf.FolderPath!;
        if (!Directory.Exists(boardFolderPath))
        {
            MessageBox.Show(this,
                $"The folder of this post-it board was not found, so the note cannot be saved there:\n{boardFolderPath}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Suggest a name based on the current time, so just pressing Enter always works.
        string suggestedNoteName = $"Note {DateTime.Now:yyyy-MM-dd HHmm}";
        var namePrompt = new TextPromptWindow("New post-it note",
            $"Name of the new note (saved as a .md file in {boardFolderPath}):", suggestedNoteName) { Owner = this };
        if (namePrompt.ShowDialog() != true)
        {
            return;
        }

        string noteFileName = MakeSafeNoteFileName(namePrompt.AnswerText, suggestedNoteName);
        string newNoteFilePath = GetUnusedFilePath(boardFolderPath, noteFileName, ".md");
        try
        {
            // An empty file: the user types the content in the editor that opens next.
            File.WriteAllText(newNoteFilePath, string.Empty);
        }
        catch (Exception writeError)
        {
            MessageBox.Show(this, $"Could not create the note:\n{newNoteFilePath}\n\n{writeError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // Put the new file on the shelf at once (the folder watcher would do it too, a moment later).
        RefreshFolderShelfAfterChange();
        LoadFile(newNoteFilePath);
        // Only start editing if the new note really opened (LoadFile has already shown any error).
        if (currentFilePath != null && string.Equals(currentFilePath, Path.GetFullPath(newNoteFilePath), StringComparison.OrdinalIgnoreCase))
        {
            SetEditMode(true);
        }
    }

    /// <summary>
    /// Turns what the user typed into a usable file name: characters Windows does not allow are replaced
    /// with "-", a typed ".md" is dropped (it is added again later), and an empty name falls back to the suggestion.
    /// </summary>
    private static string MakeSafeNoteFileName(string typedName, string fallbackName)
    {
        string trimmedName = typedName.Trim();
        if (trimmedName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            trimmedName = trimmedName[..^3].TrimEnd();
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        string safeName = new string(trimmedName
            .Select(nameCharacter => invalidCharacters.Contains(nameCharacter) ? '-' : nameCharacter)
            .ToArray())
            // Windows does not allow a name to end with a dot or space.
            .TrimEnd('.', ' ');
        return safeName.Length == 0 ? fallbackName : safeName;
    }

    /// <summary>
    /// A path in the folder for "name.ext" that no file uses yet: "name.ext", else "name (2).ext", "name (3).ext"...
    /// so creating a note never overwrites an existing one.
    /// </summary>
    private static string GetUnusedFilePath(string folderPath, string baseFileName, string fileExtension)
    {
        string candidatePath = Path.Combine(folderPath, baseFileName + fileExtension);
        int copyNumber = 2;
        while (File.Exists(candidatePath))
        {
            candidatePath = Path.Combine(folderPath, $"{baseFileName} ({copyNumber}){fileExtension}");
            copyNumber++;
        }
        return candidatePath;
    }

    /// <summary>Makes a frozen (thread-safe, faster) brush from a "#RRGGBB" colour.</summary>
    private static Brush CreateFrozenPostItBrush(string hexColour)
    {
        var colourBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColour));
        colourBrush.Freeze();
        return colourBrush;
    }
}
