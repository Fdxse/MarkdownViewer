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

    /// <summary>Shows or hides the bookshelf and properties panel (Ctrl+B).</summary>
    public static readonly RoutedUICommand ToggleBookshelf = new(
        "Toggle bookshelf",
        nameof(ToggleBookshelf),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.B, ModifierKeys.Control) });

    /// <summary>Puts the open file on the bookshelf (Ctrl+D, like a browser bookmark).</summary>
    public static readonly RoutedUICommand AddToBookshelf = new(
        "Add to bookshelf",
        nameof(AddToBookshelf),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.D, ModifierKeys.Control) });

    /// <summary>Opens todo.md from the exe's folder, creating it if needed (Ctrl+T).</summary>
    public static readonly RoutedUICommand OpenTodoList = new(
        "Open todo list",
        nameof(OpenTodoList),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.T, ModifierKeys.Control) });

    /// <summary>Asks for a task and adds it at the top of the todo section in todo.md (Ctrl+Shift+T).</summary>
    public static readonly RoutedUICommand AddTodoTask = new(
        "Add task",
        nameof(AddTodoTask),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.T, ModifierKeys.Control | ModifierKeys.Shift) });

    /// <summary>Opens the global search window that searches every bookshelf, the todo list and saved queries (Ctrl+Shift+F).</summary>
    public static readonly RoutedUICommand GlobalSearch = new(
        "Search all documents",
        nameof(GlobalSearch),
        typeof(AppCommands),
        new InputGestureCollection { new KeyGesture(Key.F, ModifierKeys.Control | ModifierKeys.Shift) });
}
