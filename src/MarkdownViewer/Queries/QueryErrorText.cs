using Microsoft.Data.SqlClient;

namespace MarkdownViewer.Queries;

/// <summary>Turns errors from running a query or testing a connection into a message with a hint on what to do.</summary>
public static class QueryErrorText
{
    /// <summary>SQL Server's error number for "the query ran longer than its timeout".</summary>
    private const int TimeoutErrorNumber = -2;

    /// <summary>SQL Server's error number for "login failed".</summary>
    private const int LoginFailedErrorNumber = 18456;

    /// <summary>The error message plus, for common problems, a sentence on how to fix it.</summary>
    public static string Describe(Exception queryError)
    {
        string errorMessage = queryError.Message;

        if (queryError is SqlException sqlError)
        {
            if (sqlError.Number == TimeoutErrorNumber)
            {
                return errorMessage + "\n\nThe query took longer than its time limit. Make it faster, or raise \"Stop after (seconds)\" under Edit query.";
            }
            if (sqlError.Number == LoginFailedErrorNumber)
            {
                return errorMessage + "\n\nCheck the login under Edit query: the user name and password, or whether your Windows account has access to this database.";
            }
        }

        // The usual message when a company server uses a certificate that Windows does not trust.
        if (errorMessage.Contains("certificate", StringComparison.OrdinalIgnoreCase))
        {
            return errorMessage + "\n\nTick \"Trust the server's certificate\" under Edit query if you know this is the right server.";
        }
        return errorMessage;
    }
}
