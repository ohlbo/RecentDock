"""Generate src/RecentDock.App/app.ico.

Run with the bundled Python:

    <python> tools/make-icon.py

The icon is drawn procedurally rather than shipped as a binary blob, so the artwork
stays editable: the geometry below is the single source of truth and every size is
rendered from it. Nothing here is needed at build time - the checked-in app.ico is
the only artifact the project consumes.

Design notes
------------
The mark is a stack of two white cards over an indigo gradient, with an amber clock
badge. Rationale:

  * The panel is a "recent items" dock, so the metaphor is a document list, not a
    file-type icon.
  * It has to survive 16x16, which is the tray size. That rules out thin strokes,
    text and gradients inside the mark: the shapes are chunky and there are three of
    them (back card, front card, badge).
  * The detail lines on the front card are sized so they blur into texture at 16 px
    and read as ruled lines at 256 px, rather than turning into noise at small sizes.

Each size is rendered natively at 8x supersampling and then box-filtered down, rather
than scaled from one large bitmap. Downsizing a 256 px render to 16 px softens every
edge; rendering at 128 px for the 16 px entry keeps the 1 px gaps actually separated.
"""

from __future__ import annotations

import struct
from pathlib import Path

from PIL import Image, ImageDraw

# Sizes shipped in the .ico. 20/24/40 cover the Windows shell's non-default DPI
# steps; 256 is the large-icon view in Explorer.
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

SUPERSAMPLE = 8

# Palette. Indigo reads as "app" against both the light and the dark Windows theme,
# and amber is the only accent that stays distinguishable from it at 16 px.
GRADIENT_TOP = (99, 102, 241)
GRADIENT_BOTTOM = (59, 48, 180)
CARD_FRONT = (255, 255, 255)
CARD_BACK = (214, 219, 255)
LINE_COLOR = (79, 70, 229)
BADGE = (251, 191, 36)
BADGE_HANDS = (49, 36, 129)


def rounded_rect(draw: ImageDraw.ImageDraw, box: tuple[float, float, float, float],
                 radius: float, fill) -> None:
    draw.rounded_rectangle(box, radius=radius, fill=fill)


def vertical_gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int]) -> Image.Image:
    """Build the background gradient once per size, before the mask is applied."""
    gradient = Image.new("RGB", (1, size))
    pixels = gradient.load()
    for y in range(size):
        t = y / max(size - 1, 1)
        # Ease out so the light end holds for longer and the mark does not look
        # like it is lit from a point source.
        t = t ** 0.85
        pixels[0, y] = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
    return gradient.resize((size, size), Image.BILINEAR)


def draw_icon(scale: int) -> Image.Image:
    """Render the mark at `scale` pixels square (already supersampled)."""
    size = scale * SUPERSAMPLE
    image = vertical_gradient(size, GRADIENT_TOP, GRADIENT_BOTTOM).convert("RGBA")

    # Squircle-ish background: a rounded square, not a circle. Windows 11 app marks
    # are square-ish with a generous radius; 0.225 keeps the silhouette distinct.
    background_mask = Image.new("L", (size, size), 0)
    rounded_rect(
        ImageDraw.Draw(background_mask),
        (0, 0, size - 1, size - 1),
        radius=size * 0.225,
        fill=255,
    )
    image.putalpha(background_mask)

    # A soft highlight across the top third gives the flat gradient some depth
    # without introducing a second hue that would muddy the 16 px version.
    highlight = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    rounded_rect(
        ImageDraw.Draw(highlight),
        (-size * 0.1, -size * 0.32, size * 1.1, size * 0.30),
        radius=size * 0.28,
        fill=(255, 255, 255, 34),
    )
    image = Image.alpha_composite(image, highlight)

    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(layer)

    # Back card, offset up and to the right, so the stack reads as "history piling
    # up" rather than as a single document.
    rounded_rect(
        draw,
        (size * 0.305, size * 0.150, size * 0.845, size * 0.690),
        radius=size * 0.075,
        fill=CARD_BACK + (255,),
    )

    # Front card.
    front = (size * 0.155, size * 0.310, size * 0.695, size * 0.850)
    rounded_rect(draw, front, radius=size * 0.080, fill=CARD_FRONT + (255,))

    # Ruled lines. Two, not three: three lines at 16 px merge into a grey band.
    line_x0 = size * 0.245
    for index, (y, x1) in enumerate((
        (0.440, 0.585),
        (0.555, 0.520),
    )):
        rounded_rect(
            draw,
            (line_x0, size * y, size * x1, size * (y + 0.058)),
            radius=size * 0.029,
            fill=LINE_COLOR + (150 if index == 0 else 110,),
        )

    # Clock badge, overlapping the front card's bottom-right corner. This is the
    # "recent" half of the idea and the only warm colour in the mark.
    badge_box = (
        size * 0.545,
        size * 0.575,
        size * 0.890,
        size * 0.920,
    )
    rounded_rect(draw, (badge_box[0] - size * 0.022, badge_box[1] - size * 0.022,
                        badge_box[2] + size * 0.022, badge_box[3] + size * 0.022),
                 radius=size * 0.195, fill=(255, 255, 255, 235))
    rounded_rect(draw, badge_box, radius=size * 0.1725, fill=BADGE + (255,))

    # Hands set to 10:10, the symmetric "uplifted" reading every clock icon uses: the
    # hour hand leans up-left and the longer minute hand up-right. Drawn only from
    # 32 px upwards; at 16 px they add a smudge rather than an hour.
    if scale >= 32:
        center_x = (badge_box[0] + badge_box[2]) / 2
        center_y = (badge_box[1] + badge_box[3]) / 2
        radius = (badge_box[2] - badge_box[0]) / 2
        width = max(1, int(size * 0.016))

        def hand(angle_degrees: float, length_factor: float) -> None:
            from math import cos, radians, sin

            angle = radians(angle_degrees)
            draw.line(
                [
                    (center_x, center_y),
                    (center_x + sin(angle) * radius * length_factor,
                     center_y - cos(angle) * radius * length_factor),
                ],
                fill=BADGE_HANDS + (255,),
                width=width,
            )

        hand(-52, 0.44)   # hour hand, roughly the 10 o'clock position
        hand(72, 0.86)    # minute hand, roughly the 2 o'clock position
        draw.ellipse(
            (center_x - width * 0.85, center_y - width * 0.85,
             center_x + width * 0.85, center_y + width * 0.85),
            fill=BADGE_HANDS + (255,),
        )

    image = Image.alpha_composite(image, layer)
    return image.resize((scale, scale), Image.LANCZOS)


def dib_entry(image: Image.Image) -> bytes:
    """Encode one ICO entry as a 32-bit bottom-up DIB with an all-zero AND mask."""
    width, height = image.size
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        width,
        height * 2,  # includes the AND mask
        1,           # biPlanes
        32,          # biBitCount
        0,           # BI_RGB
        width * height * 4,
        0, 0, 0, 0,
    )

    rgba = image.load()
    pixels = bytearray()
    for y in range(height - 1, -1, -1):
        for x in range(width):
            r, g, b, a = rgba[x, y]
            # Premultiplied is NOT wanted here: ICO stores straight BGRA.
            pixels += bytes((b, g, r, a))

    stride = ((width + 31) // 32) * 4
    mask = bytes(stride * height)
    return header + bytes(pixels) + mask


def png_entry(image: Image.Image) -> bytes:
    import io

    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def write_ico(path: Path) -> None:
    rendered = {size: draw_icon(size) for size in SIZES}

    entries = []
    for size in SIZES:
        # PNG keeps the 256 px entry small; the smaller sizes stay as DIBs, which
        # every shell version reads without going through the PNG decoder.
        payload = png_entry(rendered[size]) if size >= 128 else dib_entry(rendered[size])
        entries.append((size, payload))

    directory = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    header = bytearray()
    blobs = bytearray()

    for size, payload in entries:
        dimension = 0 if size >= 256 else size
        header += struct.pack(
            "<BBBBHHII",
            dimension,
            dimension,
            0,   # palette colours
            0,   # reserved
            1,   # colour planes
            32,  # bits per pixel
            len(payload),
            offset,
        )
        blobs += payload
        offset += len(payload)

    path.write_bytes(bytes(directory + header + blobs))


def main() -> None:
    target = Path(__file__).resolve().parent.parent / "src" / "RecentDock.App" / "app.ico"
    write_ico(target)
    print(f"wrote {target} ({target.stat().st_size} bytes)")

    # Preview strip: every size at its native resolution, so the 16 px entry can be
    # judged rather than guessed at.
    preview_dir = Path(__file__).resolve().parent.parent / ".tmp" / "icon-preview"
    preview_dir.mkdir(parents=True, exist_ok=True)
    for size in SIZES:
        draw_icon(size).save(preview_dir / f"icon-{size}.png")
    print(f"previews in {preview_dir}")


if __name__ == "__main__":
    main()
