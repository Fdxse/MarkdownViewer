using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace MarkdownViewer.PostIts;

/// <summary>
/// Tools → Post-it colours: the "settings page" where the user registers the colours a note can be given,
/// each with a name, a hex value and a category. Works on copies, so Cancel leaves the palette untouched;
/// OK makes the checked, edited palette available in <see cref="EditedColours"/>.
/// </summary>
public partial class PostItColourSettingsWindow : Window
{
    /// <summary>The rows shown in the grid (copies of the palette passed in).</summary>
    private readonly ObservableCollection<EditablePostItColourRow> editableRows;

    /// <summary>Creates the dialog showing copies of the given palette.</summary>
    public PostItColourSettingsWindow(IEnumerable<PostItColour> currentPalette)
    {
        InitializeComponent();

        editableRows = new ObservableCollection<EditablePostItColourRow>(
            currentPalette.Select(paletteColour => new EditablePostItColourRow(paletteColour.Clone())));
        ColourGrid.ItemsSource = editableRows;
    }

    /// <summary>The palette as edited by the user. Only meaningful after OK.</summary>
    public List<PostItColour> EditedColours { get; private set; } = new();

    /// <summary>OK: finishes any edit in progress, checks the hex values and closes the dialog.</summary>
    private void OkButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // Commit the cell, then the row, the user may still be typing in; otherwise its text is lost.
        ColourGrid.CommitEdit();
        ColourGrid.CommitEdit();

        // Rows where nothing was typed at all are simply ignored.
        var filledInRows = editableRows
            .Where(colourRow => !string.IsNullOrWhiteSpace(colourRow.Name) || !string.IsNullOrWhiteSpace(colourRow.Hex))
            .ToList();

        // A colour without a valid hex value cannot be drawn: point at the first such row instead of dropping it silently.
        var rowWithInvalidHex = filledInRows.FirstOrDefault(colourRow => PostItColour.NormalizeHex(colourRow.Hex) == null);
        if (rowWithInvalidHex != null)
        {
            MessageBox.Show(this,
                $"\"{rowWithInvalidHex.Hex}\" is not a colour. Type six hex digits, for example #B8E0FF.",
                "Post-it colours", MessageBoxButton.OK, MessageBoxImage.Warning);
            ColourGrid.SelectedItem = rowWithInvalidHex;
            ColourGrid.ScrollIntoView(rowWithInvalidHex);
            return;
        }

        // Dark colours make the black note text hard to read; allowed, but only after a warning.
        var tooDarkRows = filledInRows
            .Where(colourRow => PostItColour.IsTooDarkForText(PostItColour.ParseColourOrStandardYellow(colourRow.Hex)))
            .ToList();
        if (tooDarkRows.Count > 0)
        {
            string tooDarkNames = string.Join(", ", tooDarkRows.Select(colourRow => string.IsNullOrWhiteSpace(colourRow.Name) ? colourRow.Hex : colourRow.Name));
            var keepAnswer = MessageBox.Show(this,
                $"These colours are dark, so the text on the notes may be hard to read: {tooDarkNames}.\n\nKeep them anyway?",
                "Post-it colours", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (keepAnswer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        // A missing name falls back to the hex value, so every colour can be told apart in the menu.
        EditedColours = filledInRows
            .Select(colourRow =>
            {
                string normalizedHex = PostItColour.NormalizeHex(colourRow.Hex)!;
                return new PostItColour
                {
                    Id = colourRow.Id,
                    Name = string.IsNullOrWhiteSpace(colourRow.Name) ? normalizedHex : colourRow.Name.Trim(),
                    Hex = normalizedHex,
                    Category = (colourRow.Category ?? string.Empty).Trim(),
                };
            })
            .ToList();

        DialogResult = true;
    }

    /// <summary>Removes the selected row (the Delete key does the same).</summary>
    private void RemoveButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // The "new row" placeholder is not a colour row, so the pattern match skips it.
        if (ColourGrid.SelectedItem is EditablePostItColourRow selectedRow)
        {
            ColourGrid.CancelEdit();
            editableRows.Remove(selectedRow);
        }
    }

    /// <summary>Moves the selected colour one step up in the Colour menu.</summary>
    private void MoveUpButton_Click(object sender, RoutedEventArgs clickDetails) => MoveSelectedRow(-1);

    /// <summary>Moves the selected colour one step down in the Colour menu.</summary>
    private void MoveDownButton_Click(object sender, RoutedEventArgs clickDetails) => MoveSelectedRow(+1);

    /// <summary>Moves the selected colour by the given number of rows, if it stays inside the list.</summary>
    private void MoveSelectedRow(int rowOffset)
    {
        if (ColourGrid.SelectedItem is not EditablePostItColourRow selectedRow)
        {
            return;
        }

        // Finish any typing first; moving a row that is being edited is not allowed by the grid.
        ColourGrid.CommitEdit();
        ColourGrid.CommitEdit();

        int currentIndex = editableRows.IndexOf(selectedRow);
        int targetIndex = currentIndex + rowOffset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= editableRows.Count)
        {
            return;
        }

        editableRows.Move(currentIndex, targetIndex);

        // Keep the moved colour selected so the button can be clicked repeatedly.
        ColourGrid.SelectedItem = selectedRow;
        ColourGrid.ScrollIntoView(selectedRow);
    }

    /// <summary>
    /// One row of the grid. Tells the grid when the hex value changes, so the colour preview follows the typing.
    /// The grid creates new rows itself through the parameterless constructor.
    /// </summary>
    public sealed class EditablePostItColourRow : INotifyPropertyChanged
    {
        private string colourName;
        private string hexValue;
        private string categoryText;

        /// <summary>A new, empty row (the grid's "add a row" line); it gets a fresh id.</summary>
        public EditablePostItColourRow() : this(new PostItColour())
        {
        }

        /// <summary>A row showing an existing colour; its id is kept so notes in that colour keep it.</summary>
        public EditablePostItColourRow(PostItColour paletteColour)
        {
            Id = paletteColour.Id;
            colourName = paletteColour.Name;
            hexValue = paletteColour.Hex;
            categoryText = paletteColour.Category;
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The colour's fixed id (not shown).</summary>
        public string Id { get; }

        /// <summary>The colour's name, for example "Blue".</summary>
        public string Name
        {
            get => colourName;
            set => SetField(ref colourName, value);
        }

        /// <summary>The hex value as typed, for example "#B8E0FF".</summary>
        public string Hex
        {
            get => hexValue;
            set
            {
                SetField(ref hexValue, value);
                // The preview depends on the hex value.
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SwatchBrush)));
            }
        }

        /// <summary>What a note in this colour means, for example "Work".</summary>
        public string Category
        {
            get => categoryText;
            set => SetField(ref categoryText, value);
        }

        /// <summary>The preview of the colour; transparent while the hex value is not (yet) valid.</summary>
        public Brush SwatchBrush => PostItColour.NormalizeHex(hexValue) is string validHex
            ? PostItColour.CreateFrozenBrush(PostItColour.ParseColourOrStandardYellow(validHex))
            : Brushes.Transparent;

        /// <summary>Stores a new value and tells the grid about it.</summary>
        private void SetField(ref string backingField, string? newValue, [CallerMemberName] string propertyName = "")
        {
            backingField = newValue ?? string.Empty;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
