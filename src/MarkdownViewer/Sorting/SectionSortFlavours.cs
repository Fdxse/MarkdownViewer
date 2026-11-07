using System.Globalization;
using System.Text.RegularExpressions;

namespace MarkdownViewer.Sorting;

/// <summary>The ways sibling sections can be sorted from the heading's right-click menu and the reorder window.</summary>
public enum SectionSortFlavour
{
    /// <summary>Heading text A to Z (numbers in natural order: "Item 2" before "Item 10").</summary>
    AlphabeticalAscending,

    /// <summary>Heading text Z to A.</summary>
    AlphabeticalDescending,

    /// <summary>Date in the heading, newest first; headings without a date go last.</summary>
    DateNewestFirst,

    /// <summary>Date in the heading, oldest first; headings without a date go last.</summary>
    DateOldestFirst,

    /// <summary>Most unticked task boxes first; finished sections end up at the bottom.</summary>
    MostOpenTasksFirst,

    /// <summary>The current order turned upside down.</summary>
    Reverse,
}

/// <summary>
/// The sort orders themselves, plus the helpers that read a date or count open tasks.
/// All sorts are stable: sections that compare equal keep the order they had.
/// </summary>
public static class SectionSortFlavours
{
    /// <summary>Text shown for each flavour in menus and on buttons.</summary>
    public static string GetDisplayName(SectionSortFlavour sortFlavour) => sortFlavour switch
    {
        SectionSortFlavour.AlphabeticalAscending => "Alphabetical A → Z",
        SectionSortFlavour.AlphabeticalDescending => "Alphabetical Z → A",
        SectionSortFlavour.DateNewestFirst => "Date in heading, newest first",
        SectionSortFlavour.DateOldestFirst => "Date in heading, oldest first",
        SectionSortFlavour.MostOpenTasksFirst => "Most open tasks first",
        SectionSortFlavour.Reverse => "Reverse current order",
        _ => sortFlavour.ToString(),
    };

    /// <summary>Returns the sections in the order the flavour asks for. The input list is not changed.</summary>
    public static List<MarkdownSection> Sort(IEnumerable<MarkdownSection> sections, SectionSortFlavour sortFlavour)
    {
        var naturalComparer = new NaturalTextComparer();
        // LINQ's OrderBy/ThenBy is stable, so equal sections keep their current relative order.
        return sortFlavour switch
        {
            SectionSortFlavour.AlphabeticalAscending =>
                sections.OrderBy(section => GetAlphabeticalKey(section.HeadingText), naturalComparer).ToList(),

            SectionSortFlavour.AlphabeticalDescending =>
                sections.OrderByDescending(section => GetAlphabeticalKey(section.HeadingText), naturalComparer).ToList(),

            // "HeadingDate == null" sorts false (has a date) before true (no date), so undated headings go last either way.
            SectionSortFlavour.DateNewestFirst =>
                sections.OrderBy(section => section.HeadingDate == null).ThenByDescending(section => section.HeadingDate).ToList(),

            SectionSortFlavour.DateOldestFirst =>
                sections.OrderBy(section => section.HeadingDate == null).ThenBy(section => section.HeadingDate).ToList(),

            SectionSortFlavour.MostOpenTasksFirst =>
                sections.OrderByDescending(section => section.OpenTaskCount).ToList(),

            SectionSortFlavour.Reverse =>
                sections.Reverse().ToList(),

            _ => sections.ToList(),
        };
    }

    /// <summary>
    /// The heading text used for alphabetical sorting: leading emoji, symbols and Markdown marks such as
    /// "**" or "[" are skipped, so "✅ Beta" sorts under B.
    /// </summary>
    private static string GetAlphabeticalKey(string headingText)
    {
        int firstLetterIndex = 0;
        while (firstLetterIndex < headingText.Length && !char.IsLetterOrDigit(headingText[firstLetterIndex]))
        {
            firstLetterIndex++;
        }
        // A heading made only of symbols keeps its text, so it still sorts somewhere sensible.
        return firstLetterIndex < headingText.Length ? headingText[firstLetterIndex..] : headingText;
    }

    // ------------------------------------------------------------------
    // Dates in headings
    // ------------------------------------------------------------------

    /// <summary>2026-11-07 (also 2026/11/07 and 2026.11.07).</summary>
    private static readonly Regex YearFirstDateRegex = new(@"\b(?<year>\d{4})[-/.](?<month>\d{1,2})[-/.](?<day>\d{1,2})\b");

    /// <summary>07/11/2026 or 7.11.2026, read day first as in Sweden and most of Europe.</summary>
    private static readonly Regex DayFirstDateRegex = new(@"\b(?<day>\d{1,2})[/.](?<month>\d{1,2})[/.](?<year>\d{4})\b");

    /// <summary>"7 nov 2026", "7 november 2026", "7 okt. 2026".</summary>
    private static readonly Regex DayMonthNameDateRegex = new(@"\b(?<day>\d{1,2})\.?\s+(?<monthName>[A-Za-zÅÄÖåäö]{3,})\.?,?\s+(?<year>\d{4})\b");

    /// <summary>"Nov 7, 2026", "November 7 2026".</summary>
    private static readonly Regex MonthNameDayDateRegex = new(@"\b(?<monthName>[A-Za-zÅÄÖåäö]{3,})\.?\s+(?<day>\d{1,2}),?\s+(?<year>\d{4})\b");

    /// <summary>The first three letters of English and Swedish month names, with the month number.</summary>
    private static readonly Dictionary<string, int> MonthNumbersByPrefix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["maj"] = 5,
        ["jun"] = 6, ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["oct"] = 10, ["okt"] = 10,
        ["nov"] = 11, ["dec"] = 12,
    };

    /// <summary>
    /// Finds the first real date in the text, in any of the supported formats. Returns null if there is none
    /// (impossible dates such as 2026-02-31 are ignored).
    /// </summary>
    public static DateTime? FindDateInText(string text)
    {
        DateTime? earliestPlacedDate = null;
        int earliestMatchIndex = int.MaxValue;

        foreach (var dateRegex in new[] { YearFirstDateRegex, DayFirstDateRegex, DayMonthNameDateRegex, MonthNameDayDateRegex })
        {
            foreach (Match dateMatch in dateRegex.Matches(text))
            {
                // Only the date written first in the heading counts.
                if (dateMatch.Index >= earliestMatchIndex)
                {
                    break;
                }

                var parsedDate = TryReadDate(dateMatch);
                if (parsedDate != null)
                {
                    earliestPlacedDate = parsedDate;
                    earliestMatchIndex = dateMatch.Index;
                    break;
                }
            }
        }
        return earliestPlacedDate;
    }

    /// <summary>Turns a regex match into a date, or null if the parts do not make a real date.</summary>
    private static DateTime? TryReadDate(Match dateMatch)
    {
        int yearNumber = int.Parse(dateMatch.Groups["year"].Value, CultureInfo.InvariantCulture);
        int dayNumber = int.Parse(dateMatch.Groups["day"].Value, CultureInfo.InvariantCulture);

        int monthNumber;
        if (dateMatch.Groups["monthName"].Success)
        {
            string monthName = dateMatch.Groups["monthName"].Value;
            if (!MonthNumbersByPrefix.TryGetValue(monthName[..3], out monthNumber))
            {
                return null; // a word that is not a month, e.g. "Sprint 3 2026"
            }
        }
        else
        {
            monthNumber = int.Parse(dateMatch.Groups["month"].Value, CultureInfo.InvariantCulture);
        }

        bool isRealDate = yearNumber >= 1 && monthNumber is >= 1 and <= 12
            && dayNumber >= 1 && dayNumber <= DateTime.DaysInMonth(yearNumber, monthNumber);
        return isRealDate ? new DateTime(yearNumber, monthNumber, dayNumber) : null;
    }

    // ------------------------------------------------------------------
    // Open tasks
    // ------------------------------------------------------------------

    /// <summary>An unticked task: "- [ ]", "* [ ]", "+ [ ]" or "1. [ ]", any indent.</summary>
    private static readonly Regex OpenTaskLineRegex = new(@"^[ \t]*(?:[-*+]|\d+[.)])[ \t]+\[ \]");

    /// <summary>Counts unticked task boxes in the text, skipping anything inside ``` or ~~~ code blocks.</summary>
    public static int CountOpenTasks(string sectionText)
    {
        int openTaskCount = 0;
        bool isInsideCodeFence = false;
        foreach (string lineText in sectionText.Split('\n'))
        {
            string trimmedLine = lineText.TrimStart();
            if (trimmedLine.StartsWith("```", StringComparison.Ordinal) || trimmedLine.StartsWith("~~~", StringComparison.Ordinal))
            {
                isInsideCodeFence = !isInsideCodeFence;
                continue;
            }
            if (!isInsideCodeFence && OpenTaskLineRegex.IsMatch(lineText))
            {
                openTaskCount++;
            }
        }
        return openTaskCount;
    }
}

/// <summary>
/// Compares text the way people expect: case is ignored and runs of digits compare as numbers,
/// so "Item 2" comes before "Item 10". Letters use the current culture, so å, ä and ö sort as in Swedish.
/// </summary>
public sealed class NaturalTextComparer : IComparer<string>
{
    /// <summary>Compares two strings piece by piece, where a piece is either a run of digits or a run of other characters.</summary>
    public int Compare(string? firstText, string? secondText)
    {
        if (ReferenceEquals(firstText, secondText)) return 0;
        if (firstText == null) return -1;
        if (secondText == null) return 1;

        int firstPosition = 0;
        int secondPosition = 0;
        while (firstPosition < firstText.Length && secondPosition < secondText.Length)
        {
            string firstPiece = ReadPiece(firstText, ref firstPosition);
            string secondPiece = ReadPiece(secondText, ref secondPosition);

            int pieceComparison = char.IsDigit(firstPiece[0]) && char.IsDigit(secondPiece[0])
                ? CompareDigitRuns(firstPiece, secondPiece)
                : string.Compare(firstPiece, secondPiece, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
            if (pieceComparison != 0)
            {
                return pieceComparison;
            }
        }

        // One text is a prefix of the other: the shorter one comes first.
        return (firstText.Length - firstPosition).CompareTo(secondText.Length - secondPosition);
    }

    /// <summary>Reads the next run of digits or run of non-digits, starting at the position, and moves past it.</summary>
    private static string ReadPiece(string text, ref int position)
    {
        int pieceStart = position;
        bool isDigitRun = char.IsDigit(text[position]);
        while (position < text.Length && char.IsDigit(text[position]) == isDigitRun)
        {
            position++;
        }
        return text[pieceStart..position];
    }

    /// <summary>Compares two digit runs by value without parsing (so very long numbers work): fewer digits = smaller.</summary>
    private static int CompareDigitRuns(string firstDigits, string secondDigits)
    {
        string firstTrimmed = firstDigits.TrimStart('0');
        string secondTrimmed = secondDigits.TrimStart('0');
        int lengthComparison = firstTrimmed.Length.CompareTo(secondTrimmed.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(firstTrimmed, secondTrimmed);
    }
}
