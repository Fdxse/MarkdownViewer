using System.Text.Json.Serialization;

namespace MarkdownViewer.Bookshelf;

/// <summary>
/// How one image lies on a light board: its position, size, rotation, mirroring, place in the stack and the
/// user's note about it. Saved per board in the settings file (<see cref="NamedBookshelf.LightBoardImages"/>),
/// by the image file's display name. The height is never stored: it always follows from the width and the
/// image's own proportions, so an image can never be stretched out of shape.
/// </summary>
public class LightBoardImagePlacement
{
    /// <summary>Distance of the image's left edge from the left of the board, in device-independent pixels.</summary>
    public double X { get; set; }

    /// <summary>Distance of the image's top edge from the top of the board, in device-independent pixels.</summary>
    public double Y { get; set; }

    /// <summary>Width of the image on the board, in device-independent pixels (the height follows the image's proportions).</summary>
    public double Width { get; set; }

    /// <summary>Rotation in degrees, clockwise (negative turns the image to the left).</summary>
    public double Angle { get; set; }

    /// <summary>True if the image is mirrored left-to-right.</summary>
    // Left out of the settings file while false, to keep it short.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlipHorizontal { get; set; }

    /// <summary>True if the image is mirrored top-to-bottom (upside down, but mirrored rather than rotated).</summary>
    // Left out of the settings file while false.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FlipVertical { get; set; }

    /// <summary>Place in the stack: an image with a higher number lies on top of images with a lower one.</summary>
    public int StackLevel { get; set; }

    /// <summary>The user's note about the image. Not drawn on the image; shown in its tooltip and on double-click.</summary>
    // Left out of the settings file while there is no note.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }

    /// <summary>A separate copy, so changing it does not change the original.</summary>
    public LightBoardImagePlacement Clone() => (LightBoardImagePlacement)MemberwiseClone();
}
