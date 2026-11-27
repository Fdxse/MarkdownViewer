using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using MarkdownViewer.Bookshelf;
using MarkdownViewer.Todo;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// Light boards: a folder bookshelf of images instead of Markdown files. The images lie on a bright board in the
/// document area, like slides and photos on a light table. Each image can be moved, scaled (always keeping its
/// proportions), rotated, flipped and given a note. The note is not drawn on the image: it shows in the image's
/// tooltip and opens on a double-click. Images are put on the board with Import... (or by dropping image files
/// on it), which copies them into the board's folder.
/// How each image lies on the board is saved per board in the settings file (<see cref="NamedBookshelf.LightBoardImages"/>).
/// Moving, scaling and rotating with the mouse is in MainWindow.LightBoardLayout.cs.
/// </summary>
public partial class MainWindow
{
    /// <summary>The white frame around each image (like the mount of a slide), in pixels on each side.</summary>
    private const double LightBoardFrameThickness = 6;

    /// <summary>A newly placed image fits in a square of this size (its longest side gets this length).</summary>
    private const double DefaultLightBoardImageSize = 240;

    /// <summary>Images are never made narrower and shorter than this, so they can always be grabbed again.</summary>
    private const double MinimumLightBoardImageSize = 40;

    /// <summary>Images are never made wider or taller than this, so a slip of the mouse cannot make a giant board.</summary>
    private const double MaximumLightBoardImageSize = 4000;

    /// <summary>
    /// Images are decoded at most this many pixels wide or high. Enough to look sharp when scaled up quite a bit,
    /// while a folder of large photos does not use gigabytes of memory.
    /// </summary>
    private const int MaximumLightBoardDecodeSize = 1600;

    /// <summary>Most images drawn on one board, so a folder with thousands of pictures does not freeze the app.</summary>
    private const int MaximumLightBoardImageCount = 200;

    /// <summary>File dialog filter for importing images.</summary>
    private const string LightBoardImageDialogFilter =
        "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff;*.webp|All files (*.*)|*.*";

    /// <summary>The frame colour of an image: off-white, like a slide mount.</summary>
    private static readonly Brush LightBoardFrameBrush = CreateFrozenPostItBrush("#FCFCFA");

    /// <summary>Colour of the round handles (rotate and scale) on an image.</summary>
    private static readonly Brush LightBoardHandleBrush = CreateFrozenPostItBrush("#2F6FB3");

    /// <summary>True while the light board covers the document area.</summary>
    private bool isLightBoardShown;

    /// <summary>The image files and save times the board was drawn from; the board is only redrawn when this changes.</summary>
    private string lightBoardDrawnFromSignature = string.Empty;

    /// <summary>
    /// Images already read from disk, by file path, with the file's save time. Redrawing the board (e.g. after an
    /// import) then does not read every picture again. Emptied when another shelf is chosen.
    /// </summary>
    private readonly Dictionary<string, LoadedLightBoardBitmap> lightBoardBitmapCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An image read from disk: the (possibly reduced) picture plus its real size in pixels.</summary>
    private sealed record LoadedLightBoardBitmap(long LastSavedTicks, BitmapSource Bitmap, int PixelWidth, int PixelHeight);

    /// <summary>What an image on the board knows about itself; kept in the card's <see cref="FrameworkElement.Tag"/>.</summary>
    private sealed class LightBoardImageState
    {
        /// <summary>The image file the card shows.</summary>
        public required BookshelfEntry BookshelfEntry { get; init; }

        /// <summary>Width divided by height of the picture. Fixed, so scaling always keeps the proportions.</summary>
        public required double AspectRatio { get; init; }

        /// <summary>The picture's real width in pixels (0 if it could not be read); used by "Actual size".</summary>
        public required int PixelWidth { get; init; }

        /// <summary>The rotation of the whole card around its centre.</summary>
        public required RotateTransform RotationTransform { get; init; }

        /// <summary>The mirroring of the picture (ScaleX/ScaleY of -1 flips it); the frame is not affected.</summary>
        public required ScaleTransform FlipTransform { get; init; }

        /// <summary>The small "note" badge in the corner, shown only when the image has a note.</summary>
        public required FrameworkElement NoteBadge { get; init; }

        /// <summary>The round handles (rotate and scale), shown only while the mouse is over the image.</summary>
        public required List<FrameworkElement> Handles { get; init; }

        /// <summary>Width of the picture on the board (without the frame); the height follows from <see cref="AspectRatio"/>.</summary>
        public double ImageWidth { get; set; }

        /// <summary>The user's note about the image, or null.</summary>
        public string? Note { get; set; }
    }

    // ------------------------------------------------------------------
    // Showing and hiding the board
    // ------------------------------------------------------------------

    /// <summary>Called whenever another shelf is shown: a light board covers the document area, other shelves do not.</summary>
    private void ShowOrHideLightBoardForCurrentShelf()
    {
        // Pictures of another board are not needed any more.
        lightBoardBitmapCache.Clear();
        if (currentBookshelf.IsLightBoard)
        {
            // Always draw a freshly chosen board from scratch.
            lightBoardDrawnFromSignature = string.Empty;
            ShowLightBoard();
        }
        else
        {
            HideLightBoard();
        }
    }

    /// <summary>Puts the light board of the shown shelf over the document area, redrawing it if the images changed.</summary>
    private void ShowLightBoard()
    {
        if (!currentBookshelf.IsLightBoard)
        {
            return;
        }

        // Leave edit mode first: the editor would otherwise sit hidden under the board (unsaved typing is kept).
        if (isEditMode)
        {
            SetEditMode(false);
        }

        isLightBoardShown = true;
        LightBoardView.Visibility = Visibility.Visible;
        // The document underneath is hidden, so Find, zoom and the mouse wheel cannot reach it by accident.
        ContentGrid.Visibility = Visibility.Collapsed;
        RefreshLightBoardIfChanged();
        UpdateBackToPostItBoardBar();
    }

    /// <summary>Shows the document area again instead of the light board. Safe to call when no light board is shown.</summary>
    private void HideLightBoard()
    {
        isLightBoardShown = false;
        LightBoardView.Visibility = Visibility.Collapsed;
        // A post-it board keeps the document hidden while it is on screen.
        if (!isPostItBoardShown)
        {
            ContentGrid.Visibility = Visibility.Visible;
        }
        UpdateBackToPostItBoardBar();
    }

    /// <summary>Shelf menu → New light board: like a new folder bookshelf, but for images.</summary>
    private void NewLightBoardMenuItem_Click(object sender, RoutedEventArgs clickDetails)
    {
        CreateFolderShelf(FolderShelfLook.LightBoard);
    }

    /// <summary>The "Import images..." button above the board.</summary>
    private void LightBoardImportButton_Click(object sender, RoutedEventArgs clickDetails)
    {
        ImportImagesToLightBoard();
    }

    // ------------------------------------------------------------------
    // Drawing the board
    // ------------------------------------------------------------------

    /// <summary>Redraws the board if an image was added, removed or saved since it was drawn (only while it is on screen).</summary>
    private void RefreshLightBoardIfChanged()
    {
        // While an image is being moved, scaled or rotated the board is left alone; it catches up when the mouse is released.
        if (!isLightBoardShown || !currentBookshelf.IsLightBoard || isLightBoardImageBeingDragged)
        {
            return;
        }

        string currentSignature = BuildLightBoardSignature();
        if (currentSignature == lightBoardDrawnFromSignature)
        {
            return;
        }

        lightBoardDrawnFromSignature = currentSignature;
        RebuildLightBoardImages();
    }

    /// <summary>One line per image with its path and last save time (sorted), used to notice changes in the folder.</summary>
    private string BuildLightBoardSignature()
    {
        var signatureLines = new List<string>();
        foreach (var bookshelfEntry in currentBookshelf.Entries.Take(MaximumLightBoardImageCount))
        {
            signatureLines.Add($"{bookshelfEntry.FilePath}|{GetLastSavedTicks(bookshelfEntry.FilePath)}");
        }
        signatureLines.Sort(StringComparer.OrdinalIgnoreCase);
        return string.Join('\n', signatureLines);
    }

    /// <summary>When a file was last saved, as ticks; 0 if it cannot be inspected.</summary>
    private static long GetLastSavedTicks(string filePath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(filePath).Ticks;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Clears the board and draws one card per image of the shown shelf, plus the board's title.</summary>
    private void RebuildLightBoardImages()
    {
        LightBoardCanvas.Children.Clear();
        // Lay the empty board out first, so its width is known when new images are put into rows.
        LightBoardView.UpdateLayout();

        LightBoardTitleText.Text = currentBookshelf.Name;
        int imageCount = currentBookshelf.Entries.Count;
        string imageCountText = imageCount == 1 ? "1 image" : $"{imageCount:N0} images";
        string subfolderText = currentBookshelf.IncludeSubfolders ? " and its subfolders" : string.Empty;
        string cutShortText = imageCount > MaximumLightBoardImageCount ? $" (only the first {MaximumLightBoardImageCount} are shown)" : string.Empty;
        LightBoardSubtitleText.Text =
            $"{imageCountText} from {currentBookshelf.FolderPath}{subfolderText}{cutShortText}. " +
            "Drag an image to move it. Drag its corner handles to scale it (the proportions are kept) or rotate it, " +
            "or use Ctrl + mouse wheel to scale and Shift + mouse wheel to rotate. " +
            "Double-click an image to read or write its note; right-click it to flip it and more.";

        // No images: say why (an empty folder, or a folder that is gone).
        LightBoardEmptyHint.Visibility = imageCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        LightBoardEmptyHint.Text = Directory.Exists(currentBookshelf.FolderPath)
            ? "There are no images on this board yet. Click \"Import images...\", or drop image files here; they are copied into the board's folder."
            : "The folder was not found. It may have been moved, renamed or deleted, or its drive is not connected.";

        var drawnCards = new List<Border>();
        foreach (var bookshelfEntry in currentBookshelf.Entries.Take(MaximumLightBoardImageCount))
        {
            var imageCard = CreateLightBoardImageCard(bookshelfEntry);
            drawnCards.Add(imageCard);
            LightBoardCanvas.Children.Add(imageCard);
        }
        // Each image to its saved place, size, rotation and stacking (or into rows if it has none yet).
        PlaceLightBoardImagesOnBoard(drawnCards);
    }

    /// <summary>
    /// Reads an image from disk (or the cache): its real size in pixels, and the picture itself decoded at a
    /// sensible size. Returns null if the file is not a readable image. The file is not kept open (or locked).
    /// </summary>
    private LoadedLightBoardBitmap? LoadLightBoardBitmap(string imageFilePath)
    {
        long lastSavedTicks = GetLastSavedTicks(imageFilePath);
        if (lightBoardBitmapCache.TryGetValue(imageFilePath, out var cachedBitmap) && cachedBitmap.LastSavedTicks == lastSavedTicks)
        {
            return cachedBitmap;
        }

        try
        {
            // First only the size in pixels (DelayCreation reads the header, not the whole picture).
            int pixelWidth;
            int pixelHeight;
            double cameraRotationAngle;
            using (var imageStream = File.OpenRead(imageFilePath))
            {
                var sizeDecoder = BitmapDecoder.Create(imageStream,
                    BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                pixelWidth = sizeDecoder.Frames[0].PixelWidth;
                pixelHeight = sizeDecoder.Frames[0].PixelHeight;
                cameraRotationAngle = GetCameraRotationAngle(sizeDecoder.Frames[0]);
            }

            // Then the picture, shrunk while decoding if it is very large. Only one side is given, so the proportions stay.
            var pictureBitmap = new BitmapImage();
            pictureBitmap.BeginInit();
            // OnLoad reads the whole file now and closes it, so the file can still be renamed, moved or trashed.
            pictureBitmap.CacheOption = BitmapCacheOption.OnLoad;
            pictureBitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            pictureBitmap.UriSource = new Uri(imageFilePath);
            if (pixelWidth >= pixelHeight && pixelWidth > MaximumLightBoardDecodeSize)
            {
                pictureBitmap.DecodePixelWidth = MaximumLightBoardDecodeSize;
            }
            else if (pixelHeight > pixelWidth && pixelHeight > MaximumLightBoardDecodeSize)
            {
                pictureBitmap.DecodePixelHeight = MaximumLightBoardDecodeSize;
            }
            pictureBitmap.EndInit();
            // Frozen: faster to draw and safe to keep in the cache.
            pictureBitmap.Freeze();

            // Photos from phones and cameras are often stored sideways with a note saying how to turn them;
            // turn them the right way up here, so the user does not have to (and the proportions are right).
            BitmapSource uprightBitmap = pictureBitmap;
            if (cameraRotationAngle != 0)
            {
                uprightBitmap = new TransformedBitmap(pictureBitmap, new RotateTransform(cameraRotationAngle));
                uprightBitmap.Freeze();
                if (cameraRotationAngle is 90 or 270)
                {
                    (pixelWidth, pixelHeight) = (pixelHeight, pixelWidth);
                }
            }

            var loadedBitmap = new LoadedLightBoardBitmap(lastSavedTicks, uprightBitmap, pixelWidth, pixelHeight);
            lightBoardBitmapCache[imageFilePath] = loadedBitmap;
            return loadedBitmap;
        }
        catch (Exception)
        {
            // Not an image WPF can read (or the file is locked or gone): the caller draws an error card.
            return null;
        }
    }

    /// <summary>
    /// How far (0, 90, 180 or 270 degrees clockwise) a photo must be turned to stand upright, from the camera's
    /// orientation note in its EXIF data. 0 when there is no such note (e.g. PNG files) or it cannot be read.
    /// Mirrored orientations are rare and are shown as stored.
    /// </summary>
    private static double GetCameraRotationAngle(BitmapFrame imageFrame)
    {
        try
        {
            // EXIF tag 274 is "Orientation": 1 = upright, 3 = upside down, 6 = turn right, 8 = turn left.
            if (imageFrame.Metadata is BitmapMetadata imageMetadata && imageMetadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientationValue)
            {
                return orientationValue switch
                {
                    3 => 180,
                    6 => 90,
                    8 => 270,
                    _ => 0,
                };
            }
        }
        catch (Exception)
        {
            // Not a format with EXIF data (or unreadable metadata): show the picture as it is stored.
        }
        return 0;
    }

    /// <summary>
    /// Builds the card for one image: the picture in a thin white frame with a soft shadow, a rotate handle in the
    /// top-right corner, a scale handle in the bottom-right corner and a small badge when the image has a note.
    /// </summary>
    private Border CreateLightBoardImageCard(BookshelfEntry bookshelfEntry)
    {
        var loadedBitmap = LoadLightBoardBitmap(bookshelfEntry.FilePath);

        // The picture itself, or a short message if the file could not be read.
        // The flip is applied to the picture only, so mirroring does not move the frame, handles or badge.
        var flipTransform = new ScaleTransform(1, 1);
        UIElement pictureElement;
        double aspectRatio;
        if (loadedBitmap != null && loadedBitmap.PixelWidth > 0 && loadedBitmap.PixelHeight > 0)
        {
            aspectRatio = (double)loadedBitmap.PixelWidth / loadedBitmap.PixelHeight;
            pictureElement = new Image
            {
                Source = loadedBitmap.Bitmap,
                // The card always has the picture's proportions, so Fill never stretches it out of shape.
                Stretch = Stretch.Fill,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = flipTransform,
            };
            RenderOptions.SetBitmapScalingMode(pictureElement, BitmapScalingMode.HighQuality);
        }
        else
        {
            // A square card that says what went wrong; it can still be moved, trashed or opened in another program.
            aspectRatio = 1;
            pictureElement = new Border
            {
                Background = Brushes.WhiteSmoke,
                Child = new TextBlock
                {
                    Text = $"Could not show this image:\n{bookshelfEntry.DisplayName}",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brushes.DimGray,
                    Margin = new Thickness(8),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        // Round handles: rotate in the top-right corner, scale in the bottom-right corner. Hidden until the mouse is over the image.
        var rotateHandle = CreateLightBoardHandle("↻", "Drag to rotate the image (or hold Shift and turn the mouse wheel)",
            HorizontalAlignment.Right, VerticalAlignment.Top, Cursors.Hand);
        rotateHandle.MouseLeftButtonDown += LightBoardRotateHandle_MouseLeftButtonDown;
        var scaleHandle = CreateLightBoardHandle("⤡", "Drag to make the image bigger or smaller; it keeps its proportions (or hold Ctrl and turn the mouse wheel)",
            HorizontalAlignment.Right, VerticalAlignment.Bottom, Cursors.SizeNWSE);
        scaleHandle.MouseLeftButtonDown += LightBoardScaleHandle_MouseLeftButtonDown;

        // A small badge in the top-left corner that says "this image has a note" (the note itself is in the tooltip).
        var noteBadge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = CreateFrozenPostItBrush("#FFF68F"),
            BorderBrush = CreateFrozenPostItBrush("#B8A800"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "✎",
                FontSize = 12,
                Foreground = CreateFrozenPostItBrush("#5C5300"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var cardLayout = new Grid();
        cardLayout.Children.Add(pictureElement);
        cardLayout.Children.Add(noteBadge);
        cardLayout.Children.Add(rotateHandle);
        cardLayout.Children.Add(scaleHandle);

        var rotationTransform = new RotateTransform(0);
        var imageState = new LightBoardImageState
        {
            BookshelfEntry = bookshelfEntry,
            AspectRatio = aspectRatio,
            PixelWidth = loadedBitmap?.PixelWidth ?? 0,
            RotationTransform = rotationTransform,
            FlipTransform = flipTransform,
            NoteBadge = noteBadge,
            Handles = new List<FrameworkElement> { rotateHandle, scaleHandle },
        };

        var imageCard = new Border
        {
            Background = LightBoardFrameBrush,
            Padding = new Thickness(LightBoardFrameThickness),
            Child = cardLayout,
            Tag = imageState,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Direction = 290, Opacity = 0.3 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotationTransform,
            Cursor = Cursors.SizeAll,
        };
        rotateHandle.Tag = imageCard;
        scaleHandle.Tag = imageCard;

        // A default size until PlaceLightBoardImagesOnBoard applies the saved one.
        SetLightBoardImageWidth(imageCard, GetDefaultLightBoardImageWidth(aspectRatio));

        // Moving, scaling and rotating with the mouse (MainWindow.LightBoardLayout.cs).
        imageCard.MouseLeftButtonDown += LightBoardImageCard_MouseLeftButtonDown;
        imageCard.MouseLeftButtonUp += LightBoardImageCard_MouseLeftButtonUp;
        imageCard.MouseMove += LightBoardImageCard_MouseMove;
        imageCard.LostMouseCapture += LightBoardImageCard_LostMouseCapture;
        imageCard.PreviewMouseWheel += LightBoardImageCard_PreviewMouseWheel;
        // The handles only show while the mouse is over the image, so the board stays clean.
        imageCard.MouseEnter += (_, _) => imageState.Handles.ForEach(handle => handle.Visibility = Visibility.Visible);
        imageCard.MouseLeave += (_, _) => imageState.Handles.ForEach(handle => handle.Visibility = Visibility.Hidden);
        imageCard.ContextMenu = CreateLightBoardImageContextMenu(imageCard);
        // The tooltip shows the note (if any) and the file name; it stays up long enough to read a longer note.
        ToolTipService.SetShowDuration(imageCard, 60000);
        UpdateLightBoardImageNote(imageCard, note: null);
        return imageCard;
    }

    /// <summary>One round handle in a corner of an image card (rotate or scale).</summary>
    private static Border CreateLightBoardHandle(string symbolText, string toolTipText,
        HorizontalAlignment horizontalAlignment, VerticalAlignment verticalAlignment, Cursor handleCursor)
    {
        return new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(3),
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Background = Brushes.White,
            BorderBrush = LightBoardHandleBrush,
            BorderThickness = new Thickness(1.5),
            Cursor = handleCursor,
            ToolTip = toolTipText,
            Visibility = Visibility.Hidden,
            Child = new TextBlock
            {
                Text = symbolText,
                FontSize = 12,
                Foreground = LightBoardHandleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>The width that makes an image's longest side <see cref="DefaultLightBoardImageSize"/> long.</summary>
    private static double GetDefaultLightBoardImageWidth(double aspectRatio)
    {
        return aspectRatio >= 1 ? DefaultLightBoardImageSize : DefaultLightBoardImageSize * aspectRatio;
    }

    /// <summary>
    /// Sets the size of an image on the board from its width alone: the height always follows from the image's own
    /// proportions, so the picture can never be squashed or stretched. The size is kept within sensible limits.
    /// </summary>
    private static void SetLightBoardImageWidth(Border imageCard, double wantedImageWidth)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        double aspectRatio = imageState.AspectRatio;

        // The smallest and largest width that keep both sides within the limits.
        double smallestWidth = Math.Max(MinimumLightBoardImageSize, MinimumLightBoardImageSize * aspectRatio);
        double largestWidth = Math.Min(MaximumLightBoardImageSize, MaximumLightBoardImageSize * aspectRatio);
        double imageWidth = Math.Round(Math.Clamp(wantedImageWidth, smallestWidth, Math.Max(smallestWidth, largestWidth)), 1);

        imageState.ImageWidth = imageWidth;
        imageCard.Width = imageWidth + 2 * LightBoardFrameThickness;
        imageCard.Height = imageWidth / aspectRatio + 2 * LightBoardFrameThickness;
    }

    /// <summary>Shows or hides the note badge, and puts the note and the file name in the image's tooltip.</summary>
    private static void UpdateLightBoardImageNote(Border imageCard, string? note)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        imageState.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        imageState.NoteBadge.Visibility = imageState.Note != null ? Visibility.Visible : Visibility.Collapsed;

        string fileNameText = imageState.BookshelfEntry.DisplayName;
        imageCard.ToolTip = imageState.Note != null
            ? new TextBlock { Text = $"{imageState.Note}\n\n— {fileNameText}", TextWrapping = TextWrapping.Wrap, MaxWidth = 400 }
            : $"{fileNameText}\n(Double-click to write a note)";
    }

    // ------------------------------------------------------------------
    // The note on an image
    // ------------------------------------------------------------------

    /// <summary>Opens the note of an image to read and edit it. An emptied note is removed.</summary>
    private void EditLightBoardImageNote(Border imageCard)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        var notePrompt = new TextPromptWindow($"Note on {imageState.BookshelfEntry.DisplayName}",
            "Your note about this image. It is not drawn on the image: hover over the image or double-click it to see it.\n" +
            "Ctrl+Enter saves. Empty the box to remove the note.",
            imageState.Note ?? string.Empty, macroExpander: null, isMultiLine: true)
        {
            Owner = this,
        };
        if (notePrompt.ShowDialog() != true)
        {
            return;
        }

        UpdateLightBoardImageNote(imageCard, notePrompt.AnswerText);
        SaveLightBoardImagePlacements();
    }

    // ------------------------------------------------------------------
    // Right-click menu
    // ------------------------------------------------------------------

    /// <summary>Right-click menu of an image: note, rotate, flip, size, stacking, open and trash.</summary>
    private ContextMenu CreateLightBoardImageContextMenu(Border imageCard)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        var imageContextMenu = new ContextMenu();

        // Small helper: a menu item that runs an action and then saves the board.
        MenuItem AddMenuItem(ItemsControl parentMenu, string menuHeader, Action menuAction, string? inputGestureText = null)
        {
            var newMenuItem = new MenuItem { Header = menuHeader, InputGestureText = inputGestureText ?? string.Empty };
            newMenuItem.Click += (_, _) =>
            {
                menuAction();
                SaveLightBoardImagePlacements();
            };
            parentMenu.Items.Add(newMenuItem);
            return newMenuItem;
        }

        // The note (opens its own window, which saves).
        var noteMenuItem = new MenuItem { Header = "_Note...", InputGestureText = "Double-click" };
        noteMenuItem.Click += (_, _) => EditLightBoardImageNote(imageCard);
        imageContextMenu.Items.Add(noteMenuItem);
        imageContextMenu.Items.Add(new Separator());

        // Rotating.
        AddMenuItem(imageContextMenu, "Rotate 90° _right", () => RotateLightBoardImage(imageCard, imageState.RotationTransform.Angle + 90));
        AddMenuItem(imageContextMenu, "Rotate 90° _left", () => RotateLightBoardImage(imageCard, imageState.RotationTransform.Angle - 90));
        AddMenuItem(imageContextMenu, "_Straighten", () => RotateLightBoardImage(imageCard, 0));
        imageContextMenu.Items.Add(new Separator());

        // Mirroring (ticked while the image is flipped that way).
        var flipHorizontalMenuItem = AddMenuItem(imageContextMenu, "Flip _horizontally (mirror left-right)",
            () => imageState.FlipTransform.ScaleX = -imageState.FlipTransform.ScaleX);
        var flipVerticalMenuItem = AddMenuItem(imageContextMenu, "Flip _vertically (mirror top-bottom)",
            () => imageState.FlipTransform.ScaleY = -imageState.FlipTransform.ScaleY);
        imageContextMenu.Opened += (_, _) =>
        {
            flipHorizontalMenuItem.IsChecked = imageState.FlipTransform.ScaleX < 0;
            flipVerticalMenuItem.IsChecked = imageState.FlipTransform.ScaleY < 0;
        };
        imageContextMenu.Items.Add(new Separator());

        // Size (always keeping the proportions), around the image's centre.
        var sizeMenu = new MenuItem { Header = "Si_ze" };
        AddMenuItem(sizeMenu, "_Bigger", () => ScaleLightBoardImageAroundCentre(imageCard, imageState.ImageWidth * LightBoardScaleStepFactor), "Ctrl+Wheel");
        AddMenuItem(sizeMenu, "_Smaller", () => ScaleLightBoardImageAroundCentre(imageCard, imageState.ImageWidth / LightBoardScaleStepFactor), "Ctrl+Wheel");
        AddMenuItem(sizeMenu, "_Default size", () => ScaleLightBoardImageAroundCentre(imageCard, GetDefaultLightBoardImageWidth(imageState.AspectRatio)));
        var actualSizeMenuItem = AddMenuItem(sizeMenu, "_Actual size (one image pixel per screen pixel)",
            () => ScaleLightBoardImageAroundCentre(imageCard, imageState.PixelWidth));
        actualSizeMenuItem.IsEnabled = imageState.PixelWidth > 0;
        imageContextMenu.Items.Add(sizeMenu);

        // Stacking.
        AddMenuItem(imageContextMenu, "Bring to _front", () => BringLightBoardImageToFront(imageCard));
        AddMenuItem(imageContextMenu, "Send to _back", () => SendLightBoardImageToBack(imageCard));
        imageContextMenu.Items.Add(new Separator());

        // The file.
        var openMenuItem = new MenuItem { Header = "_Open in the default image program" };
        openMenuItem.Click += (_, _) => OpenWithDefaultProgram(imageState.BookshelfEntry.FilePath);
        var showInFolderMenuItem = new MenuItem { Header = "Show in _folder" };
        showInFolderMenuItem.Click += (_, _) => ShowFileInExplorer(imageState.BookshelfEntry.FilePath);
        var trashMenuItem = new MenuItem { Header = "Move to _trash", ToolTip = "Move the image file to the Trash; it can be restored from the Trash bookshelf" };
        trashMenuItem.Click += (_, _) => MoveFilesToTrash(new[] { imageState.BookshelfEntry.FilePath });
        imageContextMenu.Items.Add(openMenuItem);
        imageContextMenu.Items.Add(showInFolderMenuItem);
        imageContextMenu.Items.Add(trashMenuItem);
        imageContextMenu.Items.Add(new Separator());

        // The whole board.
        var lineUpMenuItem = new MenuItem
        {
            Header = "_Line up all images in rows",
            ToolTip = "Put all images back in rows; their size, rotation, flips and notes are kept",
        };
        lineUpMenuItem.Click += (_, _) => LineUpLightBoardImages();
        imageContextMenu.Items.Add(lineUpMenuItem);
        return imageContextMenu;
    }

    // ------------------------------------------------------------------
    // Importing images
    // ------------------------------------------------------------------

    /// <summary>Import images... (button, Add... on the shelf, or Ctrl+N): picks image files and copies them onto the board.</summary>
    private void ImportImagesToLightBoard()
    {
        if (!currentBookshelf.IsLightBoard)
        {
            return;
        }

        var openDialog = new OpenFileDialog
        {
            Title = $"Import images to the light board \"{currentBookshelf.Name}\"",
            Filter = LightBoardImageDialogFilter,
            Multiselect = true,
        };
        if (openDialog.ShowDialog(this) != true)
        {
            return;
        }

        CopyImagesToLightBoard(openDialog.FileNames, dropPointOnBoard: null);
    }

    /// <summary>
    /// Copies image files into the board's folder (a file already in that folder is simply used as is) and shows the
    /// board with them. Files that are not images are skipped. With a drop point the new images are laid down there,
    /// slightly fanned out; otherwise they go into the first free spots.
    /// </summary>
    private void CopyImagesToLightBoard(IEnumerable<string> sourceFilePaths, Point? dropPointOnBoard)
    {
        string boardFolderPath = currentBookshelf.FolderPath!;
        if (!Directory.Exists(boardFolderPath))
        {
            MessageBox.Show(this,
                $"The folder of this light board was not found, so images cannot be copied there:\n{boardFolderPath}",
                "Markdown Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var problemLines = new List<string>();
        var importedDisplayNames = new List<string>();
        string fullBoardFolderPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(boardFolderPath));
        foreach (string sourceFilePath in sourceFilePaths)
        {
            if (!NamedBookshelf.HasLightBoardImageExtension(sourceFilePath))
            {
                problemLines.Add($"{Path.GetFileName(sourceFilePath)}: not an image the light board can show");
                continue;
            }

            try
            {
                string fullSourcePath = Path.GetFullPath(sourceFilePath);
                string targetFilePath;
                if (currentBookshelf.ContainsFile(fullSourcePath))
                {
                    // Already on this board (in its folder): no copy needed.
                    targetFilePath = fullSourcePath;
                }
                else
                {
                    // Never overwrite: "photo.jpg" becomes "photo (2).jpg" if the name is taken.
                    targetFilePath = GetUnusedFilePath(fullBoardFolderPath,
                        Path.GetFileNameWithoutExtension(fullSourcePath), Path.GetExtension(fullSourcePath));
                    File.Copy(fullSourcePath, targetFilePath);
                }
                importedDisplayNames.Add(Path.GetRelativePath(fullBoardFolderPath, targetFilePath));
            }
            catch (Exception copyError)
            {
                problemLines.Add($"{Path.GetFileName(sourceFilePath)}: {copyError.Message}");
            }
        }

        // Dropped images are laid down where they were dropped, each a little lower and to the right of the one before,
        // on top of everything else. (Their size is worked out when they are drawn: Width 0 means the default size.)
        if (dropPointOnBoard is Point dropPoint && importedDisplayNames.Count > 0)
        {
            SaveLightBoardImagePlacements();
            currentBookshelf.LightBoardImages ??= new Dictionary<string, LightBoardImagePlacement>(StringComparer.OrdinalIgnoreCase);
            int highestStackLevel = currentBookshelf.LightBoardImages.Values.Select(placement => placement.StackLevel).DefaultIfEmpty(0).Max();
            const double fanOutStep = 24;
            for (int importedIndex = 0; importedIndex < importedDisplayNames.Count; importedIndex++)
            {
                string importedDisplayName = importedDisplayNames[importedIndex];
                // An image that was already on the board keeps its size, rotation, flips and note; only its place changes.
                var importedPlacement = currentBookshelf.LightBoardImages.TryGetValue(importedDisplayName, out var existingPlacement)
                    ? existingPlacement
                    : new LightBoardImagePlacement();
                importedPlacement.X = Math.Max(0, Math.Round(dropPoint.X + importedIndex * fanOutStep, 1));
                importedPlacement.Y = Math.Max(0, Math.Round(dropPoint.Y + importedIndex * fanOutStep, 1));
                importedPlacement.StackLevel = highestStackLevel + importedIndex + 1;
                currentBookshelf.LightBoardImages[importedDisplayName] = importedPlacement;
            }
            SaveSettingsOrWarn();
        }

        // Put the new files on the shelf and the board at once (the folder watcher would do it too, a moment later).
        RefreshFolderShelfAfterChange();
        ShowLightBoard();
        ShowProblems("Some files could not be put on the light board:", problemLines);
    }

    /// <summary>
    /// Files dropped while a light board is shown: image files are copied onto the board (where they were dropped,
    /// if dropped on the board itself). Returns false if nothing dropped was an image, so the normal drop handling runs.
    /// </summary>
    private bool TryDropImagesOnLightBoard(string[] droppedPaths, DragEventArgs dropDetails)
    {
        if (!currentBookshelf.IsLightBoard || !droppedPaths.Any(NamedBookshelf.HasLightBoardImageExtension))
        {
            return false;
        }

        Point? dropPointOnBoard = null;
        if (isLightBoardShown)
        {
            Point pointOnBoardView = dropDetails.GetPosition(LightBoardScroller);
            bool isOverBoard = pointOnBoardView.X >= 0 && pointOnBoardView.Y >= 0
                && pointOnBoardView.X <= LightBoardScroller.ActualWidth && pointOnBoardView.Y <= LightBoardScroller.ActualHeight;
            if (isOverBoard)
            {
                dropPointOnBoard = dropDetails.GetPosition(LightBoardCanvas);
            }
        }

        CopyImagesToLightBoard(droppedPaths.Where(NamedBookshelf.HasLightBoardImageExtension), dropPointOnBoard);
        return true;
    }

    /// <summary>
    /// A file of a light board was clicked in the bookshelf list: shows the board (if a document was in front of it)
    /// and brings that image to the front, scrolled into view.
    /// </summary>
    private void ShowLightBoardImage(BookshelfEntry bookshelfEntry)
    {
        ShowLightBoard();
        var imageCard = GetLightBoardImageCards()
            .FirstOrDefault(card => ((LightBoardImageState)card.Tag).BookshelfEntry.HasPath(bookshelfEntry.FilePath));
        if (imageCard == null)
        {
            return;
        }

        BringLightBoardImageToFront(imageCard);
        imageCard.BringIntoView();
    }
}
