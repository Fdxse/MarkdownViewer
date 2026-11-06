using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace MarkdownViewer.Queries;

/// <summary>One result set: its column names and the rows that were read.</summary>
public class QueryResultTable
{
    /// <summary>Column names in the order the query returns them (unnamed columns get "Column 1", "Column 2", ...).</summary>
    public List<string> ColumnNames { get; } = new();

    /// <summary>The rows; each holds one value per column, with SQL NULL as null.</summary>
    public List<object?[]> Rows { get; } = new();
}

/// <summary>Everything a query run produced, used to build the Markdown document and the properties panel.</summary>
public class QueryRunResult
{
    /// <summary>The result sets, in order. A query that returns nothing has none.</summary>
    public List<QueryResultTable> ResultTables { get; } = new();

    /// <summary>Rows read over all result sets.</summary>
    public int TotalRowCount => ResultTables.Sum(resultTable => resultTable.Rows.Count);

    /// <summary>True if reading stopped at the row limit, so the server had more rows than are shown.</summary>
    public bool WasCutShortAtRowLimit { get; set; }

    /// <summary>When the query was started.</summary>
    public DateTime RanAt { get; set; }

    /// <summary>How long it took, from opening the connection to reading the last row.</summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Runs a saved query against SQL Server. Reads at most the query's row limit, stops after its timeout, and always
/// runs inside a transaction that is rolled back afterwards, so the query cannot keep any change to the database.
/// </summary>
public static class SqlQueryRunner
{
    /// <summary>Name shown for the app in SQL Server's activity monitor and logs, so a DBA can see who is connected.</summary>
    private const string ApplicationNameForServer = "Markdown Viewer";

    /// <summary>
    /// Runs the query and reads its results. Throws <see cref="SqlException"/> for errors from the server
    /// (wrong password, syntax error, timeout...) and <see cref="OperationCanceledException"/> if cancelled.
    /// The read-only check (<see cref="SqlReadOnlyCheck"/>) is the caller's job, so it can tell the user first.
    /// </summary>
    public static async Task<QueryRunResult> RunAsync(SavedQuery savedQuery, string connectionString, CancellationToken cancellationToken)
    {
        var runResult = new QueryRunResult { RanAt = DateTime.Now };
        var runStopwatch = Stopwatch.StartNew();
        int maximumRowCount = Math.Max(1, savedQuery.MaximumRowCount);

        await using var databaseConnection = new SqlConnection(WithApplicationName(connectionString));
        await databaseConnection.OpenAsync(cancellationToken);

        // Everything the query does happens in this transaction, which is never committed.
        await using var readOnlyTransaction = (SqlTransaction)await databaseConnection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var queryCommand = new SqlCommand(savedQuery.SqlText, databaseConnection, readOnlyTransaction)
            {
                CommandTimeout = Math.Max(1, savedQuery.TimeoutSeconds),
            };

            try
            {
                await ReadAllResultSetsAsync(queryCommand, runResult, maximumRowCount, cancellationToken);
            }
            catch (SqlException) when (runResult.WasCutShortAtRowLimit)
            {
                // Stopping a query early at the row limit can make the server report "operation cancelled"; the rows we wanted are read.
            }
        }
        finally
        {
            // Undo anything the query changed. The connection may already be broken; then there is nothing to undo.
            try
            {
                if (readOnlyTransaction.Connection != null)
                {
                    await readOnlyTransaction.RollbackAsync(CancellationToken.None);
                }
            }
            catch (Exception)
            {
                // A broken connection rolls back on the server by itself.
            }
        }

        runResult.Duration = runStopwatch.Elapsed;
        return runResult;
    }

    /// <summary>
    /// Reads every result set of the command into <paramref name="runResult"/>, stopping at <paramref name="maximumRowCount"/>
    /// rows in total.
    /// </summary>
    private static async Task ReadAllResultSetsAsync(SqlCommand queryCommand, QueryRunResult runResult, int maximumRowCount, CancellationToken cancellationToken)
    {
        await using var resultReader = await queryCommand.ExecuteReaderAsync(cancellationToken);
        do
        {
            // Statements that return no rows at all (e.g. SET NOCOUNT ON) produce a "result" without columns: skip those.
            if (resultReader.FieldCount == 0)
            {
                continue;
            }

            var resultTable = new QueryResultTable();
            for (int columnIndex = 0; columnIndex < resultReader.FieldCount; columnIndex++)
            {
                string columnName = resultReader.GetName(columnIndex);
                resultTable.ColumnNames.Add(string.IsNullOrWhiteSpace(columnName) ? $"Column {columnIndex + 1}" : columnName);
            }
            runResult.ResultTables.Add(resultTable);

            while (await resultReader.ReadAsync(cancellationToken))
            {
                if (runResult.TotalRowCount >= maximumRowCount)
                {
                    // There is at least one row more than we may show.
                    runResult.WasCutShortAtRowLimit = true;
                    break;
                }

                var rowValues = new object?[resultReader.FieldCount];
                for (int columnIndex = 0; columnIndex < resultReader.FieldCount; columnIndex++)
                {
                    rowValues[columnIndex] = await resultReader.IsDBNullAsync(columnIndex, cancellationToken) ? null : resultReader.GetValue(columnIndex);
                }
                resultTable.Rows.Add(rowValues);
            }
        }
        while (!runResult.WasCutShortAtRowLimit && await resultReader.NextResultAsync(cancellationToken));

        // Without this, closing the reader would first fetch every remaining row; cancelling stops the query on the server instead.
        if (runResult.WasCutShortAtRowLimit)
        {
            queryCommand.Cancel();
        }
    }

    /// <summary>
    /// Opens and closes a connection to check that the server, database and login are right
    /// (used by the "Test connection" button). Throws on failure.
    /// </summary>
    public static async Task TestConnectionAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var databaseConnection = new SqlConnection(WithApplicationName(connectionString));
        await databaseConnection.OpenAsync(cancellationToken);
    }

    /// <summary>Adds "Application Name=Markdown Viewer" unless the connection string already names an application.</summary>
    private static string WithApplicationName(string connectionString)
    {
        var connectionBuilder = new SqlConnectionStringBuilder(connectionString);
        // The builder's default is "Framework Microsoft SqlClient Data Provider"; only replace that default.
        if (string.IsNullOrWhiteSpace(connectionBuilder.ApplicationName) || connectionBuilder.ApplicationName.StartsWith("Framework Microsoft", StringComparison.OrdinalIgnoreCase)
            || connectionBuilder.ApplicationName.StartsWith("Core Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            connectionBuilder.ApplicationName = ApplicationNameForServer;
        }
        return connectionBuilder.ConnectionString;
    }
}
