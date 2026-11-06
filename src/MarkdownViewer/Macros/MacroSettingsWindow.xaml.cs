using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MarkdownViewer.Macros;

/// <summary>
/// Dialog for adding, editing and removing text macros. Works on copies, so Cancel leaves
/// the current macros untouched; OK makes the edited list available in <see cref="EditedMacros"/>.
/// </summary>
public partial class MacroSettingsWindow : Window
{
    /// <summary>The rows shown in the grid (copies of the macros passed in).</summary>
    private readonly ObservableCollection<MacroDefinition> editableMacros;

    /// <summary>Creates the dialog showing copies of the given macros.</summary>
    public MacroSettingsWindow(IEnumerable<MacroDefinition> currentMacros)
    {
        InitializeComponent();

        editableMacros = new ObservableCollection<MacroDefinition>(currentMacros.Select(macro => macro.Clone()));
        MacroGrid.ItemsSource = editableMacros;
    }

    /// <summary>The macros as edited by the user, with blank rows removed. Only meaningful after OK.</summary>
    public List<MacroDefinition> EditedMacros { get; private set; } = new();

    /// <summary>OK: finishes any edit in progress and closes the dialog.</summary>
    private void OkButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // Commit a cell or row the user is still typing in, otherwise its text would be lost.
        MacroGrid.CommitEdit();
        MacroGrid.CommitEdit();

        // Rows without a trigger do nothing, so they are dropped instead of being saved.
        EditedMacros = editableMacros
            .Where(macro => !string.IsNullOrWhiteSpace(macro.Trigger))
            .ToList();

        DialogResult = true;
    }

    /// <summary>Removes the selected rows (the Delete key does the same).</summary>
    private void RemoveButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        // The selection includes the "new row" placeholder, which is not a macro; OfType skips it.
        var selectedMacros = MacroGrid.SelectedItems.OfType<MacroDefinition>().ToList();
        MacroGrid.CancelEdit();
        foreach (var selectedMacro in selectedMacros)
        {
            editableMacros.Remove(selectedMacro);
        }
    }

    /// <summary>Opens the folder with settings.json in Explorer, for editing or backing it up by hand.</summary>
    private void OpenSettingsFolderButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsFolderPath);
            Process.Start(new ProcessStartInfo(AppSettings.SettingsFolderPath) { UseShellExecute = true });
        }
        catch (Exception openError)
        {
            MessageBox.Show(this, $"Could not open the folder:\n{AppSettings.SettingsFolderPath}\n\n{openError.Message}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
