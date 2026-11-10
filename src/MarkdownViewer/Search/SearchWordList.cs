using System.Text;

namespace MarkdownViewer.Search;

/// <summary>
/// Turns text into the word list stored in the search index: every different word once, in lower case, sorted, and
/// separated by single spaces with a space at both ends (" invoice orders sql "). Repeated words are stored only once,
/// which keeps search-index.json much smaller than the documents themselves.
/// <para>
/// The spaces at the ends let a search look for " word" to find words that start with it, or just "ord" to find it
/// anywhere inside a word, with one fast string search over the whole list.
/// </para>
/// </summary>
public static class SearchWordList
{
    /// <summary>Words longer than this are left out (long hashes or base64 blobs are not worth searching).</summary>
    private const int MaximumWordLength = 64;

    /// <summary>Makes the word list of a text.</summary>
    public static string Build(string text)
    {
        var differentWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (string foundWord in SplitIntoWords(text))
        {
            if (foundWord.Length <= MaximumWordLength)
            {
                differentWords.Add(foundWord);
            }
        }

        if (differentWords.Count == 0)
        {
            return string.Empty;
        }

        // Sorted so the lookup file does not change when only the order of words in a document changes.
        var sortedWords = differentWords.ToList();
        sortedWords.Sort(StringComparer.Ordinal);
        return " " + string.Join(' ', sortedWords) + " ";
    }

    /// <summary>
    /// Splits text into lower-case words. A word is a run of letters, digits and underscores, so "Order_Id",
    /// "2026" and "åtgärd" are words, while "#", "**" and "|" are not.
    /// </summary>
    public static IEnumerable<string> SplitIntoWords(string text)
    {
        var currentWord = new StringBuilder();
        foreach (char textCharacter in text)
        {
            if (char.IsLetterOrDigit(textCharacter) || textCharacter == '_')
            {
                currentWord.Append(char.ToLowerInvariant(textCharacter));
            }
            else if (currentWord.Length > 0)
            {
                yield return currentWord.ToString();
                currentWord.Clear();
            }
        }

        if (currentWord.Length > 0)
        {
            yield return currentWord.ToString();
        }
    }

    /// <summary>
    /// True if every word of the search term occurs in the word list, each inside some stored word
    /// (so "ord" finds "orders"). A term without any word characters (e.g. "#") always counts as found.
    /// </summary>
    public static bool ContainsAllWordsOf(string wordList, string searchTerm)
    {
        foreach (string termWord in SplitIntoWords(searchTerm))
        {
            if (!wordList.Contains(termWord, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
