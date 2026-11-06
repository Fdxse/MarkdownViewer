using System.Windows;

namespace MarkdownViewer;

/// <summary>
/// Application entry point. Opens the main window and, if a file path was given on the
/// command line (e.g. "Open with..." or dragging a file onto the exe), loads that file.
/// </summary>
public partial class App : Application
{
    /// <summary>Called once when the application starts.</summary>
    protected override void OnStartup(StartupEventArgs startupArguments)
    {
        base.OnStartup(startupArguments);

        // The first command-line argument, if any, is treated as the file to open.
        string? fileToOpen = startupArguments.Args.Length > 0 ? startupArguments.Args[0] : null;

        var mainWindow = new MainWindow(fileToOpen);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
