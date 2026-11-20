using System.Globalization;
using System.Windows.Media;
using MarkdownViewer.PostIts;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// Colours and helpers for the book spines in the bookshelf: every bookshelf is shown as a tall, thin book,
/// and clicking a book shows that shelf. The user sets each book's colour, text and order number.
/// </summary>
public static class ShelfSpine
{
    /// <summary>
    /// Colours handed out, in turn, to shelves that have no spine colour yet (shelves from older settings files and
    /// new shelves). Dark "leather binding" colours, so the light spine text reads well on all of them.
    /// </summary>
    public static readonly string[] DefaultSpineColours =
    {
        "#2E4A7A", // navy
        "#7A2E2E", // oxblood
        "#2E6B4A", // bottle green
        "#8A6A2E", // ochre
        "#5A3E7A", // plum
        "#2E6B70", // teal
        "#9A4A2A", // rust
        "#4A4A4A", // charcoal
    };

    /// <summary>Spine colour of the built-in Archive shelf (cannot be changed, the Archive is not stored in the settings).</summary>
    public const string ArchiveSpineColour = "#8C7B6B";

    /// <summary>Spine colour of the built-in Trash shelf (cannot be changed, the Trash is not stored in the settings).</summary>
    public const string TrashSpineColour = "#5E5E5E";

    /// <summary>Gap between two order numbers given out automatically, so a shelf can later be put between two others (e.g. 15).</summary>
    public const int OrderNumberStep = 10;

    /// <summary>The colour used when a spine colour is missing or not a valid "#RRGGBB" value.</summary>
    private const string FallbackSpineColour = "#2E4A7A";

    /// <summary>The spine colour of a shelf (fixed colours for Archive and Trash).</summary>
    public static Color GetSpineColour(NamedBookshelf bookshelf)
    {
        string spineHex = bookshelf.IsArchiveShelf ? ArchiveSpineColour
            : bookshelf.IsTrashShelf ? TrashSpineColour
            : bookshelf.SpineColour ?? FallbackSpineColour;
        return ParseColour(spineHex);
    }

    /// <summary>The colour of a "#RRGGBB" value; the fallback navy if the value is not valid.</summary>
    public static Color ParseColour(string? hexValue)
    {
        // The post-it palette already knows how to read and clean up hex values typed by the user.
        string usableHex = PostItColour.NormalizeHex(hexValue) ?? FallbackSpineColour;
        return Color.FromRgb(
            byte.Parse(usableHex.AsSpan(1, 2), NumberStyles.HexNumber),
            byte.Parse(usableHex.AsSpan(3, 2), NumberStyles.HexNumber),
            byte.Parse(usableHex.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    /// <summary>Text colour that reads well on the given spine colour: near-white on dark spines, near-black on light ones.</summary>
    public static Color GetTextColour(Color spineColour)
    {
        // Usual perceived-brightness weights for red, green and blue.
        double perceivedBrightness = (0.299 * spineColour.R + 0.587 * spineColour.G + 0.114 * spineColour.B) / 255.0;
        return perceivedBrightness < 0.6 ? Color.FromRgb(0xF8, 0xF4, 0xEA) : Color.FromRgb(0x22, 0x22, 0x22);
    }

    /// <summary>The same colour a little darker (or lighter for very dark colours), for the bands printed across a spine.</summary>
    public static Color GetBandColour(Color spineColour)
    {
        double perceivedBrightness = (0.299 * spineColour.R + 0.587 * spineColour.G + 0.114 * spineColour.B) / 255.0;
        if (perceivedBrightness < 0.2)
        {
            // Darkening black gives black: lighten instead so the bands still show.
            return Color.FromRgb((byte)Math.Min(255, spineColour.R + 50), (byte)Math.Min(255, spineColour.G + 50), (byte)Math.Min(255, spineColour.B + 50));
        }

        const double darkeningFactor = 0.72;
        return Color.FromRgb(
            (byte)(spineColour.R * darkeningFactor),
            (byte)(spineColour.G * darkeningFactor),
            (byte)(spineColour.B * darkeningFactor));
    }

    /// <summary>
    /// A height for the spine that varies a little from book to book, like real books, but is always the same for the
    /// same shelf name (so the books do not jump around between starts). Between <paramref name="shortestHeight"/> and
    /// <paramref name="shortestHeight"/> + <paramref name="heightVariation"/>.
    /// </summary>
    public static double GetSpineHeight(NamedBookshelf bookshelf, double shortestHeight, int heightVariation)
    {
        // string.GetHashCode differs between runs in .NET, so make a simple stable hash from the characters instead.
        int stableNameHash = 17;
        foreach (char nameCharacter in bookshelf.Name.ToUpperInvariant())
        {
            stableNameHash = unchecked(stableNameHash * 31 + nameCharacter);
        }
        return shortestHeight + Math.Abs(stableNameHash % (heightVariation + 1));
    }

    /// <summary>
    /// Puts shelves in spine order: lowest order number first, shelves with the same number (or none) by name.
    /// </summary>
    public static List<NamedBookshelf> SortBySpineOrder(IEnumerable<NamedBookshelf> bookshelves)
    {
        return bookshelves
            .OrderBy(bookshelf => bookshelf.SpineOrder ?? int.MaxValue)
            .ThenBy(bookshelf => bookshelf.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
