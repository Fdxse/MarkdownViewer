using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Data.SqlClient;

namespace MarkdownViewer.Queries;

/// <summary>
/// The settings page of a saved query (used instead of the Markdown editor for query bookshelves):
/// name, SQL Server connection, the SQL itself, whether it returns Markdown or rows, an optional row template,
/// and the row and time limits. The password is never shown again after saving; it is stored encrypted.
/// </summary>
public partial class QuerySettingsWindow : Window
{
    /// <summary>Position of "Windows login" in the authentication drop-down.</summary>
    private const int WindowsLoginChoiceIndex = 0;

    /// <summary>Position of "SQL Server login" in the authentication drop-down.</summary>
    private const int SqlServerLoginChoiceIndex = 1;

    /// <summary>Largest row limit that can be entered; more rows than this would make a very slow document.</summary>
    private const int LargestAllowedRowCount = 100_000;

    /// <summary>Longest timeout that can be entered (one hour).</summary>
    private const int LargestAllowedTimeoutSeconds = 3600;

    /// <summary>
    /// The connection string the dialog started with, parsed, so options the dialog does not show
    /// (e.g. "Application Intent" or a port) are kept when the user saves.
    /// </summary>
    private readonly SqlConnectionStringBuilder originalConnectionSettings;

    /// <summary>The query as it was passed in; the dialog works on a copy and only returns it on Save.</summary>
    private readonly SavedQuery originalQuery;

    /// <summary>The query with the user's changes. Only valid after the dialog returned true.</summary>
    public SavedQuery EditedQuery { get; private set; }

    /// <summary>Opens the settings page for a query. For a new query, pass one with an empty name.</summary>
    public QuerySettingsWindow(SavedQuery queryToEdit)
    {
        InitializeComponent();
        originalQuery = queryToEdit;
        EditedQuery = queryToEdit.Clone();
        Title = string.IsNullOrWhiteSpace(queryToEdit.Name) ? "New query" : $"Query settings - {queryToEdit.Name}";

        // Fill the connection fields from the decrypted connection string, or from the plain server/database names
        // if it cannot be decrypted (then only the password has to be typed again).
        string? connectionProblem = null;
        originalConnectionSettings = new SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(queryToEdit.EncryptedConnectionString))
        {
            if (queryToEdit.TryGetConnectionString(out string savedConnectionString, out connectionProblem))
            {
                try
                {
                    originalConnectionSettings = new SqlConnectionStringBuilder(savedConnectionString);
                }
                catch (ArgumentException)
                {
                    connectionProblem = "The saved connection string could not be read. Please enter the connection again.";
                }
            }
        }

        if (connectionProblem != null)
        {
            ConnectionProblemText.Text = connectionProblem;
            ConnectionProblemText.Visibility = Visibility.Visible;
            ServerNameBox.Text = queryToEdit.ServerName;
            DatabaseNameBox.Text = queryToEdit.DatabaseName;
            AuthenticationChooser.SelectedIndex = WindowsLoginChoiceIndex;
        }
        else
        {
            ServerNameBox.Text = string.IsNullOrEmpty(originalConnectionSettings.DataSource) ? queryToEdit.ServerName : originalConnectionSettings.DataSource;
            DatabaseNameBox.Text = string.IsNullOrEmpty(originalConnectionSettings.InitialCatalog) ? queryToEdit.DatabaseName : originalConnectionSettings.InitialCatalog;
            // A brand-new query (no user name stored) defaults to Windows login.
            bool usesSqlServerLogin = !originalConnectionSettings.IntegratedSecurity && !string.IsNullOrEmpty(originalConnectionSettings.UserID);
            AuthenticationChooser.SelectedIndex = usesSqlServerLogin ? SqlServerLoginChoiceIndex : WindowsLoginChoiceIndex;
            UserNameBox.Text = originalConnectionSettings.UserID;
            PasswordInput.Password = originalConnectionSettings.Password;
            TrustServerCertificateBox.IsChecked = originalConnectionSettings.TrustServerCertificate;
        }

        QueryNameBox.Text = queryToEdit.Name;
        SqlTextBox.Text = queryToEdit.SqlText;
        RowTemplateBox.Text = queryToEdit.RowTemplate;
        MaximumRowCountBox.Text = queryToEdit.MaximumRowCount.ToString(CultureInfo.CurrentCulture);
        TimeoutSecondsBox.Text = queryToEdit.TimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        AllowAllStatementsBox.IsChecked = queryToEdit.AllowAllStatements;
        TableModeButton.IsChecked = queryToEdit.ResultMode == QueryResultMode.Table;
        MarkdownModeButton.IsChecked = queryToEdit.ResultMode == QueryResultMode.Markdown;
        UpdateRowTemplateAvailability();

        // Start where there is something to fill in.
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(QueryNameBox.Text))
            {
                QueryNameBox.Focus();
            }
            else
            {
                SqlTextBox.Focus();
            }
        };
    }

    // ------------------------------------------------------------------
    // Reading the fields
    // ------------------------------------------------------------------

    /// <summary>True if "SQL Server login" is chosen.</summary>
    private bool UsesSqlServerLogin => AuthenticationChooser.SelectedIndex == SqlServerLoginChoiceIndex;

    /// <summary>Builds the connection string from the fields, keeping any other options the saved one had.</summary>
    private string BuildConnectionString()
    {
        // Start from the original so hidden options survive; the fields shown always win.
        var connectionSettings = new SqlConnectionStringBuilder(originalConnectionSettings.ConnectionString)
        {
            DataSource = ServerNameBox.Text.Trim(),
            InitialCatalog = DatabaseNameBox.Text.Trim(),
            TrustServerCertificate = TrustServerCertificateBox.IsChecked == true,
        };

        if (UsesSqlServerLogin)
        {
            connectionSettings.IntegratedSecurity = false;
            connectionSettings.UserID = UserNameBox.Text.Trim();
            connectionSettings.Password = PasswordInput.Password;
        }
        else
        {
            connectionSettings.IntegratedSecurity = true;
            // A Windows login has no user name or password; make sure none is left behind in the stored string.
            connectionSettings.Remove("User ID");
            connectionSettings.Remove("Password");
        }
        return connectionSettings.ConnectionString;
    }

    /// <summary>
    /// Checks the fields and shows what is missing. Returns false if the user must fix something first.
    /// </summary>
    private bool ValidateFields(out int maximumRowCount, out int timeoutSeconds)
    {
        maximumRowCount = 0;
        timeoutSeconds = 0;

        if (string.IsNullOrWhiteSpace(QueryNameBox.Text))
        {
            return ShowFieldProblem("Please give the query a name; it is shown on the bookshelf.", QueryNameBox);
        }
        if (string.IsNullOrWhiteSpace(ServerNameBox.Text))
        {
            return ShowFieldProblem("Please enter the SQL Server to connect to.", ServerNameBox);
        }
        if (UsesSqlServerLogin && string.IsNullOrWhiteSpace(UserNameBox.Text))
        {
            return ShowFieldProblem("Please enter the user name of the SQL Server login (or choose Windows login).", UserNameBox);
        }
        if (string.IsNullOrWhiteSpace(SqlTextBox.Text))
        {
            return ShowFieldProblem("Please enter the SQL to run.", SqlTextBox);
        }
        if (!int.TryParse(MaximumRowCountBox.Text.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out maximumRowCount)
            || maximumRowCount < 1 || maximumRowCount > LargestAllowedRowCount)
        {
            return ShowFieldProblem($"The most rows to show must be a whole number from 1 to {LargestAllowedRowCount:N0}.", MaximumRowCountBox);
        }
        if (!int.TryParse(TimeoutSecondsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out timeoutSeconds)
            || timeoutSeconds < 1 || timeoutSeconds > LargestAllowedTimeoutSeconds)
        {
            return ShowFieldProblem($"The time limit must be a whole number of seconds from 1 to {LargestAllowedTimeoutSeconds:N0}.", TimeoutSecondsBox);
        }
        return true;
    }

    /// <summary>Tells the user what to fix, puts the cursor in that field, and returns false (for a one-line "return").</summary>
    private bool ShowFieldProblem(string problemText, Control fieldToFix)
    {
        MessageBox.Show(this, problemText, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        fieldToFix.Focus();
        return false;
    }

    // ------------------------------------------------------------------
    // Event handlers
    // ------------------------------------------------------------------

    /// <summary>Shows the user name and password fields only for SQL Server logins.</summary>
    private void AuthenticationChooser_SelectionChanged(object sender, SelectionChangedEventArgs selectionDetails)
    {
        SqlLoginPanel.Visibility = UsesSqlServerLogin ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The row template only applies to queries that return rows.</summary>
    private void ResultModeButton_Checked(object sender, RoutedEventArgs checkDetails)
    {
        UpdateRowTemplateAvailability();
    }

    /// <summary>Enables the row template box in table mode and greys it out in Markdown mode.</summary>
    private void UpdateRowTemplateAvailability()
    {
        // Called from the constructor too, possibly before every control exists.
        if (RowTemplateBox == null || MarkdownModeButton == null)
        {
            return;
        }

        bool isTableMode = MarkdownModeButton.IsChecked != true;
        RowTemplateBox.IsEnabled = isTableMode;
        RowTemplateHelpText.Opacity = isTableMode ? 1.0 : 0.5;
    }

    /// <summary>Tries to connect with the entered settings and shows whether it worked.</summary>
    private async void TestConnectionButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (string.IsNullOrWhiteSpace(ServerNameBox.Text))
        {
            ShowFieldProblem("Please enter the SQL Server to connect to.", ServerNameBox);
            return;
        }

        TestConnectionButton.IsEnabled = false;
        TestConnectionResultText.Foreground = Brushes.Gray;
        TestConnectionResultText.Text = "Connecting...";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            await SqlQueryRunner.TestConnectionAsync(BuildConnectionString(), CancellationToken.None);
            TestConnectionResultText.Foreground = Brushes.DarkGreen;
            TestConnectionResultText.Text = "Connected.";
        }
        catch (Exception connectionError)
        {
            TestConnectionResultText.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));
            TestConnectionResultText.Text = "Could not connect.";
            MessageBox.Show(this, QueryErrorText.Describe(connectionError), "Test connection", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            TestConnectionButton.IsEnabled = true;
        }
    }

    /// <summary>"Save and run": checks the fields, warns about SQL that would be refused, and closes with the new settings.</summary>
    private void SaveButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        if (!ValidateFields(out int maximumRowCount, out int timeoutSeconds))
        {
            return;
        }

        bool allowAllStatements = AllowAllStatementsBox.IsChecked == true;
        if (!allowAllStatements)
        {
            string? readOnlyProblem = SqlReadOnlyCheck.FindProblem(SqlTextBox.Text);
            if (readOnlyProblem != null)
            {
                var userAnswer = MessageBox.Show(this,
                    $"{readOnlyProblem}\n\nThe query will not run like this. To run it anyway, tick \"Allow more than SELECT\".\n\nSave it anyway?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (userAnswer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
        }

        var editedQuery = originalQuery.Clone();
        editedQuery.Name = QueryNameBox.Text.Trim();
        editedQuery.ServerName = ServerNameBox.Text.Trim();
        editedQuery.DatabaseName = DatabaseNameBox.Text.Trim();
        editedQuery.SetConnectionString(BuildConnectionString());
        editedQuery.SqlText = SqlTextBox.Text;
        editedQuery.ResultMode = MarkdownModeButton.IsChecked == true ? QueryResultMode.Markdown : QueryResultMode.Table;
        editedQuery.RowTemplate = RowTemplateBox.Text;
        editedQuery.MaximumRowCount = maximumRowCount;
        editedQuery.TimeoutSeconds = timeoutSeconds;
        editedQuery.AllowAllStatements = allowAllStatements;

        EditedQuery = editedQuery;
        DialogResult = true;
    }
}
