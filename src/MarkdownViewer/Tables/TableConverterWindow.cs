using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MarkdownViewer.Rendering;

namespace MarkdownViewer.Tables;

/// <summary>
/// Tools → Table converter (Ctrl+Shift+L): paste a table from Excel, Word, a web page, CSV or Markdown,
/// see it rendered the way the viewer will show it, and insert the generated Markdown table into the document
/// (or copy it as Markdown, for Excel/Word, or as CSV). Built in code, like the other small dialogs.
/// </summary>
public sealed class TableConverterWindow : Window
{
    /// <summary>Shown in the input box when it opens without a table, so the window is not empty.</summary>
    private const string ExampleTable =
        "| Name | Role | Started |\r\n| --- | --- | --- |\r\n| Anna | Developer | 2021 |\r\n| Erik | Tester | 2023 |";

    // Controls the user reads from or writes to.
    private readonly TextBox tableInputTextBox;
    private readonly ComboBox inputFormatComboBox;
    private readonly CheckBox firstRowIsHeaderCheckBox;
    private readonly CheckBox trimCellsCheckBox;
    private readonly CheckBox swapRowsAndColumnsCheckBox;
    private readonly CheckBox lineUpColumnsCheckBox;
    private readonly ComboBox csvSeparatorComboBox;
    private readonly TextBlock detectedFormatText;
    private readonly TextBlock raggedRowsText;
    private readonly FlowDocumentScrollViewer previewViewer;
    private readonly TextBox markdownOutputTextBox;
    private readonly TextBlock copyStatusText;
    private readonly Button insertButton;
    private readonly List<Button> copyButtons = [];

    /// <summary>False when the caller has nowhere to insert the table (only copying is possible then).</summary>
    private bool isInsertAllowed = true;

    /// <summary>
    /// The parsed table as a grid of strings (the first row is the header when that option is on).
    /// Every output format is generated from this.
    /// </summary>
    private List<List<string>> currentTableGrid = [];

    /// <summary>The Markdown table to insert (read after <see cref="Window.ShowDialog"/> returned true).</summary>
    public string MarkdownTable { get; private set; } = string.Empty;

    /// <summary>Creates the window.</summary>
    /// <param name="initialTableText">
    /// Text to start with, e.g. a table selected in the editor so it can be reformatted; null or empty shows an example.
    /// </param>
    /// <param name="insertButtonText">Caption of the insert button ("Insert into document" or "Replace selection").</param>
    public TableConverterWindow(string? initialTableText, string insertButtonText)
    {
        Title = "Table converter";
        Width = 980;
        Height = 720;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // ---- Left side: input box and its options ----

        var inputHintText = new TextBlock
        {
            Text = "Paste a table here: cells copied from Excel or Word, a table from a web page, CSV or a Markdown table.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };

        tableInputTextBox = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12,
            Padding = new Thickness(4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            SpellCheck = { IsEnabled = false },
        };
        tableInputTextBox.TextChanged += (_, _) => RefreshOutput();
        // Tables from Word and web pages are read from the clipboard's HTML (see TableInput_Pasting).
        DataObject.AddPastingHandler(tableInputTextBox, TableInput_Pasting);

        inputFormatComboBox = new ComboBox { Width = 150, Margin = new Thickness(4, 0, 12, 0) };
        inputFormatComboBox.Items.Add(new ComboBoxItem { Content = "Automatic", Tag = TableInputFormat.Automatic });
        inputFormatComboBox.Items.Add(new ComboBoxItem { Content = "Markdown", Tag = TableInputFormat.Markdown });
        inputFormatComboBox.Items.Add(new ComboBoxItem { Content = "CSV", Tag = TableInputFormat.Csv });
        inputFormatComboBox.Items.Add(new ComboBoxItem { Content = "Tab-separated (Excel)", Tag = TableInputFormat.TabSeparated });
        inputFormatComboBox.SelectedIndex = 0;
        inputFormatComboBox.SelectionChanged += (_, _) => RefreshOutput();

        var clearButton = new Button { Content = "Clear", Padding = new Thickness(10, 1, 10, 1) };
        clearButton.Click += (_, _) =>
        {
            // Empty the box and put the cursor back in it, ready for the next paste.
            tableInputTextBox.Clear();
            tableInputTextBox.Focus();
        };

        var readAsRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var readAsLabel = new TextBlock { Text = "Read as", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(clearButton, Dock.Right);
        readAsRow.Children.Add(clearButton);
        readAsRow.Children.Add(readAsLabel);
        readAsRow.Children.Add(inputFormatComboBox);
        detectedFormatText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
        readAsRow.Children.Add(detectedFormatText);

        firstRowIsHeaderCheckBox = CreateOptionCheckBox("First row is the header", isChecked: true,
            "Off: the first row is data, and the header gets \"Column 1\", \"Column 2\"... (Markdown tables always need a header)");
        trimCellsCheckBox = CreateOptionCheckBox("Trim spaces in cells", isChecked: true, "Remove spaces at the start and end of every cell");
        swapRowsAndColumnsCheckBox = CreateOptionCheckBox("Swap rows and columns", isChecked: false, "Turn rows into columns and columns into rows");
        lineUpColumnsCheckBox = CreateOptionCheckBox("Line up the columns", isChecked: true,
            "Pad the cells with spaces so the pipes line up in the editor (the rendered table looks the same either way)");

        var optionsPanel = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        optionsPanel.Children.Add(firstRowIsHeaderCheckBox);
        optionsPanel.Children.Add(trimCellsCheckBox);
        optionsPanel.Children.Add(swapRowsAndColumnsCheckBox);
        optionsPanel.Children.Add(lineUpColumnsCheckBox);

        raggedRowsText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x5B, 0x00)),
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var inputPanel = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };
        DockPanel.SetDock(inputHintText, Dock.Top);
        DockPanel.SetDock(raggedRowsText, Dock.Bottom);
        DockPanel.SetDock(optionsPanel, Dock.Bottom);
        DockPanel.SetDock(readAsRow, Dock.Bottom);
        inputPanel.Children.Add(inputHintText);
        inputPanel.Children.Add(raggedRowsText);
        inputPanel.Children.Add(optionsPanel);
        inputPanel.Children.Add(readAsRow);
        inputPanel.Children.Add(tableInputTextBox);

        // ---- Right side: rendered preview on top, generated Markdown below ----

        var previewLabel = new TextBlock { Text = "Preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        previewViewer = new FlowDocumentScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
        };

        var markdownLabel = new TextBlock { Text = "Markdown", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        markdownOutputTextBox = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12,
            Padding = new Thickness(4),
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var outputGrid = new Grid { Margin = new Thickness(6, 0, 0, 0) };
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outputGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        AddToGrid(outputGrid, previewLabel, rowIndex: 0);
        AddToGrid(outputGrid, previewViewer, rowIndex: 1);
        AddToGrid(outputGrid, markdownLabel, rowIndex: 2);
        AddToGrid(outputGrid, markdownOutputTextBox, rowIndex: 3);

        // Input and output side by side, with a splitter between them.
        var contentGrid = new Grid();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(inputPanel, 0);
        var columnSplitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        Grid.SetColumn(columnSplitter, 1);
        Grid.SetColumn(outputGrid, 2);
        contentGrid.Children.Add(inputPanel);
        contentGrid.Children.Add(columnSplitter);
        contentGrid.Children.Add(outputGrid);

        // ---- Bottom row: copy buttons on the left, insert/close on the right ----

        csvSeparatorComboBox = new ComboBox { Width = 50, Margin = new Thickness(2, 0, 0, 0), ToolTip = "Separator for Copy as CSV" };
        csvSeparatorComboBox.Items.Add(new ComboBoxItem { Content = ";", Tag = ';' });
        csvSeparatorComboBox.Items.Add(new ComboBoxItem { Content = ",", Tag = ',' });
        // Semicolon first: Swedish Excel opens semicolon CSV files without asking.
        csvSeparatorComboBox.SelectedIndex = 0;

        var copyMarkdownButton = CreateCopyButton("Copy Markdown", "Put the Markdown table on the clipboard", CopyMarkdown);
        var copyForExcelButton = CreateCopyButton("Copy for Excel / Word",
            "Put the table on the clipboard as cells, so Excel and Word paste a real table", CopyForExcelAndWord);
        var copyCsvButton = CreateCopyButton("Copy as CSV", "Put the table on the clipboard as CSV text", CopyAsCsv);

        copyStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Foreground = Brushes.DimGray };

        var copyPanel = new StackPanel { Orientation = Orientation.Horizontal };
        copyPanel.Children.Add(copyMarkdownButton);
        copyPanel.Children.Add(copyForExcelButton);
        copyPanel.Children.Add(copyCsvButton);
        copyPanel.Children.Add(csvSeparatorComboBox);
        copyPanel.Children.Add(copyStatusText);

        insertButton = new Button
        {
            Content = insertButtonText,
            IsDefault = false,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Put the Markdown table into the document at the cursor (Ctrl+Enter)",
        };
        insertButton.Click += (_, _) => AcceptAndClose();
        var closeButton = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };

        var dialogButtonsPanel = new StackPanel { Orientation = Orientation.Horizontal };
        dialogButtonsPanel.Children.Add(insertButton);
        dialogButtonsPanel.Children.Add(closeButton);

        var bottomRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        DockPanel.SetDock(copyPanel, Dock.Left);
        DockPanel.SetDock(dialogButtonsPanel, Dock.Right);
        bottomRow.Children.Add(copyPanel);
        bottomRow.Children.Add(dialogButtonsPanel);

        var rootPanel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(bottomRow, Dock.Bottom);
        rootPanel.Children.Add(bottomRow);
        rootPanel.Children.Add(contentGrid);
        Content = rootPanel;

        // Ctrl+Enter inserts from anywhere in the window (Enter alone makes a new line in the input box).
        PreviewKeyDown += (_, keyDetails) =>
        {
            if (keyDetails.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && insertButton.IsEnabled)
            {
                keyDetails.Handled = true;
                AcceptAndClose();
            }
        };

        // Start with the given table (selected, so a paste replaces it) or the example.
        tableInputTextBox.Text = string.IsNullOrWhiteSpace(initialTableText) ? ExampleTable : initialTableText;
        Loaded += (_, _) =>
        {
            tableInputTextBox.SelectAll();
            tableInputTextBox.Focus();
        };

        // The option check boxes refresh the output when changed; hook them up after everything exists.
        foreach (var optionCheckBox in new[] { firstRowIsHeaderCheckBox, trimCellsCheckBox, swapRowsAndColumnsCheckBox, lineUpColumnsCheckBox })
        {
            optionCheckBox.Checked += (_, _) => RefreshOutput();
            optionCheckBox.Unchecked += (_, _) => RefreshOutput();
        }
        RefreshOutput();
    }

    /// <summary>Creates one of the option check boxes under the input box.</summary>
    private static CheckBox CreateOptionCheckBox(string caption, bool isChecked, string toolTip) => new()
    {
        Content = caption,
        IsChecked = isChecked,
        ToolTip = toolTip,
        Margin = new Thickness(0, 2, 14, 2),
    };

    /// <summary>Creates a copy button that runs <paramref name="copyAction"/> and is disabled while there is no table.</summary>
    private Button CreateCopyButton(string caption, string toolTip, Action copyAction)
    {
        var copyButton = new Button
        {
            Content = caption,
            ToolTip = toolTip,
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
        };
        copyButton.Click += (_, _) => copyAction();
        copyButtons.Add(copyButton);
        return copyButton;
    }

    /// <summary>Puts a control in a row of a grid.</summary>
    private static void AddToGrid(Grid targetGrid, UIElement childElement, int rowIndex)
    {
        Grid.SetRow(childElement, rowIndex);
        targetGrid.Children.Add(childElement);
    }

    /// <summary>The input format picked under "Read as".</summary>
    private TableInputFormat SelectedInputFormat =>
        (inputFormatComboBox.SelectedItem as ComboBoxItem)?.Tag as TableInputFormat? ?? TableInputFormat.Automatic;

    /// <summary>The separator picked for Copy as CSV.</summary>
    private char SelectedCsvSeparator => (csvSeparatorComboBox.SelectedItem as ComboBoxItem)?.Tag as char? ?? ';';

    /// <summary>Generates the Markdown table from the current grid with the current options.</summary>
    private string BuildMarkdown() => TableTextConverter.ToMarkdown(
        currentTableGrid,
        firstRowIsHeaderCheckBox.IsChecked == true,
        lineUpColumnsCheckBox.IsChecked == true,
        lineEnding: "\r\n");

    /// <summary>Re-reads the input and refreshes the labels, the preview and the Markdown.</summary>
    private void RefreshOutput()
    {
        // The constructor sets the input text before every control exists; the first real refresh comes at its end.
        if (lineUpColumnsCheckBox == null || copyStatusText == null)
        {
            return;
        }

        string rawText = tableInputTextBox.Text;
        bool hasInput = rawText.Trim().Length > 0;

        // Pick the format: the user's choice, or a guess from the text.
        DetectedTableFormat detectedFormat = TableTextConverter.DetectFormat(rawText);
        TableInputFormat chosenFormat = SelectedInputFormat == TableInputFormat.Automatic ? detectedFormat.Format : SelectedInputFormat;

        List<List<string>> parsedGrid = [];
        if (hasInput)
        {
            parsedGrid = chosenFormat switch
            {
                TableInputFormat.Markdown => TableTextConverter.ParseMarkdown(rawText),
                TableInputFormat.TabSeparated => TableTextConverter.ParseDelimitedText(rawText, '\t'),
                _ => TableTextConverter.ParseDelimitedText(rawText, detectedFormat.CsvSeparator),
            };
        }

        // Note how many rows had a different number of cells than the first row.
        int firstRowWidth = parsedGrid.Count > 0 ? parsedGrid[0].Count : 0;
        int raggedRowCount = parsedGrid.Count(rowCells => rowCells.Count != firstRowWidth);

        if (trimCellsCheckBox.IsChecked == true)
        {
            parsedGrid = parsedGrid.Select(rowCells => rowCells.Select(cellText => cellText.Trim()).ToList()).ToList();
        }
        parsedGrid = TableTextConverter.PadRowsToSameWidth(parsedGrid);
        if (swapRowsAndColumnsCheckBox.IsChecked == true)
        {
            parsedGrid = TableTextConverter.SwapRowsAndColumns(parsedGrid);
        }
        currentTableGrid = parsedGrid;

        // Label: what the text is read as, and the size of the table.
        bool hasHeaderRow = firstRowIsHeaderCheckBox.IsChecked == true;
        string formatName = chosenFormat switch
        {
            TableInputFormat.Markdown => "Markdown",
            TableInputFormat.TabSeparated => "tab-separated",
            _ => detectedFormat.CsvSeparator == ';' ? "CSV (semicolons)" : "CSV",
        };
        int columnCount = currentTableGrid.Count > 0 ? currentTableGrid[0].Count : 0;
        int bodyRowCount = Math.Max(0, currentTableGrid.Count - (hasHeaderRow ? 1 : 0));
        detectedFormatText.Text = !hasInput
            ? string.Empty
            : $"{(SelectedInputFormat == TableInputFormat.Automatic ? "Found" : "Reading")} {formatName}: " +
              $"{bodyRowCount} {(bodyRowCount == 1 ? "row" : "rows")}, {columnCount} {(columnCount == 1 ? "column" : "columns")}";

        raggedRowsText.Visibility = raggedRowCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        raggedRowsText.Text = $"{raggedRowCount} {(raggedRowCount == 1 ? "row has" : "rows have")} a different number of cells " +
            "than the first row. Empty cells were added at the end. If the columns look wrong, pick the format under \"Read as\".";

        // The generated Markdown, rendered with the app's own renderer so the preview matches the viewer.
        string markdownTable = BuildMarkdown();
        markdownOutputTextBox.Text = markdownTable;
        var previewRenderer = new MarkdownFlowDocumentRenderer(documentFolderPath: null, (_, _) => { }) { ShowCodeCopyButtons = false };
        previewViewer.Document = markdownTable.Length > 0
            ? previewRenderer.Render(markdownTable)
            : new FlowDocument(new Paragraph(new Run("Paste a table on the left to see it here.")) { Foreground = Brushes.Gray });

        // Nothing to insert or copy without a table.
        bool hasTable = markdownTable.Length > 0;
        insertButton.IsEnabled = hasTable && isInsertAllowed;
        copyButtons.ForEach(copyButton => copyButton.IsEnabled = hasTable);
        copyStatusText.Text = string.Empty;
    }

    /// <summary>
    /// Word and web pages put an HTML table on the clipboard whose plain text loses the cell boundaries
    /// when a cell has a line break. In that case the cells are read from the HTML and pasted as tab-separated text.
    /// Excel's own plain text is already tab-separated and is pasted as it is.
    /// </summary>
    private void TableInput_Pasting(object sender, DataObjectPastingEventArgs pastingDetails)
    {
        if (!pastingDetails.DataObject.GetDataPresent(DataFormats.Html))
        {
            return;
        }

        string? clipboardHtml = pastingDetails.DataObject.GetData(DataFormats.Html) as string;
        bool hasPlainText = pastingDetails.DataObject.GetDataPresent(DataFormats.UnicodeText);
        if (string.IsNullOrEmpty(clipboardHtml) || (HtmlTableClipboard.IsFromExcel(clipboardHtml) && hasPlainText))
        {
            return;
        }

        var pastedGrid = HtmlTableClipboard.ReadSingleTable(clipboardHtml);
        if (pastedGrid == null)
        {
            return;
        }

        // Replace the normal paste with the tab-separated version of the table.
        pastingDetails.CancelCommand();
        tableInputTextBox.SelectedText = TableTextConverter.ToTabSeparated(pastedGrid);
        tableInputTextBox.CaretIndex = tableInputTextBox.SelectionStart + tableInputTextBox.SelectionLength;
    }

    /// <summary>Copy Markdown: the Markdown table as plain text.</summary>
    private void CopyMarkdown() => PutOnClipboard(new DataObject(DataFormats.UnicodeText, BuildMarkdown()), "Markdown copied");

    /// <summary>Copy for Excel / Word: tab-separated text plus an HTML table, so both paste as real cells.</summary>
    private void CopyForExcelAndWord()
    {
        var clipboardData = new DataObject();
        clipboardData.SetData(DataFormats.UnicodeText, TableTextConverter.ToTabSeparated(currentTableGrid));
        clipboardData.SetData(DataFormats.Html,
            HtmlTableClipboard.WrapForClipboard(TableTextConverter.ToHtmlTable(currentTableGrid, firstRowIsHeaderCheckBox.IsChecked == true)));
        PutOnClipboard(clipboardData, "Copied for Excel and Word");
    }

    /// <summary>Copy as CSV with the chosen separator.</summary>
    private void CopyAsCsv() => PutOnClipboard(
        new DataObject(DataFormats.UnicodeText, TableTextConverter.ToCsv(currentTableGrid, SelectedCsvSeparator)), "CSV copied");

    /// <summary>Puts data on the clipboard and says so; another program holding the clipboard is reported instead of crashing.</summary>
    private void PutOnClipboard(DataObject clipboardData, string successMessage)
    {
        try
        {
            Clipboard.SetDataObject(clipboardData, copy: true);
            copyStatusText.Text = successMessage;
        }
        catch (System.Runtime.InteropServices.ExternalException clipboardError)
        {
            copyStatusText.Text = "The clipboard is busy, try again (" + clipboardError.Message + ")";
        }
    }

    /// <summary>Turns off the insert button (e.g. while a query result is shown) and says why in its tooltip.</summary>
    public void DisableInsert(string reason)
    {
        isInsertAllowed = false;
        insertButton.IsEnabled = false;
        insertButton.ToolTip = reason;
        ToolTipService.SetShowOnDisabled(insertButton, true);
    }

    /// <summary>Insert: keeps the Markdown table for the caller and closes the window.</summary>
    private void AcceptAndClose()
    {
        MarkdownTable = BuildMarkdown();
        if (MarkdownTable.Length > 0)
        {
            DialogResult = true;
        }
    }
}
