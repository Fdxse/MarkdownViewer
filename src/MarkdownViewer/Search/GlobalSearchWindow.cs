using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MarkdownViewer.Search;

/// <summary>
/// Search → Search all documents (Ctrl+Shift+F): a search box with results that update while typing, one row per
/// document with its name, where it is, and the matching lines with the search terms highlighted.
/// Enter or a double-click opens the document in the main window; the window stays open so several results can be
/// looked at in turn. Built in code because it is small. Searches only the <see cref="SearchIndex"/>, never the disk.
/// </summary>
public sealed class GlobalSearchWindow : Window
{
    /// <summary>Background colour of a highlighted search term in the results (a soft marker yellow).</summary>
    private static readonly Brush HighlightBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xE5, 0x8A));

    /// <summary>Colour of the secondary text (where a document is, the status line).</summary>
    private static readonly Brush SecondaryTextBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70));

    /// <summary>The index that is searched.</summary>
    private readonly SearchIndex searchIndex;

    /// <summary>The box the user types the search into.</summary>
    private readonly TextBox searchTextBox;

    /// <summary>Grey hint shown in the search box while it is empty.</summary>
    private readonly TextBlock searchHintText;

    /// <summary>The results, one row per document.</summary>
    private readonly ListBox resultsListBox;

    /// <summary>Line under the search box: how many documents matched and how fresh the index is.</summary>
    private readonly TextBlock statusText;

    /// <summary>"Include archive": when ticked (the default) archived documents are found too, listed after the others.</summary>
    private readonly CheckBox includeArchiveCheckBox;

    /// <summary>Waits until the user pauses typing before searching, so every key press does not start a search.</summary>
    private readonly DispatcherTimer typingPauseTimer;

    /// <summary>Stops the previous search when a new one starts.</summary>
    private CancellationTokenSource? runningSearchCancellation;

    /// <summary>The terms of the search whose results are shown (used to highlight the hit in the opened document).</summary>
    private List<string> shownSearchTerms = new();

    /// <summary>True while the main window is bringing the index up to date.</summary>
    private bool isIndexBeingRefreshed;

    /// <summary>
    /// Raised when the user picks a result (Enter or double-click). Gets the hit and the search terms, so the main
    /// window can open the document and jump to the first place a term occurs.
    /// </summary>
    public event Action<SearchHit, IReadOnlyList<string>>? DocumentChosen;

    /// <summary>Raised when the user clicks "Refresh index", asking the main window to read every document again.</summary>
    public event Action? RefreshIndexRequested;

    /// <summary>Creates the search window for the given index.</summary>
    public GlobalSearchWindow(SearchIndex searchIndex)
    {
        this.searchIndex = searchIndex;

        Title = "Search all documents";
        Width = 720;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // Search box with a grey hint on top of it while it is empty.
        searchTextBox = new TextBox { FontSize = 15, Padding = new Thickness(4) };
        searchTextBox.TextChanged += (_, _) =>
        {
            searchHintText!.Visibility = searchTextBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            typingPauseTimer!.Stop();
            typingPauseTimer.Start();
        };
        searchTextBox.PreviewKeyDown += SearchTextBox_PreviewKeyDown;

        searchHintText = new TextBlock
        {
            Text = "Search every bookshelf, the todo list, the archive and saved queries. Use \"quotes\" for an exact phrase.",
            Foreground = SecondaryTextBrush,
            FontSize = 13,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var searchBoxWithHint = new Grid();
        searchBoxWithHint.Children.Add(searchTextBox);
        searchBoxWithHint.Children.Add(searchHintText);

        statusText = new TextBlock { Foreground = SecondaryTextBrush, Margin = new Thickness(0, 6, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };

        resultsListBox = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(resultsListBox, ScrollBarVisibility.Disabled);
        resultsListBox.MouseDoubleClick += (_, _) => OpenSelectedResult();
        resultsListBox.KeyDown += ResultsListBox_KeyDown;

        // Bottom row: keyboard help and the rebuild button.
        var refreshIndexButton = new Button
        {
            Content = "Refresh index",
            Padding = new Thickness(10, 3, 10, 3),
            ToolTip = "Read every document on every bookshelf again and rewrite search-index.json. " +
                      "Normally not needed: changed files are picked up each time this window opens.",
        };
        refreshIndexButton.Click += (_, _) => RefreshIndexRequested?.Invoke();
        includeArchiveCheckBox = new CheckBox
        {
            Content = "Include archive",
            IsChecked = true,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Also find archived documents (they are listed after the others and marked \"archived\")",
        };
        // Search again straight away with the new choice.
        includeArchiveCheckBox.Click += (_, _) => _ = RunSearchAsync();
        var keyboardHelpText = new TextBlock
        {
            Text = "Enter or double-click opens  ·  ↓ moves to the results  ·  Esc closes",
            Foreground = SecondaryTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var bottomRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(refreshIndexButton, Dock.Right);
        DockPanel.SetDock(includeArchiveCheckBox, Dock.Right);
        bottomRow.Children.Add(refreshIndexButton);
        bottomRow.Children.Add(includeArchiveCheckBox);
        bottomRow.Children.Add(keyboardHelpText);

        var windowLayout = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(searchBoxWithHint, Dock.Top);
        DockPanel.SetDock(statusText, Dock.Top);
        DockPanel.SetDock(bottomRow, Dock.Bottom);
        windowLayout.Children.Add(searchBoxWithHint);
        windowLayout.Children.Add(statusText);
        windowLayout.Children.Add(bottomRow);
        windowLayout.Children.Add(resultsListBox);
        Content = windowLayout;

        typingPauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        typingPauseTimer.Tick += (_, _) =>
        {
            typingPauseTimer.Stop();
            _ = RunSearchAsync();
        };

        // Esc closes the window from anywhere in it.
        PreviewKeyDown += (_, keyDetails) =>
        {
            if (keyDetails.Key == Key.Escape)
            {
                keyDetails.Handled = true;
                Close();
            }
        };

        Loaded += (_, _) => FocusSearchBox();
        UpdateStatusText(matchCount: null);
    }

    // ------------------------------------------------------------------
    // Called by the main window
    // ------------------------------------------------------------------

    /// <summary>Puts the cursor in the search box with its text selected, so the user can type a new search at once.</summary>
    public void FocusSearchBox()
    {
        searchTextBox.Focus();
        searchTextBox.SelectAll();
    }

    /// <summary>Tells the window whether the index is being brought up to date (shown in the status line).</summary>
    public void SetIndexRefreshing(bool isRefreshing)
    {
        isIndexBeingRefreshed = isRefreshing;
        UpdateStatusText(matchCount: resultsListBox.Items.Count > 0 || shownSearchTerms.Count > 0 ? resultsListBox.Items.Count : null);
    }

    /// <summary>Runs the search again, e.g. after the index changed, keeping the selected result if it is still there.</summary>
    public void RefreshResults()
    {
        _ = RunSearchAsync();
    }

    // ------------------------------------------------------------------
    // Searching
    // ------------------------------------------------------------------

    /// <summary>Searches the index in the background and shows the results (unless a newer search has started meanwhile).</summary>
    private async Task RunSearchAsync()
    {
        runningSearchCancellation?.Cancel();
        var thisSearchCancellation = new CancellationTokenSource();
        runningSearchCancellation = thisSearchCancellation;

        var searchTerms = GlobalSearcher.ParseSearchTerms(searchTextBox.Text);
        if (searchTerms.Count == 0)
        {
            shownSearchTerms = searchTerms;
            resultsListBox.Items.Clear();
            UpdateStatusText(matchCount: null);
            return;
        }

        List<SearchHit> searchHits;
        try
        {
            var indexedDocuments = searchIndex.GetDocumentsSnapshot();
            bool includeArchive = includeArchiveCheckBox.IsChecked == true;
            searchHits = await Task.Run(() => GlobalSearcher.Search(indexedDocuments, searchTerms, includeArchive, thisSearchCancellation.Token), thisSearchCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
            return;
        }

        if (thisSearchCancellation.IsCancellationRequested)
        {
            return;
        }

        // Keep the same document selected when the results are refreshed (e.g. after the index changed).
        string? previouslySelectedKey = ((resultsListBox.SelectedItem as ListBoxItem)?.Tag as SearchHit)?.Document.Key;

        shownSearchTerms = searchTerms;
        resultsListBox.Items.Clear();
        foreach (var searchHit in searchHits)
        {
            var resultRow = BuildResultRow(searchHit, searchTerms);
            resultsListBox.Items.Add(resultRow);
            if (previouslySelectedKey != null && string.Equals(searchHit.Document.Key, previouslySelectedKey, StringComparison.OrdinalIgnoreCase))
            {
                resultsListBox.SelectedItem = resultRow;
            }
        }
        if (resultsListBox.SelectedItem == null && resultsListBox.Items.Count > 0)
        {
            resultsListBox.SelectedIndex = 0;
        }
        resultsListBox.ScrollIntoView(resultsListBox.SelectedItem);
        UpdateStatusText(matchCount: searchHits.Count);
    }

    /// <summary>Writes the status line: what was found, the size of the index, and when it was last brought up to date.</summary>
    private void UpdateStatusText(int? matchCount)
    {
        string resultPart = matchCount switch
        {
            null => string.Empty,
            0 => "No documents found.  ",
            1 => "1 document found.  ",
            >= GlobalSearcher.MaximumHitCount => $"The first {GlobalSearcher.MaximumHitCount} documents found; add a word to narrow it down.  ",
            _ => $"{matchCount:N0} documents found.  ",
        };

        string indexPart = isIndexBeingRefreshed
            ? $"Searching {searchIndex.DocumentCount:N0} documents (checking for changes…)"
            : searchIndex.LastRefreshedAt is DateTime refreshedAt
                ? $"Searching {searchIndex.DocumentCount:N0} documents, up to date as of {refreshedAt:HH:mm}."
                : $"Searching {searchIndex.DocumentCount:N0} documents.";
        statusText.Text = resultPart + indexPart;
    }

    // ------------------------------------------------------------------
    // Showing results
    // ------------------------------------------------------------------

    /// <summary>Builds the row for one result: name in bold, where it is in grey, then the matching lines.</summary>
    private static ListBoxItem BuildResultRow(SearchHit searchHit, IReadOnlyList<string> searchTerms)
    {
        var indexedDocument = searchHit.Document;
        var rowLayout = new StackPanel { Margin = new Thickness(2, 4, 2, 6) };

        // Name of the file or query, with the terms highlighted.
        var titleText = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        AddHighlightedText(titleText.Inlines, indexedDocument.Title, searchTerms);
        // Archived documents are marked, so they are not mistaken for documents in everyday use.
        if (indexedDocument.IsArchived)
        {
            titleText.Inlines.Add(new Run("  (archived)") { FontWeight = FontWeights.Normal, Foreground = SecondaryTextBrush });
        }
        rowLayout.Children.Add(titleText);

        // "Query · Reports" or "Standard, Notes · C:\Docs\Projects".
        string kindText = indexedDocument.Kind == SearchDocumentKind.Query ? "Query" : System.IO.Path.GetDirectoryName(indexedDocument.Key) ?? string.Empty;
        string shelvesText = string.Join(", ", indexedDocument.ShelfNames);
        string locationText = indexedDocument.Kind == SearchDocumentKind.Query
            ? $"{kindText} · {shelvesText}"
            : $"{shelvesText} · {kindText}";
        if (indexedDocument.Kind == SearchDocumentKind.Query && indexedDocument.LastQueryRunAt is DateTime lastRunAt)
        {
            locationText += $" · result from {lastRunAt:yyyy-MM-dd HH:mm}";
        }
        rowLayout.Children.Add(new TextBlock
        {
            Text = locationText,
            Foreground = SecondaryTextBrush,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // The matching lines of a file, with their line numbers (none if the match was only in the name or folder,
        // or the file was not read because it is far down the list).
        foreach (var matchingLine in searchHit.MatchingLines)
        {
            var lineText = new TextBlock
            {
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 12,
                Margin = new Thickness(10, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            lineText.Inlines.Add(new Run($"{matchingLine.LineNumber,4}: ") { Foreground = SecondaryTextBrush });
            AddHighlightedText(lineText.Inlines, matchingLine.LineText, searchTerms);
            rowLayout.Children.Add(lineText);
        }

        // A query has no lines to show (only the words of its result are stored), so say where it matched instead.
        if (indexedDocument.Kind == SearchDocumentKind.Query)
        {
            rowLayout.Children.Add(new TextBlock
            {
                Text = searchHit.IsFoundInQueryResult ? "Found in the last result. Open it to run the query and see where." : "Found in the name or the SQL.",
                FontStyle = FontStyles.Italic,
                FontSize = 12,
                Margin = new Thickness(10, 2, 0, 0),
            });
        }

        string toolTipText = indexedDocument.Kind == SearchDocumentKind.Query
            ? $"Saved query \"{indexedDocument.Title}\". Opening it runs the query again."
            : indexedDocument.Key;
        return new ListBoxItem { Content = rowLayout, Tag = searchHit, ToolTip = toolTipText };
    }

    /// <summary>Adds text to a text block, with every occurrence of a search term on a yellow background.</summary>
    private static void AddHighlightedText(InlineCollection targetInlines, string text, IReadOnlyList<string> searchTerms)
    {
        int writtenUpToIndex = 0;
        while (writtenUpToIndex < text.Length)
        {
            // Find the nearest occurrence of any term from here on.
            int nearestHitIndex = -1;
            int nearestHitLength = 0;
            foreach (string searchTerm in searchTerms)
            {
                int foundIndex = GlobalSearcher.IndexOfTerm(text, searchTerm, writtenUpToIndex);
                bool isNearer = foundIndex >= 0 && (nearestHitIndex < 0 || foundIndex < nearestHitIndex
                    || (foundIndex == nearestHitIndex && searchTerm.Length > nearestHitLength));
                if (isNearer)
                {
                    nearestHitIndex = foundIndex;
                    nearestHitLength = searchTerm.Length;
                }
            }

            if (nearestHitIndex < 0)
            {
                break;
            }

            if (nearestHitIndex > writtenUpToIndex)
            {
                targetInlines.Add(new Run(text[writtenUpToIndex..nearestHitIndex]));
            }
            targetInlines.Add(new Run(text.Substring(nearestHitIndex, nearestHitLength)) { Background = HighlightBrush });
            writtenUpToIndex = nearestHitIndex + nearestHitLength;
        }

        if (writtenUpToIndex < text.Length)
        {
            targetInlines.Add(new Run(text[writtenUpToIndex..]));
        }
    }

    // ------------------------------------------------------------------
    // Keyboard and opening
    // ------------------------------------------------------------------

    /// <summary>In the search box: Enter opens the selected result, Down/Up move through the results without leaving the box.</summary>
    private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs keyDetails)
    {
        switch (keyDetails.Key)
        {
            case Key.Enter:
                // Search at once if the user pressed Enter before the typing pause ran out.
                if (typingPauseTimer.IsEnabled)
                {
                    typingPauseTimer.Stop();
                    _ = OpenFirstResultAfterSearchAsync();
                }
                else
                {
                    OpenSelectedResult();
                }
                keyDetails.Handled = true;
                break;

            case Key.Down when resultsListBox.Items.Count > 0:
                resultsListBox.SelectedIndex = Math.Min(resultsListBox.SelectedIndex + 1, resultsListBox.Items.Count - 1);
                resultsListBox.ScrollIntoView(resultsListBox.SelectedItem);
                keyDetails.Handled = true;
                break;

            case Key.Up when resultsListBox.Items.Count > 0:
                resultsListBox.SelectedIndex = Math.Max(resultsListBox.SelectedIndex - 1, 0);
                resultsListBox.ScrollIntoView(resultsListBox.SelectedItem);
                keyDetails.Handled = true;
                break;
        }
    }

    /// <summary>Runs the pending search, then opens its first result.</summary>
    private async Task OpenFirstResultAfterSearchAsync()
    {
        await RunSearchAsync();
        OpenSelectedResult();
    }

    /// <summary>In the result list: Enter opens the selected result.</summary>
    private void ResultsListBox_KeyDown(object sender, KeyEventArgs keyDetails)
    {
        if (keyDetails.Key == Key.Enter)
        {
            OpenSelectedResult();
            keyDetails.Handled = true;
        }
    }

    /// <summary>Hands the selected result to the main window, which opens it.</summary>
    private void OpenSelectedResult()
    {
        if ((resultsListBox.SelectedItem as ListBoxItem)?.Tag is SearchHit selectedHit)
        {
            DocumentChosen?.Invoke(selectedHit, shownSearchTerms);
        }
    }
}
