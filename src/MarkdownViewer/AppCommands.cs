using System.Windows.Input;

namespace MarkdownViewer;

/// <summary>
/// Custom commands that have no built-in WPF equivalent, with their keyboard shortcuts.
/// </summary>
public static class AppCommands
{
    /// <summary>Switches between viewing only and side-by-side editing (Ctrl+E).</summary>
    public static readonly RoutedUICommand ToggleEditMode = new(
        "Toggle edit mode",
        nameof(ToggleEditMode),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.E, ModifierKeys.Control) });

    /// <summary>Re-reads the current file from disk (F5).</summary>
    public static readonly RoutedUICommand Reload = new(
        "Reload",
        nameof(Reload),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.F5) });
}
