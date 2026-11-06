using System.Text.Json.Serialization;

namespace MarkdownViewer.Queries;

/// <summary>How the rows a query returns are turned into a Markdown document.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryResultMode
{
    /// <summary>
    /// The query itself returns Markdown: the first column of every row is shown exactly as it is,
    /// one row per line.
    /// </summary>
    Markdown,

    /// <summary>
    /// A normal query: the result is shown as a Markdown table, or, when a row template is given,
    /// each row is written out through that template.
    /// </summary>
    Table,
}

/// <summary>
/// A saved SQL Server query on a query bookshelf. Opening it runs the query and shows the result as a
/// Markdown document. Stored in settings.json; the connection string is stored encrypted (see
/// <see cref="ConnectionSecrets"/>) so it never ends up in the settings file or a Zip all backup as plain text.
/// </summary>
public class SavedQuery
{
    /// <summary>Default for <see cref="MaximumRowCount"/>.</summary>
    public const int DefaultMaximumRowCount = 1000;

    /// <summary>Default for <see cref="TimeoutSeconds"/>.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Name shown in the bookshelf list and as the document title, e.g. "Open orders".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The connection string, encrypted with Windows DPAPI for the current Windows user (base64).
    /// Null until a connection has been entered. Use <see cref="TryGetConnectionString"/> and
    /// <see cref="SetConnectionString"/> instead of reading or writing this directly.
    /// </summary>
    public string? EncryptedConnectionString { get; set; }

    /// <summary>
    /// Server name shown in the properties panel and the bookshelf tooltip. Not secret, so it is stored
    /// as plain text; it also lets the user see which server a query used when its connection cannot be decrypted.
    /// </summary>
    public string ServerName { get; set; } = string.Empty;

    /// <summary>Database name shown in the properties panel (plain text for the same reason as <see cref="ServerName"/>).</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>The SQL that is run when the query is opened.</summary>
    public string SqlText { get; set; } = string.Empty;

    /// <summary>Whether the query returns ready-made Markdown, or rows that the app turns into a table.</summary>
    public QueryResultMode ResultMode { get; set; } = QueryResultMode.Table;

    /// <summary>
    /// Optional, for <see cref="QueryResultMode.Table"/> only: Markdown written once per row, with
    /// "{ColumnName}" replaced by that row's value, e.g. "## {Title}\n{Description}". Empty for a plain table.
    /// </summary>
    public string RowTemplate { get; set; } = string.Empty;

    /// <summary>Most rows read from the server; the rest are left out (and the document says so).</summary>
    public int MaximumRowCount { get; set; } = DefaultMaximumRowCount;

    /// <summary>How long the query may run before it is stopped.</summary>
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;

    /// <summary>
    /// False (the default): only plain SELECT queries are run, see <see cref="SqlReadOnlyCheck"/>.
    /// True: other statements such as EXEC (for a stored procedure that returns Markdown) are allowed too.
    /// Either way the query runs inside a transaction that is rolled back afterwards, so changes are not kept
    /// (unless the SQL or a stored procedure commits them itself, which only this setting makes possible).
    /// </summary>
    public bool AllowAllStatements { get; set; }

    /// <summary>
    /// Decrypts the stored connection string. Returns false (with a reason in <paramref name="problemText"/>)
    /// if none has been entered yet or it cannot be decrypted, e.g. because the settings came from another
    /// computer or another Windows user.
    /// </summary>
    public bool TryGetConnectionString(out string connectionString, out string? problemText)
    {
        connectionString = string.Empty;
        if (string.IsNullOrEmpty(EncryptedConnectionString))
        {
            problemText = "No database connection has been entered for this query yet.";
            return false;
        }

        return ConnectionSecrets.TryDecrypt(EncryptedConnectionString, out connectionString, out problemText);
    }

    /// <summary>Encrypts and stores a connection string (an empty one clears it).</summary>
    public void SetConnectionString(string connectionString)
    {
        EncryptedConnectionString = string.IsNullOrWhiteSpace(connectionString) ? null : ConnectionSecrets.Encrypt(connectionString);
    }

    /// <summary>A copy of this query (used when a query is copied to another query bookshelf).</summary>
    public SavedQuery Clone() => (SavedQuery)MemberwiseClone();
}
