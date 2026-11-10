using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace MarkdownViewer.Search;

/// <summary>One line of a file that contains a search term, shown under the document in the results.</summary>
/// <param name="LineText">The line, trimmed (and shortened around the hit if it is long).</param>
/// <param name="LineNumber">1-based line number in the file.</param>
public sealed record SearchHitLine(string LineText, int LineNumber);

/// <summary>A document that matches the search, with the lines that show why.</summary>
public sealed class SearchHit
{
    /// <summary>The matching document.</summary>
    public required SearchIndexDocument Document { get; init; }

    /// <summary>
    /// The first few matching lines of a file (at most <see cref="GlobalSearcher.MaximumLinesPerHit"/>), read from
    /// the file itself. Always empty for a query, because only the words of a query's result are stored.
    /// </summary>
    public required List<SearchHitLine> MatchingLines { get; init; }

    /// <summary>How many lines of the file contain a search term, used to put the best hits first.</summary>
    public required int MatchingLineCount { get; init; }

    /// <summary>True if every term occurs in the title (file or query name); such hits come first.</summary>
    public required bool IsTitleMatch { get; init; }

    /// <summary>For a query: true if some of the terms were only found in its last result (not in its name or SQL).</summary>
    public required bool IsFoundInQueryResult { get; init; }
}

/// <summary>
/// The search itself. Finds the documents whose stored word lists contain every search term, ignoring upper/lower
/// case; words may be in any order and may be the start or part of a longer word ("ord" finds "orders").
/// Text in "double quotes" must occur exactly as typed (e.g. "open orders"): the index only knows single words,
/// so that is checked in the file itself. For the best results, the matching lines are read from the files.
/// </summary>
public static class GlobalSearcher
{
    /// <summary>Most matching lines shown per document.</summary>
    public const int MaximumLinesPerHit = 3;

    /// <summary>Most documents shown, so a one-letter search on a big index stays quick to display.</summary>
    public const int MaximumHitCount = 200;

    /// <summary>
    /// For this many of the best results the file is read to show the matching lines (and to check exact phrases).
    /// Kept low because files may be on slow network shares and the search runs again on every pause in typing.
    /// </summary>
    public const int MaximumFilesReadPerSearch = 60;

    /// <summary>Lines longer than this are cut down to the part around the first hit.</summary>
    private const int MaximumLineLength = 180;

    /// <summary>
    /// Ignores upper/lower case (also for letters such as "Å"/"å") but not accents, so "å" and "a" stay different
    /// letters. Ordinal comparison is used because it is many times faster than a language-aware one.
    /// </summary>
    private const StringComparison TermComparison = StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Text of files read for recent searches, so typing one more letter does not read the same files again.
    /// An entry is only used while the file's modified time and size are unchanged.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (DateTime LastWriteTimeUtc, long SizeInBytes, string Text)> recentFileTexts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Most files kept in <see cref="recentFileTexts"/>; it is emptied when it grows past this.</summary>
    private const int MaximumRememberedFileCount = 300;

    /// <summary>
    /// Splits what the user typed into search terms: words, and "quoted phrases" kept whole.
    /// An unclosed quote takes the rest of the text as one phrase.
    /// </summary>
    public static List<string> ParseSearchTerms(string searchText)
    {
        var searchTerms = new List<string>();
        var currentTerm = new StringBuilder();
        bool isInsideQuotes = false;

        // Adds the term built so far (if any) and starts a new one.
        void FinishCurrentTerm()
        {
            string finishedTerm = currentTerm.ToString().Trim();
            if (finishedTerm.Length > 0 && !searchTerms.Contains(finishedTerm, StringComparer.OrdinalIgnoreCase))
            {
                searchTerms.Add(finishedTerm);
            }
            currentTerm.Clear();
        }

        foreach (char typedCharacter in searchText)
        {
            if (typedCharacter == '"')
            {
                // A quote starts or ends a phrase.
                FinishCurrentTerm();
                isInsideQuotes = !isInsideQuotes;
            }
            else if (char.IsWhiteSpace(typedCharacter) && !isInsideQuotes)
            {
                FinishCurrentTerm();
            }
            else
            {
                currentTerm.Append(typedCharacter);
            }
        }
        FinishCurrentTerm();
        return searchTerms;
    }

    /// <summary>
    /// Finds the documents that contain every term and returns them best first: title matches first, then the files
    /// with the most matching lines. Runs on a background thread (it reads some files).
    /// </summary>
    public static List<SearchHit> Search(IEnumerable<SearchIndexDocument> indexedDocuments, IReadOnlyList<string> searchTerms, CancellationToken cancellationToken)
    {
        if (searchTerms.Count == 0)
        {
            return new List<SearchHit>();
        }

        // 1. Candidates from the word lists alone (fast: no files are read).
        var candidateDocuments = indexedDocuments
            .Where(indexedDocument => HasEveryTermInIndex(indexedDocument, searchTerms))
            .Select(indexedDocument => (Document: indexedDocument, IsTitleMatch: searchTerms.All(searchTerm => indexedDocument.Title.Contains(searchTerm, TermComparison))))
            .OrderByDescending(candidate => candidate.IsTitleMatch)
            .ThenBy(candidate => candidate.Document.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        // 2. For the best candidates, read the file to show the matching lines and check exact phrases.
        var searchHits = new List<SearchHit>();
        int readFileCount = 0;
        foreach (var (candidateDocument, isTitleMatch) in candidateDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (searchHits.Count >= MaximumHitCount)
            {
                break;
            }

            if (candidateDocument.Kind == SearchDocumentKind.Query)
            {
                searchHits.Add(BuildQueryHit(candidateDocument, searchTerms, isTitleMatch));
                continue;
            }

            string? fileText = null;
            if (readFileCount < MaximumFilesReadPerSearch)
            {
                readFileCount++;
                fileText = ReadFileText(candidateDocument.Key);
            }

            var fileHit = BuildFileHit(candidateDocument, searchTerms, isTitleMatch, fileText);
            if (fileHit != null)
            {
                searchHits.Add(fileHit);
            }
        }

        TrimRememberedFiles();
        return searchHits
            .OrderByDescending(searchHit => searchHit.IsTitleMatch)
            .ThenByDescending(searchHit => searchHit.MatchingLineCount)
            .ThenBy(searchHit => searchHit.Document.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// True if every term is found in the document's name or location, or (word by word) in its stored words or,
    /// for a query, the words of its last result.
    /// </summary>
    private static bool HasEveryTermInIndex(SearchIndexDocument indexedDocument, IReadOnlyList<string> searchTerms)
    {
        // The folder is searched too for files, so e.g. "projects readme" finds Projects\README.md.
        string titleAndLocation = indexedDocument.Kind == SearchDocumentKind.File ? indexedDocument.Key : indexedDocument.Title;
        foreach (string searchTerm in searchTerms)
        {
            if (titleAndLocation.Contains(searchTerm, TermComparison))
            {
                continue;
            }

            // Each word of the term must be in the document's words or its query result's words.
            bool hasEveryWord = SearchWordList.SplitIntoWords(searchTerm).All(termWord =>
                indexedDocument.Words.Contains(termWord, StringComparison.Ordinal)
                || indexedDocument.LastQueryResultWords.Contains(termWord, StringComparison.Ordinal));
            if (!hasEveryWord)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>A hit for a query. No lines can be shown, but it says whether the match was in the last result.</summary>
    private static SearchHit BuildQueryHit(SearchIndexDocument queryDocument, IReadOnlyList<string> searchTerms, bool isTitleMatch)
    {
        // Found in the result if some term is in neither the name nor the SQL.
        bool isFoundInQueryResult = searchTerms.Any(searchTerm =>
            !queryDocument.Title.Contains(searchTerm, TermComparison)
            && !SearchWordList.ContainsAllWordsOf(queryDocument.Words, searchTerm));
        return new SearchHit
        {
            Document = queryDocument,
            MatchingLines = new List<SearchHitLine>(),
            MatchingLineCount = 0,
            IsTitleMatch = isTitleMatch,
            IsFoundInQueryResult = isFoundInQueryResult,
        };
    }

    /// <summary>
    /// A hit for a file, with its matching lines when its text could be read. Returns null if the text shows that a
    /// term (e.g. an exact "phrase" whose words are all in the file, but not next to each other) is not really there.
    /// </summary>
    private static SearchHit? BuildFileHit(SearchIndexDocument fileDocument, IReadOnlyList<string> searchTerms, bool isTitleMatch, string? fileText)
    {
        var matchingLines = new List<SearchHitLine>();
        int matchingLineCount = 0;
        if (fileText != null)
        {
            // Every term must occur as typed in the text, or in the file's name or folder.
            bool isEveryTermReallyThere = searchTerms.All(searchTerm =>
                fileText.Contains(searchTerm, TermComparison) || fileDocument.Key.Contains(searchTerm, TermComparison));
            if (!isEveryTermReallyThere)
            {
                return null;
            }

            (matchingLines, matchingLineCount) = FindMatchingLines(fileText, searchTerms);
        }

        return new SearchHit
        {
            Document = fileDocument,
            MatchingLines = matchingLines,
            MatchingLineCount = matchingLineCount,
            IsTitleMatch = isTitleMatch,
            IsFoundInQueryResult = false,
        };
    }

    /// <summary>
    /// Reads a file's text (from <see cref="recentFileTexts"/> if it has not changed). Returns null if it cannot be
    /// read right now (moved, locked, network down) or is too large to search line by line.
    /// </summary>
    private static string? ReadFileText(string filePath)
    {
        try
        {
            var fileDetails = new FileInfo(filePath);
            if (!fileDetails.Exists || fileDetails.Length > SearchIndex.MaximumIndexedFileSizeInBytes)
            {
                return null;
            }

            if (recentFileTexts.TryGetValue(filePath, out var rememberedFile)
                && rememberedFile.LastWriteTimeUtc == fileDetails.LastWriteTimeUtc
                && rememberedFile.SizeInBytes == fileDetails.Length)
            {
                return rememberedFile.Text;
            }

            // FileShare.ReadWrite: an editor that has the file open must not stop it from being searched.
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var fileReader = new StreamReader(fileStream, detectEncodingFromByteOrderMarks: true);
            string fileText = fileReader.ReadToEnd();
            recentFileTexts[filePath] = (fileDetails.LastWriteTimeUtc, fileDetails.Length, fileText);
            return fileText;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Empties the remembered file texts once there are too many, so memory use stays small.</summary>
    private static void TrimRememberedFiles()
    {
        if (recentFileTexts.Count > MaximumRememberedFileCount)
        {
            recentFileTexts.Clear();
        }
    }

    /// <summary>
    /// The lines that contain any of the terms: the best few (lines with more different terms first, then earlier
    /// lines), shown in file order, plus how many lines matched in total.
    /// </summary>
    private static (List<SearchHitLine> BestLines, int MatchingLineCount) FindMatchingLines(string text, IReadOnlyList<string> searchTerms)
    {
        var scoredLines = new List<(string LineText, int TermCount, int LineNumber)>();
        int lineNumber = 0;
        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            int termCount = searchTerms.Count(searchTerm => rawLine.Contains(searchTerm, TermComparison));
            if (termCount > 0)
            {
                scoredLines.Add((rawLine, termCount, lineNumber));
            }
        }

        var bestLines = scoredLines
            .OrderByDescending(scoredLine => scoredLine.TermCount)
            .ThenBy(scoredLine => scoredLine.LineNumber)
            .Take(MaximumLinesPerHit)
            .OrderBy(scoredLine => scoredLine.LineNumber)
            .Select(scoredLine => new SearchHitLine(ShortenLineAroundHit(scoredLine.LineText, searchTerms), scoredLine.LineNumber))
            .ToList();
        return (bestLines, scoredLines.Count);
    }

    /// <summary>Position of the first occurrence of the term in the text (ignoring upper/lower case), or -1.</summary>
    public static int IndexOfTerm(string text, string searchTerm, int startIndex = 0)
    {
        return text.IndexOf(searchTerm, startIndex, TermComparison);
    }

    /// <summary>Trims a line and, if it is long, keeps only the part around the first hit with "…" at the cut ends.</summary>
    private static string ShortenLineAroundHit(string lineText, IReadOnlyList<string> searchTerms)
    {
        string trimmedLine = lineText.Trim();
        if (trimmedLine.Length <= MaximumLineLength)
        {
            return trimmedLine;
        }

        int firstHitIndex = searchTerms
            .Select(searchTerm => IndexOfTerm(trimmedLine, searchTerm))
            .Where(foundIndex => foundIndex >= 0)
            .DefaultIfEmpty(0)
            .Min();

        // Show some text before the hit so it is read in context.
        int cutStartIndex = Math.Max(0, firstHitIndex - MaximumLineLength / 3);
        int cutLength = Math.Min(MaximumLineLength, trimmedLine.Length - cutStartIndex);
        string shortenedLine = trimmedLine.Substring(cutStartIndex, cutLength);
        string leadingMarker = cutStartIndex > 0 ? "…" : string.Empty;
        string trailingMarker = cutStartIndex + cutLength < trimmedLine.Length ? "…" : string.Empty;
        return leadingMarker + shortenedLine + trailingMarker;
    }
}
