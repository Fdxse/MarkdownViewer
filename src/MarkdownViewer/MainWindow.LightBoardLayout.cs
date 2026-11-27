using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MarkdownViewer.Bookshelf;

namespace MarkdownViewer;

/// <summary>
/// Arranging the images on a light board with the mouse: drag an image to move it (it comes to the top of the
/// stack), drag the round handle in its top-right corner to rotate it, and the one in its bottom-right corner to
/// scale it. Scaling always keeps the image's proportions and happens around the image's centre. Ctrl + mouse
/// wheel scales and Shift + mouse wheel rotates. Everything is saved per board in the settings file
/// (<see cref="NamedBookshelf.LightBoardImages"/>) together with the flips and the notes.
/// </summary>
public partial class MainWindow
{
    /// <summary>Space between images when they are laid out in rows.</summary>
    private const double LightBoardGridGap = 20;

    /// <summary>How many degrees one step of Shift + mouse wheel rotates an image.</summary>
    private const double LightBoardRotateStep = 5;

    /// <summary>While rotating with the handle, an angle this close (in degrees) to straight or a quarter turn snaps to it.</summary>
    private const double LightBoardRotateSnapAngle = 2;

    /// <summary>One step of Ctrl + mouse wheel (or Size → Bigger) makes an image this many times wider (and as many times taller).</summary>
    private const double LightBoardScaleStepFactor = 1.1;

    /// <summary>What the mouse is doing with an image right now.</summary>
    private enum LightBoardMouseAction
    {
        /// <summary>Nothing: no button is held on an image.</summary>
        None,

        /// <summary>The left button is down on an image but the mouse has not moved far yet.</summary>
        Pressed,

        /// <summary>The image is being moved around the board.</summary>
        Moving,

        /// <summary>The image is being rotated with its handle.</summary>
        Rotating,

        /// <summary>The image is being scaled with its handle.</summary>
        Scaling,
    }

    /// <summary>True while an image is being moved, rotated or scaled. The board is not redrawn meanwhile.</summary>
    private bool isLightBoardImageBeingDragged;

    /// <summary>What the mouse is doing with <see cref="lightBoardCardUnderMouse"/>.</summary>
    private LightBoardMouseAction lightBoardMouseAction = LightBoardMouseAction.None;

    /// <summary>The image card the left mouse button is held down on, or null.</summary>
    private Border? lightBoardCardUnderMouse;

    /// <summary>Where the mouse button went down, relative to the board's canvas.</summary>
    private Point lightBoardPressPoint;

    /// <summary>Where the card's top-left corner was when the mouse button went down.</summary>
    private Point lightBoardCardStartPosition;

    /// <summary>When rotating: the direction (in degrees) from the card's centre to the mouse when rotating started.</summary>
    private double lightBoardRotateStartMouseAngle;

    /// <summary>When rotating: the card's angle when rotating started.</summary>
    private double lightBoardRotateStartCardAngle;

    /// <summary>When scaling: the distance from the card's centre to the mouse when scaling started.</summary>
    private double lightBoardScaleStartDistance;

    /// <summary>When scaling: the picture's width when scaling started.</summary>
    private double lightBoardScaleStartWidth;

    /// <summary>Saves the board a moment after the last mouse wheel step, so a quick spin does not write the settings file every step.</summary>
    private DispatcherTimer? lightBoardSaveTimer;

    // ------------------------------------------------------------------
    // Placing the images when the board is drawn
    // ------------------------------------------------------------------

    /// <summary>
    /// Gives freshly drawn cards their saved place, size, rotation, flips, stacking and note. Images without a saved
    /// place go into rows, in the first spots no placed image covers, on top of the stack.
    /// </summary>
    private void PlaceLightBoardImagesOnBoard(IReadOnlyList<Border> drawnCards)
    {
        // A copy that ignores upper/lower case in file names (the one read from the settings file does not).
        var savedPlacementByName = new Dictionary<string, LightBoardImagePlacement>(StringComparer.OrdinalIgnoreCase);
        foreach (var savedPlacement in currentBookshelf.LightBoardImages ?? new Dictionary<string, LightBoardImagePlacement>())
        {
            savedPlacementByName.TryAdd(savedPlacement.Key, savedPlacement.Value);
        }

        var coveredAreas = new List<Rect>();
        var cardsWithoutPlace = new List<Border>();
        int highestStackLevel = 0;
        foreach (var drawnCard in drawnCards)
        {
            var imageState = (LightBoardImageState)drawnCard.Tag;
            if (!savedPlacementByName.TryGetValue(imageState.BookshelfEntry.DisplayName, out var imagePlacement))
            {
                cardsWithoutPlace.Add(drawnCard);
                continue;
            }

            // Width 0 (an image dropped on the board but not sized yet) means the default size.
            SetLightBoardImageWidth(drawnCard, imagePlacement.Width > 0 ? imagePlacement.Width : GetDefaultLightBoardImageWidth(imageState.AspectRatio));
            SetPostItNotePosition(drawnCard, new Point(imagePlacement.X, imagePlacement.Y));
            imageState.RotationTransform.Angle = imagePlacement.Angle;
            imageState.FlipTransform.ScaleX = imagePlacement.FlipHorizontal ? -1 : 1;
            imageState.FlipTransform.ScaleY = imagePlacement.FlipVertical ? -1 : 1;
            UpdateLightBoardImageNote(drawnCard, imagePlacement.Note);
            Panel.SetZIndex(drawnCard, imagePlacement.StackLevel);
            coveredAreas.Add(new Rect(imagePlacement.X, imagePlacement.Y, drawnCard.Width, drawnCard.Height));
            highestStackLevel = Math.Max(highestStackLevel, imagePlacement.StackLevel);
        }

        // Rows of square slots big enough for an image at its default size; as many columns as fit the board's width.
        double slotSize = DefaultLightBoardImageSize + 2 * LightBoardFrameThickness + LightBoardGridGap;
        double availableWidth = LightBoardScroller.ActualWidth - 32;
        int columnCount = availableWidth > 0 ? Math.Max(1, (int)(availableWidth / slotSize)) : FallbackPostItColumnCount;

        int slotIndex = 0;
        foreach (var cardWithoutPlace in cardsWithoutPlace)
        {
            // Walk through the slots until one is not covered by a placed image.
            Rect freeSlotArea;
            do
            {
                int rowIndex = slotIndex / columnCount;
                int columnIndex = slotIndex % columnCount;
                freeSlotArea = new Rect(columnIndex * slotSize, rowIndex * slotSize, cardWithoutPlace.Width, cardWithoutPlace.Height);
                slotIndex++;
            }
            while (coveredAreas.Any(coveredArea => coveredArea.IntersectsWith(freeSlotArea)));

            SetPostItNotePosition(cardWithoutPlace, freeSlotArea.TopLeft);
            // New images lie on top, so they are never hidden under a stack.
            highestStackLevel++;
            Panel.SetZIndex(cardWithoutPlace, highestStackLevel);
        }

        UpdateLightBoardCanvasSize();
    }

    /// <summary>All image cards on the board.</summary>
    private IEnumerable<Border> GetLightBoardImageCards() => LightBoardCanvas.Children.OfType<Border>();

    /// <summary>The centre of a card on the board (it is rotated around this point).</summary>
    private static Point GetLightBoardCardCentre(Border imageCard)
    {
        Point cardPosition = GetPostItNotePosition(imageCard);
        return new Point(cardPosition.X + imageCard.Width / 2, cardPosition.Y + imageCard.Height / 2);
    }

    /// <summary>
    /// The width and height of the area a card covers once it is rotated (a rotated rectangle takes up more room
    /// than the rectangle itself).
    /// </summary>
    private static Size GetRotatedLightBoardCardSize(Border imageCard)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        double angleInRadians = imageState.RotationTransform.Angle * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(angleInRadians));
        double sine = Math.Abs(Math.Sin(angleInRadians));
        return new Size(imageCard.Width * cosine + imageCard.Height * sine, imageCard.Width * sine + imageCard.Height * cosine);
    }

    /// <summary>Makes the canvas just big enough for all images (rotated ones included), so the board scrolls to the furthest one.</summary>
    private void UpdateLightBoardCanvasSize()
    {
        double furthestRightEdge = 0;
        double furthestBottomEdge = 0;
        foreach (var imageCard in GetLightBoardImageCards())
        {
            Point cardCentre = GetLightBoardCardCentre(imageCard);
            Size rotatedSize = GetRotatedLightBoardCardSize(imageCard);
            furthestRightEdge = Math.Max(furthestRightEdge, cardCentre.X + rotatedSize.Width / 2);
            furthestBottomEdge = Math.Max(furthestBottomEdge, cardCentre.Y + rotatedSize.Height / 2);
        }

        // A little extra for the shadows.
        LightBoardCanvas.Width = furthestRightEdge + LightBoardGridGap;
        LightBoardCanvas.Height = furthestBottomEdge + LightBoardGridGap;
    }

    // ------------------------------------------------------------------
    // Mouse: moving, rotating and scaling
    // ------------------------------------------------------------------

    /// <summary>
    /// The left button went down on an image: a double-click opens its note; otherwise it may become a move if the
    /// mouse moves far enough with the button held down.
    /// </summary>
    private void LightBoardImageCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (sender is not Border { Tag: LightBoardImageState } pressedCard)
        {
            return;
        }

        mouseDetails.Handled = true;
        if (mouseDetails.ClickCount == 2)
        {
            EndLightBoardImagePress();
            EditLightBoardImageNote(pressedCard);
            return;
        }

        lightBoardCardUnderMouse = pressedCard;
        lightBoardMouseAction = LightBoardMouseAction.Pressed;
        lightBoardPressPoint = mouseDetails.GetPosition(LightBoardCanvas);
        lightBoardCardStartPosition = GetPostItNotePosition(pressedCard);
        // Keep getting the mouse even when it leaves the image (or the window) while the button is down.
        pressedCard.CaptureMouse();
    }

    /// <summary>The rotate handle was pressed: dragging it rotates the image around its centre.</summary>
    private void LightBoardRotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (sender is not FrameworkElement { Tag: Border rotatedCard } || rotatedCard.Tag is not LightBoardImageState imageState)
        {
            return;
        }

        // Handled, so the card itself does not also start a move.
        mouseDetails.Handled = true;
        lightBoardCardUnderMouse = rotatedCard;
        lightBoardMouseAction = LightBoardMouseAction.Rotating;
        isLightBoardImageBeingDragged = true;
        lightBoardRotateStartMouseAngle = GetAngleFromLightBoardCardCentre(rotatedCard, mouseDetails.GetPosition(LightBoardCanvas));
        lightBoardRotateStartCardAngle = imageState.RotationTransform.Angle;
        BringLightBoardImageToFront(rotatedCard);
        rotatedCard.CaptureMouse();
    }

    /// <summary>The scale handle was pressed: dragging it away from the centre makes the image bigger, towards it smaller.</summary>
    private void LightBoardScaleHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (sender is not FrameworkElement { Tag: Border scaledCard } || scaledCard.Tag is not LightBoardImageState imageState)
        {
            return;
        }

        mouseDetails.Handled = true;
        lightBoardCardUnderMouse = scaledCard;
        lightBoardMouseAction = LightBoardMouseAction.Scaling;
        isLightBoardImageBeingDragged = true;
        Point cardCentre = GetLightBoardCardCentre(scaledCard);
        Point mouseOnCanvas = mouseDetails.GetPosition(LightBoardCanvas);
        // At least one pixel, so the scale factor below can never divide by zero.
        lightBoardScaleStartDistance = Math.Max(1, (mouseOnCanvas - cardCentre).Length);
        lightBoardScaleStartWidth = imageState.ImageWidth;
        BringLightBoardImageToFront(scaledCard);
        scaledCard.CaptureMouse();
    }

    /// <summary>Moves, rotates or scales the image under the mouse while the left button is held down.</summary>
    private void LightBoardImageCard_MouseMove(object sender, MouseEventArgs mouseDetails)
    {
        if (!ReferenceEquals(sender, lightBoardCardUnderMouse) || lightBoardCardUnderMouse.Tag is not LightBoardImageState imageState)
        {
            return;
        }

        Point mouseOnCanvas = mouseDetails.GetPosition(LightBoardCanvas);
        switch (lightBoardMouseAction)
        {
            case LightBoardMouseAction.Pressed:
                // Windows' own drag threshold, so a slightly shaky click (or double-click) does not move the image.
                bool movedFarEnough =
                    Math.Abs(mouseOnCanvas.X - lightBoardPressPoint.X) >= SystemParameters.MinimumHorizontalDragDistance
                    || Math.Abs(mouseOnCanvas.Y - lightBoardPressPoint.Y) >= SystemParameters.MinimumVerticalDragDistance;
                if (!movedFarEnough)
                {
                    return;
                }

                // Picked up: the image goes on top of the stack.
                lightBoardMouseAction = LightBoardMouseAction.Moving;
                isLightBoardImageBeingDragged = true;
                BringLightBoardImageToFront(lightBoardCardUnderMouse);
                MoveLightBoardImageWithMouse(lightBoardCardUnderMouse, mouseOnCanvas);
                break;

            case LightBoardMouseAction.Moving:
                MoveLightBoardImageWithMouse(lightBoardCardUnderMouse, mouseOnCanvas);
                break;

            case LightBoardMouseAction.Rotating:
                // The image turns as far as the mouse has turned around its centre since rotating started.
                double mouseAngle = GetAngleFromLightBoardCardCentre(lightBoardCardUnderMouse, mouseOnCanvas);
                double newAngle = NormalizeTiltAngle(lightBoardRotateStartCardAngle + (mouseAngle - lightBoardRotateStartMouseAngle));
                imageState.RotationTransform.Angle = SnapLightBoardAngle(newAngle);
                UpdateLightBoardCanvasSize();
                break;

            case LightBoardMouseAction.Scaling:
                // The image grows or shrinks by as much as the mouse moved away from or towards its centre.
                double mouseDistance = (mouseOnCanvas - GetLightBoardCardCentre(lightBoardCardUnderMouse)).Length;
                ScaleLightBoardImageAroundCentre(lightBoardCardUnderMouse, lightBoardScaleStartWidth * mouseDistance / lightBoardScaleStartDistance);
                ScrollLightBoardNearEdge(Mouse.GetPosition(LightBoardScroller));
                break;
        }
    }

    /// <summary>Puts the image where the mouse has dragged it (never past the board's top or left edge).</summary>
    private void MoveLightBoardImageWithMouse(Border movedCard, Point mouseOnCanvas)
    {
        double newLeftEdge = Math.Max(0, lightBoardCardStartPosition.X + (mouseOnCanvas.X - lightBoardPressPoint.X));
        double newTopEdge = Math.Max(0, lightBoardCardStartPosition.Y + (mouseOnCanvas.Y - lightBoardPressPoint.Y));
        SetPostItNotePosition(movedCard, new Point(newLeftEdge, newTopEdge));
        // The board grows when an image is pulled past its right or bottom edge.
        UpdateLightBoardCanvasSize();
        ScrollLightBoardNearEdge(Mouse.GetPosition(LightBoardScroller));
    }

    /// <summary>A mouse release on an image ends a move, rotation or scaling (and saves it).</summary>
    private void LightBoardImageCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs mouseDetails)
    {
        if (ReferenceEquals(sender, lightBoardCardUnderMouse))
        {
            mouseDetails.Handled = true;
            EndLightBoardImagePress();
        }
    }

    /// <summary>The image lost the mouse while it was being changed (e.g. another window popped up): keep it as it is now and save that.</summary>
    private void LightBoardImageCard_LostMouseCapture(object sender, MouseEventArgs mouseDetails)
    {
        if (ReferenceEquals(sender, lightBoardCardUnderMouse))
        {
            EndLightBoardImagePress();
        }
    }

    /// <summary>Ends a press on an image. A move, rotation or scaling is saved; a plain click changes nothing.</summary>
    private void EndLightBoardImagePress()
    {
        var finishedAction = lightBoardMouseAction;
        var releasedCard = lightBoardCardUnderMouse;

        // Reset first: letting go of the mouse capture below raises LostMouseCapture, which must see nothing in progress.
        lightBoardMouseAction = LightBoardMouseAction.None;
        lightBoardCardUnderMouse = null;
        isLightBoardImageBeingDragged = false;
        releasedCard?.ReleaseMouseCapture();

        if (finishedAction is LightBoardMouseAction.Moving or LightBoardMouseAction.Rotating or LightBoardMouseAction.Scaling)
        {
            SaveLightBoardImagePlacements();
            // Catch up on any images that were added, removed or saved meanwhile.
            RefreshLightBoardIfChanged();
        }
    }

    /// <summary>Ctrl + mouse wheel over an image scales it, Shift + mouse wheel rotates it; without them the wheel scrolls the board.</summary>
    private void LightBoardImageCard_PreviewMouseWheel(object sender, MouseWheelEventArgs wheelDetails)
    {
        if (sender is not Border { Tag: LightBoardImageState imageState } imageCard)
        {
            return;
        }

        // Wheel away from you: bigger, or turned to the right (clockwise). Towards you: smaller, or to the left.
        bool isWheelTurnedAway = wheelDetails.Delta > 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            wheelDetails.Handled = true;
            double scaleFactor = isWheelTurnedAway ? LightBoardScaleStepFactor : 1 / LightBoardScaleStepFactor;
            ScaleLightBoardImageAroundCentre(imageCard, imageState.ImageWidth * scaleFactor);
            SaveLightBoardImagePlacementsSoon();
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            wheelDetails.Handled = true;
            double angleChange = isWheelTurnedAway ? LightBoardRotateStep : -LightBoardRotateStep;
            RotateLightBoardImage(imageCard, imageState.RotationTransform.Angle + angleChange);
            SaveLightBoardImagePlacementsSoon();
        }
    }

    /// <summary>The direction (in degrees) from the centre of a card to a point on the board.</summary>
    private static double GetAngleFromLightBoardCardCentre(Border imageCard, Point pointOnCanvas)
    {
        Point cardCentre = GetLightBoardCardCentre(imageCard);
        return Math.Atan2(pointOnCanvas.Y - cardCentre.Y, pointOnCanvas.X - cardCentre.X) * 180 / Math.PI;
    }

    /// <summary>An angle close to straight or a quarter/half turn snaps to exactly that, so images are easy to line up again.</summary>
    private static double SnapLightBoardAngle(double rotationAngle)
    {
        double nearestQuarterTurn = Math.Round(rotationAngle / 90) * 90;
        return Math.Abs(rotationAngle - nearestQuarterTurn) < LightBoardRotateSnapAngle
            ? NormalizeTiltAngle(nearestQuarterTurn)
            : rotationAngle;
    }

    /// <summary>Scrolls the board a little when the mouse is held near its edge while changing an image.</summary>
    private void ScrollLightBoardNearEdge(Point pointOnScroller)
    {
        if (pointOnScroller.Y < DragAutoScrollMargin)
        {
            LightBoardScroller.ScrollToVerticalOffset(LightBoardScroller.VerticalOffset - PostItBoardAutoScrollStep);
        }
        else if (pointOnScroller.Y > LightBoardScroller.ViewportHeight - DragAutoScrollMargin)
        {
            LightBoardScroller.ScrollToVerticalOffset(LightBoardScroller.VerticalOffset + PostItBoardAutoScrollStep);
        }

        if (pointOnScroller.X < DragAutoScrollMargin)
        {
            LightBoardScroller.ScrollToHorizontalOffset(LightBoardScroller.HorizontalOffset - PostItBoardAutoScrollStep);
        }
        else if (pointOnScroller.X > LightBoardScroller.ViewportWidth - DragAutoScrollMargin)
        {
            LightBoardScroller.ScrollToHorizontalOffset(LightBoardScroller.HorizontalOffset + PostItBoardAutoScrollStep);
        }
    }

    // ------------------------------------------------------------------
    // Changing one image (used by the mouse, the wheel and the right-click menu)
    // ------------------------------------------------------------------

    /// <summary>Rotates an image to the given angle (kept between -180 and +180 degrees).</summary>
    private void RotateLightBoardImage(Border imageCard, double newAngle)
    {
        var imageState = (LightBoardImageState)imageCard.Tag;
        imageState.RotationTransform.Angle = NormalizeTiltAngle(newAngle);
        UpdateLightBoardCanvasSize();
    }

    /// <summary>
    /// Gives an image a new width, with the height following its proportions, while its centre stays where it was
    /// (as far as the board's top and left edges allow).
    /// </summary>
    private void ScaleLightBoardImageAroundCentre(Border imageCard, double newImageWidth)
    {
        Point centreBefore = GetLightBoardCardCentre(imageCard);
        SetLightBoardImageWidth(imageCard, newImageWidth);
        double newLeftEdge = Math.Max(0, centreBefore.X - imageCard.Width / 2);
        double newTopEdge = Math.Max(0, centreBefore.Y - imageCard.Height / 2);
        SetPostItNotePosition(imageCard, new Point(newLeftEdge, newTopEdge));
        UpdateLightBoardCanvasSize();
    }

    /// <summary>Puts an image on top of all other images.</summary>
    private void BringLightBoardImageToFront(Border imageCard)
    {
        int highestOtherStackLevel = GetLightBoardImageCards()
            .Where(otherCard => !ReferenceEquals(otherCard, imageCard))
            .Select(Panel.GetZIndex)
            .DefaultIfEmpty(0)
            .Max();
        if (Panel.GetZIndex(imageCard) <= highestOtherStackLevel)
        {
            Panel.SetZIndex(imageCard, highestOtherStackLevel + 1);
        }
    }

    /// <summary>Puts an image underneath all other images.</summary>
    private void SendLightBoardImageToBack(Border imageCard)
    {
        int lowestOtherStackLevel = GetLightBoardImageCards()
            .Where(otherCard => !ReferenceEquals(otherCard, imageCard))
            .Select(Panel.GetZIndex)
            .DefaultIfEmpty(0)
            .Min();
        if (Panel.GetZIndex(imageCard) >= lowestOtherStackLevel)
        {
            Panel.SetZIndex(imageCard, lowestOtherStackLevel - 1);
        }
    }

    // ------------------------------------------------------------------
    // Saving and lining up
    // ------------------------------------------------------------------

    /// <summary>
    /// Saves how every image on the board lies (place, size, rotation, flips, stacking) and its note.
    /// Once anything has been saved, all images keep their places, so new images never push existing ones around.
    /// </summary>
    private void SaveLightBoardImagePlacements()
    {
        lightBoardSaveTimer?.Stop();
        var cardsOnBoard = GetLightBoardImageCards().ToList();
        if (cardsOnBoard.Count == 0 || !currentBookshelf.IsLightBoard)
        {
            return;
        }

        var newPlacements = new Dictionary<string, LightBoardImagePlacement>(StringComparer.OrdinalIgnoreCase);

        // Stack levels are renumbered 1, 2, 3... from the bottom up, so they stay small however often images are moved.
        var cardsBottomToTop = cardsOnBoard
            .Select((imageCard, drawOrder) => (imageCard, drawOrder))
            .OrderBy(cardAndOrder => Panel.GetZIndex(cardAndOrder.imageCard))
            .ThenBy(cardAndOrder => cardAndOrder.drawOrder)
            .Select(cardAndOrder => cardAndOrder.imageCard);
        int stackLevel = 1;
        foreach (var imageCard in cardsBottomToTop)
        {
            var imageState = (LightBoardImageState)imageCard.Tag;
            Point cardPosition = GetPostItNotePosition(imageCard);
            newPlacements[imageState.BookshelfEntry.DisplayName] = new LightBoardImagePlacement
            {
                X = Math.Round(cardPosition.X, 1),
                Y = Math.Round(cardPosition.Y, 1),
                Width = imageState.ImageWidth,
                Angle = imageState.RotationTransform.Angle,
                FlipHorizontal = imageState.FlipTransform.ScaleX < 0,
                FlipVertical = imageState.FlipTransform.ScaleY < 0,
                StackLevel = stackLevel,
                Note = imageState.Note,
            };
            Panel.SetZIndex(imageCard, stackLevel);
            stackLevel++;
        }

        // Keep what was saved for images that are in the folder but not drawn (beyond the most images shown).
        // Images that are gone (deleted, renamed, moved away) are dropped, notes included.
        if (currentBookshelf.LightBoardImages != null)
        {
            var namesOnShelf = new HashSet<string>(currentBookshelf.Entries.Select(bookshelfEntry => bookshelfEntry.DisplayName), StringComparer.OrdinalIgnoreCase);
            foreach (var oldPlacement in currentBookshelf.LightBoardImages)
            {
                if (namesOnShelf.Contains(oldPlacement.Key))
                {
                    newPlacements.TryAdd(oldPlacement.Key, oldPlacement.Value);
                }
            }
        }

        currentBookshelf.LightBoardImages = newPlacements;
        SaveSettingsOrWarn();
    }

    /// <summary>Saves the board shortly, after a series of quick mouse wheel steps has stopped.</summary>
    private void SaveLightBoardImagePlacementsSoon()
    {
        if (lightBoardSaveTimer == null)
        {
            lightBoardSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            lightBoardSaveTimer.Tick += (_, _) => SaveLightBoardImagePlacements();
        }
        lightBoardSaveTimer.Stop();
        lightBoardSaveTimer.Start();
    }

    /// <summary>
    /// Asks, then puts all images back in rows as wide as the board, in their current reading order (top-left first).
    /// Size, rotation, flips and notes are kept; only the places (and the stacking) change.
    /// </summary>
    private void LineUpLightBoardImages()
    {
        var lineUpAnswer = MessageBox.Show(this,
            $"Put all images on \"{currentBookshelf.Name}\" in rows?\n\n" +
            "They keep their size, rotation, flips and notes; only where they lie (and how they are stacked) changes.",
            "Markdown Viewer", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (lineUpAnswer != MessageBoxResult.OK)
        {
            return;
        }

        // Reading order: by the centre's row first (rows of default-image height), then left to right.
        double readingRowHeight = DefaultLightBoardImageSize + LightBoardGridGap;
        var cardsInReadingOrder = GetLightBoardImageCards()
            .OrderBy(imageCard => Math.Floor(GetLightBoardCardCentre(imageCard).Y / readingRowHeight))
            .ThenBy(imageCard => GetLightBoardCardCentre(imageCard).X)
            .ToList();

        // Flow the images into rows: left to right until the board's width is used, then a new row below the tallest one.
        double availableWidth = Math.Max(DefaultLightBoardImageSize, LightBoardScroller.ActualWidth - 32);
        double rowLeft = 0;
        double rowTop = 0;
        double rowHeight = 0;
        int stackLevel = 1;
        foreach (var imageCard in cardsInReadingOrder)
        {
            Size rotatedSize = GetRotatedLightBoardCardSize(imageCard);
            if (rowLeft > 0 && rowLeft + rotatedSize.Width > availableWidth)
            {
                rowLeft = 0;
                rowTop += rowHeight + LightBoardGridGap;
                rowHeight = 0;
            }

            // The card is placed so that the area it covers once rotated starts at (rowLeft, rowTop).
            double cardLeft = rowLeft + (rotatedSize.Width - imageCard.Width) / 2;
            double cardTop = rowTop + (rotatedSize.Height - imageCard.Height) / 2;
            SetPostItNotePosition(imageCard, new Point(cardLeft, cardTop));
            Panel.SetZIndex(imageCard, stackLevel++);

            rowLeft += rotatedSize.Width + LightBoardGridGap;
            rowHeight = Math.Max(rowHeight, rotatedSize.Height);
        }

        UpdateLightBoardCanvasSize();
        SaveLightBoardImagePlacements();
    }
}
