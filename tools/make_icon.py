"""
Generates src/MarkdownViewer/Assets/AppIcon.ico for Markdown Viewer.

Design: a white document page with a folded top-right corner and the
Markdown "M + down arrow" mark in blue. Every icon size is drawn at a high
resolution and then scaled down, so edges stay smooth. The smallest sizes
(16 and 24 px) use a simplified, bolder variant so they stay readable.

Run from the repository root:  python3 tools/make_icon.py
Needs Pillow (a build-time tool only; the app itself does not use it).
"""
from pathlib import Path
from PIL import Image, ImageDraw

# Colours used in the icon (RGBA)
PAGE_FILL = (255, 255, 255, 255)
PAGE_OUTLINE = (96, 112, 136, 255)
FOLD_FILL = (214, 222, 235, 255)
MARK_BLUE = (37, 99, 235, 255)

# Every shape is described on a 256 x 256 design grid and scaled when drawn
DESIGN_GRID = 256
# Each size is rendered this many times larger, then downsampled (anti-aliasing)
SUPERSAMPLE_FACTOR = 8
# The sizes Windows picks from (taskbar, title bar, Explorer views, ...)
ICON_SIZES = [16, 24, 32, 48, 64, 128, 256]


def draw_icon(pixel_size: int) -> Image.Image:
    """Draws the icon at the requested size and returns it as an RGBA image."""
    canvas_size = pixel_size * SUPERSAMPLE_FACTOR
    scale = canvas_size / DESIGN_GRID
    is_small_size = pixel_size <= 24

    def to_canvas(points):
        """Converts design-grid coordinates into canvas coordinates."""
        return [(x_coord * scale, y_coord * scale) for x_coord, y_coord in points]

    image = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    painter = ImageDraw.Draw(image)

    # --- The document page -------------------------------------------------
    # Small sizes use the full width and a smaller fold so the mark gets more room
    page_left, page_top, page_right, page_bottom = (20, 8, 236, 248) if is_small_size else (36, 12, 220, 244)
    fold_size = 40 if is_small_size else 56
    page_outline_points = [
        (page_left, page_top),
        (page_right - fold_size, page_top),
        (page_right, page_top + fold_size),
        (page_right, page_bottom),
        (page_left, page_bottom),
    ]
    outline_width = max(1, round((14 if is_small_size else 8) * scale))
    painter.polygon(to_canvas(page_outline_points), fill=PAGE_FILL)
    painter.line(to_canvas(page_outline_points + [page_outline_points[0]]),
                 fill=PAGE_OUTLINE, width=outline_width, joint="curve")

    # Folded corner: a small shaded triangle
    fold_points = [
        (page_right - fold_size, page_top),
        (page_right - fold_size, page_top + fold_size),
        (page_right, page_top + fold_size),
    ]
    painter.polygon(to_canvas(fold_points), fill=FOLD_FILL)
    painter.line(to_canvas(fold_points + [fold_points[0]]),
                 fill=PAGE_OUTLINE, width=outline_width, joint="curve")

    # --- The Markdown mark: "M" on the left, down arrow on the right ---------
    if is_small_size:
        mark_top, mark_bottom = 92, 212
        stroke = 34                      # thickness of the M's strokes
        letter_left, letter_right = 40, 150
        arrow_center, arrow_shaft_half, arrow_head_half = 192, 17, 40
    else:
        mark_top, mark_bottom = 104, 204
        stroke = 22
        letter_left, letter_right = 60, 148
        arrow_center, arrow_shaft_half, arrow_head_half = 182, 12, 28

    letter_middle = (letter_left + letter_right) / 2
    valley_depth = (mark_bottom - mark_top) * 0.55

    # The "M" is drawn as one polygon: two legs joined by a V in the middle
    letter_points = [
        (letter_left, mark_bottom),
        (letter_left, mark_top),
        (letter_left + stroke, mark_top),
        (letter_middle, mark_top + valley_depth - stroke * 0.6),
        (letter_right - stroke, mark_top),
        (letter_right, mark_top),
        (letter_right, mark_bottom),
        (letter_right - stroke, mark_bottom),
        (letter_right - stroke, mark_top + stroke * 1.6),
        (letter_middle, mark_top + valley_depth + stroke * 0.5),
        (letter_left + stroke, mark_top + stroke * 1.6),
        (letter_left + stroke, mark_bottom),
    ]
    painter.polygon(to_canvas(letter_points), fill=MARK_BLUE)

    # The down arrow: a shaft and a triangular head
    arrow_head_top = mark_bottom - arrow_head_half * 1.1
    arrow_points = [
        (arrow_center - arrow_shaft_half, mark_top),
        (arrow_center + arrow_shaft_half, mark_top),
        (arrow_center + arrow_shaft_half, arrow_head_top),
        (arrow_center + arrow_head_half, arrow_head_top),
        (arrow_center, mark_bottom),
        (arrow_center - arrow_head_half, arrow_head_top),
        (arrow_center - arrow_shaft_half, arrow_head_top),
    ]
    painter.polygon(to_canvas(arrow_points), fill=MARK_BLUE)

    # Downsample to the target size for smooth edges
    return image.resize((pixel_size, pixel_size), Image.LANCZOS)


def main() -> None:
    repository_root = Path(__file__).resolve().parent.parent
    output_path = repository_root / "src" / "MarkdownViewer" / "Assets" / "AppIcon.ico"

    rendered_images = [draw_icon(icon_size) for icon_size in ICON_SIZES]
    # Pillow writes every image into one .ico; the largest is passed as the base
    largest_image = rendered_images[-1]
    largest_image.save(output_path, format="ICO",
                       sizes=[(icon_size, icon_size) for icon_size in ICON_SIZES],
                       append_images=rendered_images[:-1])
    print(f"Wrote {output_path}")


if __name__ == "__main__":
    main()
