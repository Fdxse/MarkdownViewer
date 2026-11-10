using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarkdownViewer.Links;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Macros;
using MarkdownViewer.Rendering;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// The main window: shows a rendered Markdown document and, in edit mode,
/// a plain text editor next to it with a live preview.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>File extensions treated as Markdown when opening files or following links.</summary>
    private static readonly string[] MarkdownFileExtensions = { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".txt" };

    /// <summary>Filter string for the open and save dialogs.</summary>
    private const string MarkdownFileDialogFilter =
        "Markdown files (*.md;*.markdown)|*.md;*.markdown;*.mdown;*.mkd;*.mkdn|Text files (*.txt)|*.txt|All files (*.*)|*.*";

    /// <summary>Encoding used when saving: UTF-8 without a byte order mark, the usual choice for Markdown.</summary>
    private static readonly Encoding SaveEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Full path of the open file, or null for a new unsaved document.</summary>
    private string? currentFilePath;

    /// <summary>The text as it was last loaded or saved; used to detect unsaved changes.</summary>
    private string lastSavedText = string.Empty;

    /// <summary>True while the side-by-side editor is visible.</summary>
    private bool isEditMode;

    /// <summary>Set while the editor text is changed by code, so the change is not treated as a user edit.</summary>
    private bool isLoadingText;

    /// <summary>Renderer for the currently displayed document (kept to resolve in-document links).</summary>
    private MarkdownFlowDocumentRenderer? currentRenderer;

    /// <summary>Delays re-rendering while the user is typing, so the preview does not flicker on every key press.</summary>
    private readonly DispatcherTimer previewRefreshTimer;

    /// <summary>Delays reloading after an external change, because editors often write a file in several steps.</summary>
    private readonly DispatcherTimer externalChangeTimer;

    /// <summary>Watches the open file for changes made by other programs.</summary>
    private FileSystemWatcher? openFileWatcher;

    /// <summary>User settings (text macros and the bookshelf), loaded from %APPDATA% at startup.</summary>
    private AppSettings appSettings;

    /// <summary>Expands macro triggers such as "@730" while typing in the editor.</summary>
    private MacroExpander macroExpander;

    /// <summary>Characters that end a macro trigger and make it expand (besides whitespace, which always does).</summary>
    private const string MacroTerminatorCharacters = MacroExpander.TerminatorCharacters;

    /// <summary>
    /// Creates the main window and opens the given file, or shows the welcome page if none is given.
    /// </summary>
    public MainWindow(string? fileToOpen)
    {
        InitializeComponent();

        // Load the macros. A broken settings file should not stop the app, so fall back to defaults.
        appSettings = AppSettings.Load(out string? settingsLoadError);
        macroExpander = new MacroExpander(appSettings.Macros);
        RebuildLinksMenu();
        if (settingsLoadError != null)
        {
            MessageBox.Show(settingsLoadError + "\n\nThe default macros are used for now.",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        previewRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        previewRefreshTimer.Tick += (_, _) =>
        {
            previewRefreshTimer.Stop();
            RenderPreview();
        };

        externalChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        externalChangeTimer.Tick += (_, _) =>
        {
            externalChangeTimer.Stop();
            HandleExternalFileChange();
        };

        // Ctrl+Z outside the editor undoes the last section sort.
        PreviewKeyDown += SectionSort_PreviewKeyDown;

        // Show the bookshelf panel as it was when the app was last closed.
        InitializeBookshelf();

        // Global search: loads search-index.json next to the exe and checks it for changed files, in the background.
        InitializeGlobalSearch();

        if (!string.IsNullOrWhiteSpace(fileToOpen) && File.Exists(fileToOpen))
        {
            LoadFile(fileToOpen);
        }
        else
        {
            ShowWelcomePage();
        }
    }

    // ------------------------------------------------------------------
    // State helpers
    // ------------------------------------------------------------------

    /// <summary>True if the editor holds text that has not been saved.</summary>
    private bool HasUnsavedChanges => MarkdownEditor.Text != lastSavedText;

    /// <summary>Updates the window title and status bar to match the current state.</summary>
    private void UpdateTitleAndStatus()
    {
        string documentName = currentFilePath != null ? Path.GetFileName(currentFilePath) : currentQueryEntry?.Query?.Name ?? "Untitled";
        string unsavedMarker = HasUnsavedChanges ? "*" : string.Empty;
        // The version number shows which release is running.
        Title = $"{documentName}{unsavedMarker} - Markdown Viewer {AppVersionText}";

        // A query result has no file; the status bar says where it came from instead.
        FilePathStatusText.Text = currentFilePath
            ?? (currentQueryEntry != null ? currentQueryEntry.ToolTipText + "  (F5 runs it again, Ctrl+E edits it)" : "Not saved yet");

        // Same word count as in the properties panel (Markdown symbols such as "#" are not counted).
        int wordCount = DocumentStatistics.CountWords(MarkdownEditor.Text);
        string modeName = isEditMode ? "Editing" : "Viewing";
        string modifiedText = HasUnsavedChanges ? "  |  Modified" : string.Empty;
        DocumentInfoStatusText.Text = $"{modeName}  |  {wordCount:N0} words{modifiedText}";
    }

    /// <summary>
    /// Puts text into the editor without it counting as a user edit, then renders it.
    /// <paramref name="queryEntry"/> is set when the text is the result of a query from a query bookshelf.
    /// </summary>
    private void SetDocumentText(string markdownText, string? filePath, BookshelfEntry? queryEntry = null)
    {
        isLoadingText = true;
        try
        {
            currentFilePath = filePath;
            currentQueryEntry = queryEntry;
            if (queryEntry == null)
            {
                currentQueryRunResult = null;
            }
            lastSavedText = markdownText;
            MarkdownEditor.Text = markdownText;
            MarkdownEditor.CaretIndex = 0;
        }
        finally
        {
            isLoadingText = false;
        }

        WatchFile(filePath);
        RenderPreview(keepScrollPosition: false);
        UpdateTitleAndStatus();
        SelectOpenFileInBookshelf();
        // A document that is opened (from a note, the list, a link or a search) replaces the post-it board on screen.
        HidePostItBoard();
    }

    /// <summary>Shows the built-in welcome page (embedded in the exe) as an untitled document.</summary>
    private void ShowWelcomePage()
    {
        using var welcomeStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MarkdownViewer.Welcome.md");
        string welcomeText = welcomeStream != null ? new StreamReader(welcomeStream).ReadToEnd() : "# Markdown Viewer";

        SetDocumentText(welcomeText, filePath: null);
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    /// <summary>
    /// Renders the editor text into the document viewer.
    /// </summary>
    /// <param name="keepScrollPosition">True to stay at the same place in the document (used for live preview).</param>
    private void RenderPreview(bool keepScrollPosition = true)
    {
        // Remember where the user was scrolled to, so typing does not jump the preview to the top.
        var previewScrollViewer = FindDescendant<ScrollViewer>(DocumentReader);
        double previousVerticalOffset = previewScrollViewer?.VerticalOffset ?? 0;

        string? documentFolder = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null;
        currentRenderer = new MarkdownFlowDocumentRenderer(documentFolder, HandleLinkClick, HandleTaskCheckBoxToggled);

        try
        {
            DocumentReader.Document = currentRenderer.Render(MarkdownEditor.Text);
            // Right-click on a heading offers "Sort sections at this level".
            AttachSectionSortMenus(currentRenderer.TopLevelHeadings);
        }
        catch (Exception renderError)
        {
            // Rendering should never crash the app; show the problem in the viewer instead.
            var errorDocument = new FlowDocument(new Paragraph(new Run($"Could not render the document:\n{renderError.Message}")));
            DocumentReader.Document = errorDocument;
        }

        if (keepScrollPosition && previousVerticalOffset > 0)
        {
            // Wait until the new document has been laid out before scrolling back.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                FindDescendant<ScrollViewer>(DocumentReader)?.ScrollToVerticalOffset(previousVerticalOffset);
            });
        }

        // The properties panel follows the text, also while editing.
        UpdatePropertiesPanel();
    }

    /// <summary>Searches the visual tree below an element for the first child of a given type.</summary>
    private static TElement? FindDescendant<TElement>(DependencyObject parentElement) where TElement : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parentElement);
        for (int childIndex = 0; childIndex < childCount; childIndex++)
        {
            var childElement = VisualTreeHelper.GetChild(parentElement, childIndex);
            if (childElement is TElement matchingElement)
            {
                return matchingElement;
            }

            var deeperMatch = FindDescendant<TElement>(childElement);
            if (deeperMatch != null)
            {
                return deeperMatch;
            }
        }
        return null;
    }

    /// <summary>
    /// Handles a click on a link in the rendered document:
    /// "#heading" jumps within the document, Markdown files open in this viewer,
    /// web and mail links open in the default browser or mail program.
    /// </summary>
    private void HandleLinkClick(string linkTarget, MarkdownFlowDocumentRenderer sourceRenderer)
    {
        // Jump to a heading in this document.
        if (linkTarget.StartsWith('#'))
        {
            sourceRenderer.ScrollToHeading(Uri.UnescapeDataString(linkTarget[1..]));
            return;
        }

        var resolvedUri = sourceRenderer.ResolveUri(linkTarget);
        if (resolvedUri == null)
        {
            MessageBox.Show(this, $"Cannot open the link:\n{linkTarget}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (resolvedUri.IsFile)
        {
            OpenLocalLink(resolvedUri.LocalPath, linkTarget);
            return;
        }

        // Only well-known, safe schemes are handed to the operating system.
        if (resolvedUri.Scheme is "http" or "https" or "mailto")
        {
            OpenWithDefaultProgram(resolvedUri.AbsoluteUri);
        }
        else
        {
            MessageBox.Show(this, $"Links of type \"{resolvedUri.Scheme}:\" are not opened for safety reasons:\n{linkTarget}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>Follows a link that points at a file on disk.</summary>
    private void OpenLocalLink(string localPath, string originalLinkTarget)
    {
        if (!File.Exists(localPath))
        {
            MessageBox.Show(this, $"The linked file does not exist:\n{localPath}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Markdown files open right here.
        if (IsMarkdownFile(localPath))
        {
            if (!ConfirmDiscardUnsavedChanges())
            {
                return;
            }
            LoadFile(localPath);

            // Support links like "other.md#section".
            int anchorStart = originalLinkTarget.IndexOf('#');
            if (anchorStart >= 0 && currentRenderer != null)
            {
                string headingId = Uri.UnescapeDataString(originalLinkTarget[(anchorStart + 1)..]);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => currentRenderer?.ScrollToHeading(headingId));
            }
            return;
        }

        // Any other local file could be a program, so ask before opening it.
        var userAnswer = MessageBox.Show(this,
            $"Open this file with its default program?\n\n{localPath}",
            "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (userAnswer == MessageBoxResult.Yes)
        {
            OpenWithDefaultProgram(localPath);
        }
    }

    /// <summary>Opens a URL or file with whatever program Windows associates with it.</summary>
    private void OpenWithDefaultProgram(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Exception openError)
        {
            MessageBox.Show(this, $"Could not open:\n{pathOrUrl}\n\n{openError.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>True if the path has one of the Markdown (or plain text) file extensions.</summary>
    private static bool IsMarkdownFile(string filePath)
    {
        string fileExtension = Path.GetExtension(filePath);
        return MarkdownFileExtensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Loading, saving and file watching
    // ------------------------------------------------------------------

    /// <summary>Reads a file from disk and displays it. Shows an error message if it cannot be read.</summary>
    private void LoadFile(string filePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(filePath);
            // ReadAllText detects UTF-8/UTF-16 byte order marks and defaults to UTF-8.
            string fileText = File.ReadAllText(fullPath);
            SetDocumentText(fileText, fullPath);
        }
        catch (Exception readError)
        {
            MessageBox.Show(this, $"Could not open the file:\n{filePath}\n\n{readError.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Writes the editor text to the given path. Returns true on success.</summary>
    private bool SaveToFile(string filePath)
    {
        try
        {
            // Pause the watcher so our own save is not mistaken for an external change.
            if (openFileWatcher != null)
            {
                openFileWatcher.EnableRaisingEvents = false;
            }

            File.WriteAllText(filePath, MarkdownEditor.Text, SaveEncoding);

            bool isNewPath = !string.Equals(filePath, currentFilePath, StringComparison.OrdinalIgnoreCase);
            currentFilePath = filePath;
            // A query result saved with Save As is an ordinary file from now on.
            currentQueryEntry = null;
            currentQueryRunResult = null;
            lastSavedText = MarkdownEditor.Text;
            WatchFile(filePath);

            // Relative image paths may resolve differently after "Save As" to another folder.
            if (isNewPath)
            {
                RenderPreview();
                SelectOpenFileInBookshelf();
            }
            UpdateTitleAndStatus();
            // New size and modified date of the saved file.
            UpdatePropertiesPanel();
            // Global search should find the new text straight away.
            UpdateSavedFileInSearchIndex(filePath);
            return true;
        }
        catch (Exception writeError)
        {
            WatchFile(currentFilePath);
            MessageBox.Show(this, $"Could not save the file:\n{filePath}\n\n{writeError.Message}", "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>Saves to the current file, or asks for a file name if the document is new. Returns true on success.</summary>
    private bool Save()
    {
        return currentFilePath != null ? SaveToFile(currentFilePath) : SaveAs();
    }

    /// <summary>Asks for a file name and saves there. Returns true on success.</summary>
    private bool SaveAs()
    {
        var saveDialog = new SaveFileDialog
        {
            Filter = MarkdownFileDialogFilter,
            DefaultExt = ".md",
            FileName = currentFilePath != null ? Path.GetFileName(currentFilePath) : "Untitled.md",
            InitialDirectory = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null,
        };

        return saveDialog.ShowDialog(this) == true && SaveToFile(saveDialog.FileName);
    }

    /// <summary>
    /// If there are unsaved changes, asks whether to save them.
    /// Returns false if the user cancelled (the current action should then be aborted).
    /// </summary>
    private bool ConfirmDiscardUnsavedChanges()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        string documentName = currentFilePath != null ? Path.GetFileName(currentFilePath) : "Untitled";
        var userAnswer = MessageBox.Show(this,
            $"Do you want to save the changes to {documentName}?",
            "Markdown Viewer", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        return userAnswer switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    /// <summary>Starts watching the given file for outside changes (stops watching the previous file).</summary>
    private void WatchFile(string? filePath)
    {
        openFileWatcher?.Dispose();
        openFileWatcher = null;

        string? folderPath = filePath != null ? Path.GetDirectoryName(filePath) : null;
        if (filePath == null || folderPath == null || !Directory.Exists(folderPath))
        {
            return;
        }

        openFileWatcher = new FileSystemWatcher(folderPath, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };

        // Watcher events arrive on a background thread; restart the debounce timer on the UI thread.
        void OnFileChanged(object? sender, FileSystemEventArgs changeDetails)
        {
            Dispatcher.BeginInvoke(() =>
            {
                externalChangeTimer.Stop();
                externalChangeTimer.Start();
            });
        }
        openFileWatcher.Changed += OnFileChanged;
        openFileWatcher.Created += OnFileChanged;
        openFileWatcher.Renamed += (sender, renameDetails) => OnFileChanged(sender, renameDetails);
        openFileWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Reloads the file after another program changed it, unless that would overwrite
    /// the user's own unsaved edits.
    /// </summary>
    private void HandleExternalFileChange()
    {
        if (currentFilePath == null || !File.Exists(currentFilePath))
        {
            return;
        }

        string diskText;
        try
        {
            diskText = File.ReadAllText(currentFilePath);
        }
        catch (IOException)
        {
            // The other program may still be writing; try again shortly.
            externalChangeTimer.Start();
            return;
        }

        if (diskText == lastSavedText)
        {
            return; // nothing actually changed
        }

        if (HasUnsavedChanges)
        {
            // Never silently throw away the user's edits; just tell them.
            DocumentInfoStatusText.Text = "File changed on disk - press F5 to reload (your edits will be lost)";
            return;
        }

        // Keep the scroll position so a live-updating file does not jump to the top.
        isLoadingText = true;
        try
        {
            lastSavedText = diskText;
            MarkdownEditor.Text = diskText;
        }
        finally
        {
            isLoadingText = false;
        }
        RenderPreview(keepScrollPosition: true);
        UpdateTitleAndStatus();
    }

    // ------------------------------------------------------------------
    // Edit mode
    // ------------------------------------------------------------------

    /// <summary>Shows or hides the side-by-side editor.</summary>
    private void SetEditMode(bool enableEditing)
    {
        isEditMode = enableEditing;

        // Split the window evenly when editing; give all space to the viewer otherwise.
        EditorColumn.Width = enableEditing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SplitterColumn.Width = enableEditing ? GridLength.Auto : new GridLength(0);
        MarkdownEditor.Visibility = enableEditing ? Visibility.Visible : Visibility.Collapsed;
        EditorSplitter.Visibility = enableEditing ? Visibility.Visible : Visibility.Collapsed;
        EditModeMenuItem.IsChecked = enableEditing;

        if (enableEditing)
        {
            // The editor sits under the post-it board, so leave the board to edit the open document.
            HidePostItBoard();
            MarkdownEditor.Focus();
        }
        else
        {
            // Make sure the viewer shows the latest text when leaving edit mode.
            previewRefreshTimer.Stop();
            RenderPreview();
        }

        UpdateTitleAndStatus();
    }

    /// <summary>Called on every change in the editor; schedules a preview refresh.</summary>
    private void MarkdownEditor_TextChanged(object sender, TextChangedEventArgs changeDetails)
    {
        if (isLoadingText)
        {
            return;
        }

        ExpandMacroIfJustTyped(changeDetails);

        previewRefreshTimer.Stop();
        previewRefreshTimer.Start();
        UpdateTitleAndStatus();
    }

    // ------------------------------------------------------------------
    // Macros
    // ------------------------------------------------------------------

    /// <summary>
    /// If the user just typed a space, Enter, Tab or punctuation right after a macro trigger
    /// (for example "@730"), replaces the trigger with the macro's replacement text.
    /// </summary>
    private void ExpandMacroIfJustTyped(TextChangedEventArgs changeDetails)
    {
        if (!macroExpander.HasMacros || changeDetails.Changes.Count != 1)
        {
            return;
        }

        // Only react to plain typing: a short insertion that removed nothing (not a paste or a replace).
        var textChange = changeDetails.Changes.First();
        if (textChange.RemovedLength != 0 || textChange.AddedLength is < 1 or > 2)
        {
            return;
        }

        string editorText = MarkdownEditor.Text;
        int insertedAtIndex = textChange.Offset;
        string insertedText = editorText.Substring(insertedAtIndex, textChange.AddedLength);

        // Enter inserts "\r\n", so up to two characters are accepted, but all of them must end a word.
        bool insertedOnlyTerminators = insertedText.All(insertedCharacter =>
            char.IsWhiteSpace(insertedCharacter) || MacroTerminatorCharacters.Contains(insertedCharacter));
        if (!insertedOnlyTerminators || insertedAtIndex == 0)
        {
            return;
        }

        // Look only at the current line, up to the character that was just typed.
        int lineStartIndex = editorText.LastIndexOf('\n', insertedAtIndex - 1) + 1;
        string lineTextBeforeInsertion = editorText[lineStartIndex..insertedAtIndex];

        if (!macroExpander.TryExpandAtEnd(lineTextBeforeInsertion, out int triggerStartInLine, out int triggerLength, out string expandedText))
        {
            return;
        }

        int triggerStartIndex = lineStartIndex + triggerStartInLine;
        string triggerText = editorText.Substring(triggerStartIndex, triggerLength);

        // Changing the text from inside TextChanged is not allowed, so do the replacement right after.
        Dispatcher.BeginInvoke(() =>
        {
            // Make sure nothing else changed the text in the meantime.
            if (MarkdownEditor.Text.Length < triggerStartIndex + triggerLength ||
                MarkdownEditor.Text.Substring(triggerStartIndex, triggerLength) != triggerText)
            {
                return;
            }

            // Replacing through the selection keeps it as one undo step: Ctrl+Z brings "@730" back.
            MarkdownEditor.Select(triggerStartIndex, triggerLength);
            MarkdownEditor.SelectedText = expandedText;

            // Put the cursor after the separator the user typed, where it was before.
            MarkdownEditor.Select(triggerStartIndex + expandedText.Length + insertedText.Length, 0);
        });
    }

    /// <summary>Tools → Macros: lets the user edit the macros and saves them to the settings file.</summary>
    private void MacroSettingsMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var macroSettingsWindow = new MacroSettingsWindow(appSettings.Macros) { Owner = this };
        if (macroSettingsWindow.ShowDialog() != true)
        {
            return;
        }

        appSettings.Macros = macroSettingsWindow.EditedMacros;
        macroExpander = new MacroExpander(appSettings.Macros);

        try
        {
            appSettings.Save();
        }
        catch (Exception saveError)
        {
            // The new macros still work for this session even if they could not be stored.
            MessageBox.Show(this, $"The macros are active now, but could not be saved:\n{AppSettings.SettingsFilePath}\n\n{saveError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Links menu
    // ------------------------------------------------------------------

    /// <summary>
    /// Fills the Links menu: one item per saved link, then "Edit links..." at the bottom.
    /// Called at startup and whenever the links have been edited.
    /// </summary>
    private void RebuildLinksMenu()
    {
        LinksMenu.Items.Clear();

        foreach (var savedLink in appSettings.Links)
        {
            var linkMenuItem = new MenuItem
            {
                // A single "_" in a menu header marks the access key, so double it to show it literally.
                Header = savedLink.Name.Replace("_", "__"),
                ToolTip = savedLink.Url,
            };
            linkMenuItem.Click += (_, _) => OpenWithDefaultProgram(savedLink.GetOpenableAddress());
            LinksMenu.Items.Add(linkMenuItem);
        }

        // Without any links, show a disabled hint so the menu does not look broken.
        if (appSettings.Links.Count == 0)
        {
            LinksMenu.Items.Add(new MenuItem { Header = "(no saved links yet)", IsEnabled = false });
        }

        LinksMenu.Items.Add(new Separator());
        var editLinksMenuItem = new MenuItem { Header = "_Edit links..." };
        editLinksMenuItem.Click += EditLinksMenuItem_Click;
        LinksMenu.Items.Add(editLinksMenuItem);
    }

    /// <summary>Links → Edit links: lets the user edit the saved links and stores them in the settings file.</summary>
    private void EditLinksMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        var linkSettingsWindow = new LinkSettingsWindow(appSettings.Links) { Owner = this };
        if (linkSettingsWindow.ShowDialog() != true)
        {
            return;
        }

        appSettings.Links = linkSettingsWindow.EditedLinks;
        RebuildLinksMenu();

        try
        {
            appSettings.Save();
        }
        catch (Exception saveError)
        {
            // The new links still show in the menu for this session even if they could not be stored.
            MessageBox.Show(this, $"The links are updated now, but could not be saved:\n{AppSettings.SettingsFilePath}\n\n{saveError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------
    // Command handlers (menu items and keyboard shortcuts)
    // ------------------------------------------------------------------

    /// <summary>File → New: starts an empty document in edit mode.</summary>
    private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }
        SetDocumentText(string.Empty, filePath: null);
        SetEditMode(true);
    }

    /// <summary>File → Open: shows the open dialog.</summary>
    private void OpenCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Filter = MarkdownFileDialogFilter,
            InitialDirectory = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null,
        };

        if (openDialog.ShowDialog(this) == true)
        {
            LoadFile(openDialog.FileName);
        }
    }

    /// <summary>File → Save.</summary>
    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails) => Save();

    /// <summary>File → Save As.</summary>
    private void SaveAsCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails) => SaveAs();

    /// <summary>File → Exit.</summary>
    private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails) => Close();

    /// <summary>View → Edit mode (Ctrl+E). For a query result, opens the query's settings page instead of the editor.</summary>
    private void ToggleEditModeCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (currentQueryEntry != null && !isEditMode)
        {
            EditQuery(currentQueryEntry);
            return;
        }
        SetEditMode(!isEditMode);
    }

    /// <summary>File → Reload (F5) is only available when a saved file or a query result is open.</summary>
    private void ReloadCommand_CanExecute(object sender, CanExecuteRoutedEventArgs canExecuteDetails)
    {
        canExecuteDetails.CanExecute = currentFilePath != null || currentQueryEntry != null;
    }

    /// <summary>File → Reload (F5): re-reads the file (or runs the query again), asking first if there are unsaved edits.</summary>
    private void ReloadCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        if (currentFilePath == null && currentQueryEntry == null)
        {
            return;
        }

        if (HasUnsavedChanges)
        {
            var userAnswer = MessageBox.Show(this, "Reload the file and discard your unsaved changes?",
                "Markdown Viewer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (userAnswer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (currentQueryEntry != null)
        {
            _ = RunQueryAndShowResultAsync(currentQueryEntry);
        }
        else
        {
            LoadFile(currentFilePath!);
        }
    }

    /// <summary>View → Reset zoom: back to 100 %.</summary>
    private void ResetZoomMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        DocumentReader.Zoom = 100;
    }

    /// <summary>File → Print: renders a fresh copy of the document sized to the printer page.</summary>
    private void PrintCommand_Executed(object sender, ExecutedRoutedEventArgs commandDetails)
    {
        var printDialog = new PrintDialog();
        if (printDialog.ShowDialog() != true)
        {
            return;
        }

        // A separate document is needed: the one on screen is owned by the viewer.
        string? documentFolder = currentFilePath != null ? Path.GetDirectoryName(currentFilePath) : null;
        var printRenderer = new MarkdownFlowDocumentRenderer(documentFolder, (_, _) => { });
        var printDocument = printRenderer.Render(MarkdownEditor.Text);

        // Fit the document to the printable page with a single column.
        printDocument.PageWidth = printDialog.PrintableAreaWidth;
        printDocument.PageHeight = printDialog.PrintableAreaHeight;
        printDocument.ColumnWidth = printDialog.PrintableAreaWidth;
        printDocument.PagePadding = new Thickness(60);

        string documentName = currentFilePath != null ? Path.GetFileName(currentFilePath) : currentQueryEntry?.Query?.Name ?? "Markdown document";
        printDialog.PrintDocument(((IDocumentPaginatorSource)printDocument).DocumentPaginator, documentName);
    }

    // ------------------------------------------------------------------
    // Drag and drop, closing
    // ------------------------------------------------------------------

    /// <summary>Shows the "copy" cursor when a file is dragged over the window.</summary>
    private void Window_PreviewDragOver(object sender, DragEventArgs dragDetails)
    {
        if (dragDetails.Data.GetDataPresent(DataFormats.FileDrop))
        {
            dragDetails.Effects = DragDropEffects.Copy;
            // Handled, so the editor TextBox does not try to insert the file name as text.
            dragDetails.Handled = true;
        }
        else if (dragDetails.Data.GetDataPresent(BookshelfEntryDragFormat))
        {
            // A bookshelf file being reordered can only be dropped on the bookshelf list,
            // whose own DragOver handler (which runs after this one) allows it again.
            dragDetails.Effects = DragDropEffects.None;
        }
    }

    /// <summary>
    /// Files dropped on the bookshelf are added to it; files dropped anywhere else open
    /// (the first one) in the viewer.
    /// </summary>
    private void Window_PreviewDrop(object sender, DragEventArgs dropDetails)
    {
        if (dropDetails.Data.GetData(DataFormats.FileDrop) is string[] droppedPaths && droppedPaths.Length > 0)
        {
            dropDetails.Handled = true;

            if (IsOverBookshelfPanel(dropDetails))
            {
                var droppedMarkdownFiles = CollectMarkdownFiles(droppedPaths);
                if (droppedMarkdownFiles.Count == 0)
                {
                    MessageBox.Show(this, "No Markdown files were found in what was dropped.",
                        "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                AddFilesToBookshelf(droppedMarkdownFiles);
                return;
            }

            if (ConfirmDiscardUnsavedChanges())
            {
                LoadFile(droppedPaths[0]);
            }
        }
    }

    /// <summary>Asks about unsaved changes before the window closes.</summary>
    private void Window_Closing(object? sender, CancelEventArgs closingDetails)
    {
        if (!ConfirmDiscardUnsavedChanges())
        {
            closingDetails.Cancel = true;
            return;
        }

        openFileWatcher?.Dispose();
        StopWatchingShelfFolder();
        // Stop a query that is still running; its result is no longer wanted.
        runningQueryCancellation?.Cancel();
        SaveBookshelfLayout();
    }
}
