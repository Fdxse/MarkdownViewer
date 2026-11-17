using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace MarkdownViewer.PostIts;

/// <summary>
/// One colour the user can give a post-it note, with a category that says what the colour means
/// (for example "Urgent" or "Work"). The palette is a fixed list the user extends in
/// Tools → Post-it colours; there is deliberately no free colour picker on the notes themselves.
/// The standard yellow is not in this list: it is built in, always available, and used for every
/// note that has no colour of its own.
/// </summary>
/// <remarks>Stored as JSON in settings.json (<see cref="MarkdownViewer.Macros.AppSettings.PostItColours"/>).</remarks>
public partial class PostItColour
{
    /// <summary>The standard post-it yellow, used for every note without a colour of its own.</summary>
    public const string StandardYellowHex = "#FFF68F";

    /// <summary>Name shown for the standard yellow in menus.</summary>
    public const string StandardYellowName = "Yellow (standard)";

    /// <summary>
    /// A fixed id that never changes, also when the colour is renamed or its hex value is adjusted.
    /// Notes remember their colour by this id, so they follow such changes.
    /// </summary>
    public string Id { get; set; } = NewId();

    /// <summary>The name of the colour as shown in menus, for example "Blue".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The colour as "#RRGGBB", for example "#B8E0FF".</summary>
    public string Hex { get; set; } = string.Empty;

    /// <summary>What a note in this colour means, for example "Work". May be empty.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>A new random id for a colour (short, so the settings file stays readable).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>A copy, so the settings dialog can be cancelled without changing the live palette.</summary>
    public PostItColour Clone() => new()
    {
        Id = Id,
        Name = Name,
        Hex = Hex,
        Category = Category,
    };

    /// <summary>The text shown for this colour in menus: "Blue – Work", or just "Blue" without a category.</summary>
    [JsonIgnore]
    public string MenuText => string.IsNullOrWhiteSpace(Category) ? Name : $"{Name} – {Category}";

    /// <summary>
    /// The palette the first time the app runs (and for settings files from before colours existed).
    /// Only examples: the user can rename, change, remove or add to them.
    /// </summary>
    public static List<PostItColour> CreateDefaultPalette() => new()
    {
        new PostItColour { Name = "Blue", Hex = "#B8E0FF", Category = "Work" },
        new PostItColour { Name = "Green", Hex = "#CDEFB8", Category = "Idea" },
        new PostItColour { Name = "Pink", Hex = "#FFC2DC", Category = "Urgent" },
        new PostItColour { Name = "Orange", Hex = "#FFD59E", Category = "Private" },
    };

    /// <summary>Accepts "#RRGGBB" or "RRGGBB" (any case, surrounding spaces allowed).</summary>
    [GeneratedRegex("^#?(?<hexDigits>[0-9a-fA-F]{6})$")]
    private static partial Regex HexColourPattern();

    /// <summary>
    /// Turns what the user typed into "#RRGGBB" in capitals. Returns null if it is not a six-digit hex colour.
    /// </summary>
    public static string? NormalizeHex(string? typedHex)
    {
        var hexMatch = HexColourPattern().Match((typedHex ?? string.Empty).Trim());
        return hexMatch.Success ? "#" + hexMatch.Groups["hexDigits"].Value.ToUpperInvariant() : null;
    }

    /// <summary>The colour of a "#RRGGBB" value; the standard yellow if the value is not valid.</summary>
    public static Color ParseColourOrStandardYellow(string? hexValue)
    {
        string usableHex = NormalizeHex(hexValue) ?? StandardYellowHex;
        return Color.FromRgb(
            byte.Parse(usableHex.AsSpan(1, 2), NumberStyles.HexNumber),
            byte.Parse(usableHex.AsSpan(3, 2), NumberStyles.HexNumber),
            byte.Parse(usableHex.AsSpan(5, 2), NumberStyles.HexNumber));
    }

    /// <summary>
    /// The same colour a little darker, for the strip along the top of a note where the glue would be
    /// (for the standard yellow this gives roughly the darker yellow the notes always had).
    /// </summary>
    public static Color MakeGlueStripColour(Color paperColour)
    {
        const double darkeningFactor = 0.95;
        return Color.FromRgb(
            (byte)(paperColour.R * darkeningFactor),
            (byte)(paperColour.G * darkeningFactor),
            (byte)(paperColour.B * darkeningFactor));
    }

    /// <summary>
    /// True if the colour is so dark that the black note text would be hard to read on it.
    /// Uses the usual perceived-brightness weights for red, green and blue.
    /// </summary>
    public static bool IsTooDarkForText(Color paperColour)
    {
        double perceivedBrightness = (0.299 * paperColour.R + 0.587 * paperColour.G + 0.114 * paperColour.B) / 255.0;
        return perceivedBrightness < 0.55;
    }

    /// <summary>Makes a frozen (thread-safe, faster) brush of a colour.</summary>
    public static SolidColorBrush CreateFrozenBrush(Color brushColour)
    {
        var colourBrush = new SolidColorBrush(brushColour);
        colourBrush.Freeze();
        return colourBrush;
    }
}
