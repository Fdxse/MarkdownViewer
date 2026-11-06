using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Queries;

/// <summary>
/// A quick check that a query only reads data, so a typo or a pasted script cannot change a database by mistake.
/// It is a safety net, not a security boundary: queries also always run inside a transaction that is rolled back
/// (see <see cref="SqlQueryRunner"/>), and the safest setup is a database login that may only read.
///
/// The rules: the first word must be SELECT, WITH (a common table expression), DECLARE or SET, and none of the
/// words that change data or call other code may appear anywhere (comments and text in quotes are ignored).
/// </summary>
public static partial class SqlReadOnlyCheck
{
    /// <summary>Words a query may start with.</summary>
    private static readonly HashSet<string> AllowedFirstWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "DECLARE", "SET",
    };

    /// <summary>
    /// Words that change data or the database, or run other code (which could do anything).
    /// INTO is included because SELECT ... INTO creates a table.
    /// </summary>
    private static readonly HashSet<string> BlockedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "INTO",
        "CREATE", "ALTER", "DROP",
        "EXEC", "EXECUTE", "SP_EXECUTESQL",
        "GRANT", "REVOKE", "DENY",
        "BACKUP", "RESTORE", "DBCC", "BULK", "RECONFIGURE", "SHUTDOWN", "KILL",
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE",
        // A COMMIT would keep changes that the surrounding transaction is meant to roll back.
        "COMMIT", "ROLLBACK",
    };

    /// <summary>Finds words: letters, digits, underscores and the @ # $ characters T-SQL allows in names.</summary>
    [GeneratedRegex(@"[A-Za-z_@#][A-Za-z0-9_@#$]*")]
    private static partial Regex WordPattern();

    /// <summary>
    /// Returns null if the query looks read-only, otherwise a sentence that explains what was found,
    /// e.g. "The query contains UPDATE, which can change data."
    /// </summary>
    public static string? FindProblem(string sqlText)
    {
        string codeOnly = RemoveCommentsAndQuotedText(sqlText);
        var foundWords = WordPattern().Matches(codeOnly).Select(wordMatch => wordMatch.Value).ToList();

        if (foundWords.Count == 0)
        {
            return "The query is empty.";
        }

        // Variables (@name) and temporary table names (#name) never count as keywords.
        var keywords = foundWords.Where(word => word[0] != '@' && word[0] != '#').ToList();

        string? firstKeyword = keywords.FirstOrDefault();
        if (firstKeyword == null || !AllowedFirstWords.Contains(firstKeyword))
        {
            return $"The query starts with \"{firstKeyword ?? foundWords[0]}\". Only queries that start with SELECT, WITH, DECLARE or SET are run.";
        }

        string? blockedWord = keywords.FirstOrDefault(BlockedWords.Contains);
        if (blockedWord != null)
        {
            return $"The query contains {blockedWord.ToUpperInvariant()}, which can change data or run other code.";
        }

        return null;
    }

    /// <summary>
    /// Replaces comments (-- line and /* block */, which T-SQL allows to nest), 'text' and N'text' strings,
    /// [bracketed names] and "quoted names" with spaces, so words inside them are not mistaken for keywords.
    /// </summary>
    public static string RemoveCommentsAndQuotedText(string sqlText)
    {
        var codeBuilder = new StringBuilder(sqlText.Length);
        int position = 0;
        while (position < sqlText.Length)
        {
            char currentCharacter = sqlText[position];
            char nextCharacter = position + 1 < sqlText.Length ? sqlText[position + 1] : '\0';

            if (currentCharacter == '-' && nextCharacter == '-')
            {
                // Line comment: skip to the end of the line.
                int lineEnd = sqlText.IndexOf('\n', position);
                position = lineEnd < 0 ? sqlText.Length : lineEnd;
                codeBuilder.Append(' ');
            }
            else if (currentCharacter == '/' && nextCharacter == '*')
            {
                position = SkipBlockComment(sqlText, position);
                codeBuilder.Append(' ');
            }
            else if (currentCharacter is '\'' or '[' or '"')
            {
                // A quote ends where its closing character appears; a doubled closing character ('' or ]]) is part of the text.
                char closingCharacter = currentCharacter == '[' ? ']' : currentCharacter;
                position = SkipQuoted(sqlText, position + 1, closingCharacter);
                codeBuilder.Append(' ');
            }
            else
            {
                codeBuilder.Append(currentCharacter);
                position++;
            }
        }
        return codeBuilder.ToString();
    }

    /// <summary>Skips a /* block comment */ starting at <paramref name="startPosition"/>, including nested ones. Returns the position after it.</summary>
    private static int SkipBlockComment(string sqlText, int startPosition)
    {
        int nestingDepth = 0;
        int position = startPosition;
        while (position < sqlText.Length)
        {
            if (sqlText[position] == '/' && position + 1 < sqlText.Length && sqlText[position + 1] == '*')
            {
                nestingDepth++;
                position += 2;
            }
            else if (sqlText[position] == '*' && position + 1 < sqlText.Length && sqlText[position + 1] == '/')
            {
                nestingDepth--;
                position += 2;
                if (nestingDepth == 0)
                {
                    return position;
                }
            }
            else
            {
                position++;
            }
        }
        return position;
    }

    /// <summary>Skips quoted text up to its closing character (a doubled closing character does not end it). Returns the position after it.</summary>
    private static int SkipQuoted(string sqlText, int startPosition, char closingCharacter)
    {
        int position = startPosition;
        while (position < sqlText.Length)
        {
            if (sqlText[position] == closingCharacter)
            {
                bool isDoubled = position + 1 < sqlText.Length && sqlText[position + 1] == closingCharacter;
                if (!isDoubled)
                {
                    return position + 1;
                }
                position += 2;
            }
            else
            {
                position++;
            }
        }
        return position;
    }
}
