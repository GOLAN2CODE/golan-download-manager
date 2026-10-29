#!/usr/bin/env python3
"""
Reproducible Golan Download Manager (GDM 2.2.0) brand asset generator.

Reads:
  design/gdm-logo-source.webp

Produces:
  1. GDM/src/GDM/Assets/gdm.ico (multi-size: 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 px)
  2. GDM/src/GDM/Assets/gdm.png (512x512 mark-only app icon)
     and GDM/src/GDM/Assets/gdm-logo-full.png (cleaned full logo with text)
  3. GDM/extension/icons/icon16.png, icon32.png, icon48.png, icon128.png
  4. GDM/store/icon-128.png, icon-300.png, promo-small-440x280.png,
     promo-large-920x680.png, promo-marquee-1400x560.png, screenshot-1280x800.png
  5. GDM/installer/wizard-large-{100..250}.bmp and wizard-small-{100..250}.bmp
"""

from __future__ import annotations

import io
import struct
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

BRAND_YELLOW = (253, 209, 3)      # #FDD103
BRAND_YELLOW_DARK = (224, 177, 0) # #E0B100
BRAND_INK = (19, 19, 19)          # #131313
DARK_BG = (14, 14, 16)            # #0E0E10
DARK_CARD = (22, 22, 25)          # #161619
DARK_LINE = (44, 44, 51)          # #2C2C33
LIGHT_TEXT = (244, 244, 246)      # #F4F4F6
MUTED_TEXT = (161, 161, 170)      # #A1A1AA


def repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


def _extract_tight_ink_mask_from_card(card_png: Path) -> Image.Image:
    im = Image.open(card_png).convert("RGBA")
    arr = np.asarray(im, dtype=np.float32)
    lum = 0.299 * arr[:, :, 0] + 0.587 * arr[:, :, 1] + 0.114 * arr[:, :, 2]
    bg_lum = 0.299 * BRAND_YELLOW[0] + 0.587 * BRAND_YELLOW[1] + 0.114 * BRAND_YELLOW[2]
    ink_lum = 0.299 * BRAND_INK[0] + 0.587 * BRAND_INK[1] + 0.114 * BRAND_INK[2]
    alpha = np.clip((bg_lum - lum) / (bg_lum - ink_lum), 0.0, 1.0)
    # Only consider interior of rounded card where card alpha is opaque
    alpha = np.where(arr[:, :, 3] > 240, alpha, 0.0)
    alpha_u8 = (alpha * 255.0).round().astype(np.uint8)
    ys, xs = np.where(alpha_u8 > 8)
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    x0, x1 = int(xs.min()), int(xs.max()) + 1
    return Image.fromarray(alpha_u8[y0:y1, x0:x1], mode="L")


def extract_clean_alpha_masks(source_webp: Path, assets_dir: Path | None = None) -> tuple[Image.Image, Image.Image]:
    """
    Loads design/gdm-logo-source.webp (or falls back to Assets/gdm.png and
    Assets/gdm-logo-full.png when running inside an isolated repo copy) and
    extracts pure anti-aliased alpha masks for:
      - mark_alpha: the 'GDM' mark only (with M download arrow, no words)
      - full_alpha: the full 'GDM' mark + 'Golan Download Manager' wordmark
    """
    if not source_webp.exists():
        if assets_dir is not None and (assets_dir / "gdm.png").exists() and (assets_dir / "gdm-logo-full.png").exists():
            return (
                _extract_tight_ink_mask_from_card(assets_dir / "gdm.png"),
                _extract_tight_ink_mask_from_card(assets_dir / "gdm-logo-full.png"),
            )
        raise FileNotFoundError(f"Missing brand source logo: {source_webp}")

    im = Image.open(source_webp).convert("RGB")
    arr = np.asarray(im, dtype=np.float32)

    # Remove the '04' design-sheet number in the top-left corner (bbox x:36..134, y:36..93)
    arr[:220, :220, 0] = BRAND_YELLOW[0]
    arr[:220, :220, 1] = BRAND_YELLOW[1]
    arr[:220, :220, 2] = BRAND_YELLOW[2]

    # Compute perceptual luminance and derive smooth ink coverage alpha in [0, 1]
    lum = 0.299 * arr[:, :, 0] + 0.587 * arr[:, :, 1] + 0.114 * arr[:, :, 2]
    bg_lum = 0.299 * BRAND_YELLOW[0] + 0.587 * BRAND_YELLOW[1] + 0.114 * BRAND_YELLOW[2]
    ink_lum = 0.299 * BRAND_INK[0] + 0.587 * BRAND_INK[1] + 0.114 * BRAND_INK[2]

    alpha = np.clip((bg_lum - lum) / (bg_lum - ink_lum), 0.0, 1.0)
    # Suppress WebP compression ringing below 6% coverage
    alpha = np.where(alpha < 0.06, 0.0, np.clip((alpha - 0.06) / 0.90, 0.0, 1.0))
    alpha_u8 = (alpha * 255.0).round().astype(np.uint8)

    # Crop mark-only (y: 374..807, x: 84..1198)
    mark_crop = alpha_u8[374:807, 84:1198]
    # Crop full logo (y: 374..904, x: 84..1198)
    full_crop = alpha_u8[374:904, 84:1198]

    return Image.fromarray(mark_crop, mode="L"), Image.fromarray(full_crop, mode="L")


def make_simplified_small_mark(mark_alpha: Image.Image) -> Image.Image:
    """
    Creates a dedicated 1:1 square bold glyph for 16, 20, and 24 px icons:
    a geometric 'G' with an integrated downward download arrow designed on a
    12x12 pixel-aligned grid (480x480, 40 units per 16x16 pixel) so every
    stroke and counter reads with maximum contrast and zero blur in the
    Windows system tray, window title bar, and browser extension toolbar.
    """
    _ = mark_alpha
    sz = 480
    mask = Image.new("L", (sz, sz), 0)
    draw = ImageDraw.Draw(mask)

    # 1. Outer rounded-C body of the bold geometric 'G'
    #    Left stem: x=0..108, Top bar: y=0..104, Bottom bar: y=376..480 (x=0..236)
    draw.rounded_rectangle([0, 0, 400, 104], radius=36, fill=255)
    draw.rounded_rectangle([0, 0, 108, 480], radius=36, fill=255)
    draw.rounded_rectangle([0, 376, 240, 480], radius=32, fill=255)

    # 2. Inner crossbar of 'G' turning downward into the download arrow shaft
    #    Crossbar: x=188..388, y=188..276
    #    Vertical shaft: x=284..388, y=188..348
    draw.rounded_rectangle([188, 188, 388, 276], radius=20, fill=255)
    draw. rectangle([284, 210, 388, 348], fill=255)

    # 3. Bold downward download arrowhead at bottom-right (tip at y=476)
    draw.polygon(
        [
            (196, 304),
            (476, 304),
            (336, 476),
        ],
        fill=255,
    )

    return mask


def render_rounded_icon(
    size: int,
    mark_alpha: Image.Image,
    small_mark_alpha: Image.Image,
    corner_ratio: float = 0.225,
) -> Image.Image:
    """
    Renders a crisp square icon at `size x size` with a supersampled Golan Yellow
    (#FDD103) rounded square and #131313 BrandInk mark.
    Uses the dedicated 1:1 'G + download arrow' glyph for sizes <= 24 px,
    and the full 'GDM' mark from 32 px upward.
    """
    ss = 4 if size <= 64 else 2
    canvas_sz = size * ss
    radius = int(round(canvas_sz * corner_ratio))

    # Create supersampled rounded square mask
    bg_mask = Image.new("L", (canvas_sz, canvas_sz), 0)
    draw = ImageDraw.Draw(bg_mask)
    inset = max(0, ss // 2)
    draw.rounded_rectangle(
        [inset, inset, canvas_sz - 1 - inset, canvas_sz - 1 - inset],
        radius=radius,
        fill=255,
    )

    # Base yellow fill
    rgba = np.zeros((canvas_sz, canvas_sz, 4), dtype=np.uint8)
    rgba[:, :, 0] = BRAND_YELLOW[0]
    rgba[:, :, 1] = BRAND_YELLOW[1]
    rgba[:, :, 2] = BRAND_YELLOW[2]
    rgba[:, :, 3] = np.asarray(bg_mask, dtype=np.uint8)

    # Choose dedicated 1:1 glyph for 16-24 px, full GDM mark for >= 32 px
    src_mark = small_mark_alpha if size <= 24 else mark_alpha
    width_fill = 0.75 if size <= 24 else 0.84
    target_w = max(1, int(round(canvas_sz * width_fill)))
    aspect = src_mark.height / src_mark.width
    target_h = max(1, int(round(target_w * aspect)))

    resized_mark = src_mark.resize((target_w, target_h), Image.Resampling.LANCZOS)
    mark_arr = np.asarray(resized_mark, dtype=np.float32) / 255.0

    # At small sizes, apply a crisp sigmoid contrast curve so edges are sharp in taskbar/tray
    if size <= 32:
        mark_arr = np.clip((mark_arr - 0.22) / 0.55, 0.0, 1.0)

    x0 = (canvas_sz - target_w) // 2
    # Optical vertical centering
    y0 = (canvas_sz - target_h) // 2 if size <= 24 else (canvas_sz - target_h) // 2 + int(round(canvas_sz * 0.015))

    roi = rgba[y0 : y0 + target_h, x0 : x0 + target_w].astype(np.float32)
    m = mark_arr[:, :, None]
    roi[:, :, 0] = roi[:, :, 0] * (1.0 - m[:, :, 0]) + BRAND_INK[0] * m[:, :, 0]
    roi[:, :, 1] = roi[:, :, 1] * (1.0 - m[:, :, 0]) + BRAND_INK[1] * m[:, :, 0]
    roi[:, :, 2] = roi[:, :, 2] * (1.0 - m[:, :, 0]) + BRAND_INK[2] * m[:, :, 0]
    rgba[y0 : y0 + target_h, x0 : x0 + target_w] = np.clip(roi, 0, 255).astype(np.uint8)

    out = Image.fromarray(rgba, mode="RGBA")
    if ss > 1:
        out = out.resize((size, size), Image.Resampling.LANCZOS)
    return out


def render_full_logo_card(size: int, full_alpha: Image.Image) -> Image.Image:
    """
    Renders the cleaned full logo (GDM mark + Golan Download Manager text)
    on a solid Golan Yellow #FDD103 rounded square.
    """
    radius = int(round(size * 0.18))
    bg_mask = Image.new("L", (size, size), 0)
    draw = ImageDraw.Draw(bg_mask)
    draw.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)

    rgba = np.zeros((size, size, 4), dtype=np.uint8)
    rgba[:, :, 0] = BRAND_YELLOW[0]
    rgba[:, :, 1] = BRAND_YELLOW[1]
    rgba[:, :, 2] = BRAND_YELLOW[2]
    rgba[:, :, 3] = np.asarray(bg_mask, dtype=np.uint8)

    target_w = int(round(size * 0.86))
    target_h = int(round(target_w * (full_alpha.height / full_alpha.width)))
    resized = full_alpha.resize((target_w, target_h), Image.Resampling.LANCZOS)
    m = np.asarray(resized, dtype=np.float32) / 255.0

    x0 = (size - target_w) // 2
    y0 = (size - target_h) // 2
    roi = rgba[y0 : y0 + target_h, x0 : x0 + target_w].astype(np.float32)
    roi[:, :, 0] = roi[:, :, 0] * (1.0 - m) + BRAND_INK[0] * m
    roi[:, :, 1] = roi[:, :, 1] * (1.0 - m) + BRAND_INK[1] * m
    roi[:, :, 2] = roi[:, :, 2] * (1.0 - m) + BRAND_INK[2] * m
    rgba[y0 : y0 + target_h, x0 : x0 + target_w] = np.clip(roi, 0, 255).astype(np.uint8)

    return Image.fromarray(rgba, mode="RGBA")


def save_multi_size_ico(ico_path: Path, frames: dict[int, Image.Image]) -> None:
    """
    Packs exact PNG frames for each size (e.g. 16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    into a standard Windows ICO container without Pillow re-scaling or dropping frames.
    """
    sorted_sizes = sorted(frames.keys())
    png_blobs: list[tuple[int, bytes]] = []
    for sz in sorted_sizes:
        buf = io.BytesIO()
        frames[sz].save(buf, format="PNG", optimize=True)
        png_blobs.append((sz, buf.getvalue()))

    count = len(png_blobs)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries = bytearray()
    payloads = bytearray()

    for sz, blob in png_blobs:
        w_byte = 0 if sz >= 256 else sz
        h_byte = 0 if sz >= 256 else sz
        entry = struct.pack(
            "<BBBBHHII",
            w_byte,
            h_byte,
            0,      # palette count
            0,      # reserved
            1,      # color planes
            32,     # bits per pixel
            len(blob),
            offset,
        )
        entries.extend(entry)
        payloads.extend(blob)
        offset += len(blob)

    ico_path.parent.mkdir(parents=True, exist_ok=True)
    ico_path.write_bytes(header + entries + payloads)


def composite_tinted_alpha(
    canvas: Image.Image,
    alpha_mask: Image.Image,
    target_w: int,
    pos: tuple[int, int],
    color: tuple[int, int, int],
) -> tuple[int, int]:
    """Composites `alpha_mask` tinted with `color` onto RGBA/RGB `canvas` at `pos`."""
    target_h = max(1, int(round(target_w * (alpha_mask.height / alpha_mask.width))))
    resized = alpha_mask.resize((target_w, target_h), Image.Resampling.LANCZOS)
    layer = Image.new("RGBA", (target_w, target_h), (*color, 0))
    layer.putalpha(resized)
    canvas.paste(layer, pos, layer)
    return target_w, target_h


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = (
        ["segoeuib.ttf", "arialbd.ttf", "calibrib.ttf"]
        if bold
        else ["segoeui.ttf", "arial.ttf", "calibri.ttf"]
    )
    for name in candidates:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def build_store_promo_tile(
    width: int,
    height: int,
    mark_alpha: Image.Image,
    full_alpha: Image.Image,
    subtitle: str = "Fast Segmented HTTP, HLS/DASH Video & BitTorrent Download Manager",
) -> Image.Image:
    """
    Renders a sharp, Apple-grade store promo/marquee tile in Golan Yellow (#FDD103)
    and near-black carbon (#0E0E10).
    """
    img = Image.new("RGB", (width, height), DARK_BG)
    draw = ImageDraw.Draw(img)

    # Top Golan Yellow accent bar
    bar_h = max(6, int(round(height * 0.022)))
    draw.rectangle([0, 0, width, bar_h], fill=BRAND_YELLOW)

    # Left yellow hero badge card
    pad = int(round(height * 0.10))
    card_h = height - 2 * pad
    card_w = min(int(round(card_h * 0.92)), int(round(width * 0.34)))
    if width < 600:
        # Compact centered layout for 440x280 small promo tile
        badge_w = int(round(width * 0.84))
        badge_h = int(round(height * 0.56))
        bx = (width - badge_w) // 2
        by = int(round(height * 0.10))
        draw.rounded_rectangle(
            [bx, by, bx + badge_w, by + badge_h],
            radius=int(round(height * 0.06)),
            fill=BRAND_YELLOW,
        )
        logo_w = int(round(badge_w * 0.82))
        logo_h = int(round(logo_w * (full_alpha.height / full_alpha.width)))
        lx = bx + (badge_w - logo_w) // 2
        ly = by + (badge_h - logo_h) // 2
        composite_tinted_alpha(img, full_alpha, logo_w, (lx, ly), BRAND_INK)

        f_sub = load_font(max(13, int(round(height * 0.055))), bold=True)
        tw = draw.textlength("Golan Download Manager 2.2 · Browser Companion", font=f_sub)
        draw.text(
            ((width - tw) / 2, by + badge_h + int(round(height * 0.09))),
            "Golan Download Manager 2.2 · Browser Companion",
            fill=BRAND_YELLOW,
            font=f_sub,
        )
        return img

    # Split hero layout for 920x680, 1400x560, 1280x800
    draw.rounded_rectangle(
        [pad, pad, pad + card_w, pad + card_h],
        radius=int(round(height * 0.05)),
        fill=BRAND_YELLOW,
    )
    logo_w = int(round(card_w * 0.84))
    logo_h = int(round(logo_w * (full_alpha.height / full_alpha.width)))
    composite_tinted_alpha(
        img,
        full_alpha,
        logo_w,
        (pad + (card_w - logo_w) // 2, pad + (card_h - logo_h) // 2),
        BRAND_INK,
    )

    # Right side typography & feature cards
    rx = pad + card_w + int(round(width * 0.04))
    avail_w = width - rx - pad
    ry = pad + int(round(card_h * 0.05))

    title_px = int(round(height * 0.068))
    f_title = load_font(title_px, bold=True)
    while title_px > 20 and draw.textlength("Golan Download Manager", font=f_title) > avail_w:
        title_px -= 2
        f_title = load_font(title_px, bold=True)

    sub_px = int(round(height * 0.032))
    f_sub = load_font(sub_px, bold=False)
    while sub_px > 12 and draw.textlength(subtitle, font=f_sub) > avail_w:
        sub_px -= 1
        f_sub = load_font(sub_px, bold=False)

    pill_px = min(int(round(height * 0.028)), int(round(avail_w * 0.038)))
    f_pill = load_font(max(13, pill_px), bold=True)

    draw.text((rx, ry), "Golan Download Manager", fill=LIGHT_TEXT, font=f_title)
    ry += int(round(title_px * 1.38))
    draw.text((rx, ry), subtitle, fill=MUTED_TEXT, font=f_sub)
    ry += int(round(sub_px * 2.2))

    features = [
        "32x Segmented HTTP/HTTPS Engine",
        "One-Click HLS / DASH Video Stream Grabber",
        "Native BitTorrent & Magnet Support",
        "Arabic (RTL) & English · Dark & Light Themes",
    ]
    pill_h = int(round(height * 0.082))
    pill_w = avail_w
    for feat in features:
        draw.rounded_rectangle(
            [rx, ry, rx + pill_w, ry + pill_h],
            radius=int(round(pill_h * 0.28)),
            fill=DARK_CARD,
            outline=DARK_LINE,
            width=2,
        )
        # Yellow accent dot
        dot_r = max(4, int(round(pill_h * 0.14)))
        dot_cx = rx + int(round(pill_h * 0.48))
        dot_cy = ry + pill_h // 2
        draw.ellipse([dot_cx - dot_r, dot_cy - dot_r, dot_cx + dot_r, dot_cy + dot_r], fill=BRAND_YELLOW)
        draw.text(
            (dot_cx + dot_r * 2 + 8, ry + (pill_h - pill_px) // 2 - 2),
            feat,
            fill=LIGHT_TEXT,
            font=f_pill,
        )
        ry += pill_h + int(round(height * 0.028))

    return img


def build_store_screenshot_1280x800(
    root: Path,
    mark_alpha: Image.Image,
    full_alpha: Image.Image,
) -> Image.Image:
    """
    Creates the 1280x800 store showcase screenshot by framing the rendered
    Main_1366x768.ar.dark.png window on a branded Golan dark canvas.
    """
    rendered_main = root / "GDM" / "tests" / "render" / "out" / "Main_1366x768.ar.dark.png"
    if not rendered_main.exists():
        return build_store_promo_tile(
            1280,
            800,
            mark_alpha,
            full_alpha,
            subtitle="High-Speed Segmented Downloads · Media Grabber · BitTorrent · Remote Control",
        )

    width, height = 1280, 800
    img = Image.new("RGB", (width, height), DARK_BG)
    draw = ImageDraw.Draw(img)

    # Top Golan Yellow accent bar
    draw.rectangle([0, 0, width, 6], fill=BRAND_YELLOW)

    # Header bar with Golan badge + title
    badge = render_rounded_icon(44, mark_alpha, mark_alpha)
    img.paste(badge, (48, 22), badge)
    f_head = load_font(22, bold=True)
    f_sub = load_font(15, bold=False)
    draw.text((104, 21), "Golan Download Manager 2.2", fill=LIGHT_TEXT, font=f_head)
    draw.text(
        (104, 47),
        "32x Segmented HTTP/HTTPS  ·  HLS/DASH Video Grabber  ·  BitTorrent  ·  LAN Remote",
        fill=BRAND_YELLOW,
        font=f_sub,
    )

    # Framed app window screenshot
    win_img = Image.open(rendered_main).convert("RGBA")
    target_w = 1184
    target_h = int(round(target_w * (win_img.height / win_img.width)))
    if target_h > 684:
        target_h = 684
        target_w = int(round(target_h * (win_img.width / win_img.height)))
    win_resized = win_img.resize((target_w, target_h), Image.Resampling.LANCZOS)

    wx = (width - target_w) // 2
    wy = 86
    draw.rounded_rectangle(
        [wx - 3, wy - 3, wx + target_w + 3, wy + target_h + 3],
        radius=12,
        fill=DARK_CARD,
        outline=BRAND_YELLOW,
        width=2,
    )
    img.paste(win_resized, (wx, wy), win_resized)
    return img


def build_installer_wizard_bmps(installer_dir: Path, mark_alpha: Image.Image, full_alpha: Image.Image) -> None:
    """
    Generates Inno Setup 6 modern wizard large (164x314 @ 100%) and small (55x55 @ 100%)
    24-bit BMP images across 100%, 125%, 150%, 175%, 200%, 225%, 250% DPI scales.
    """
    scales = [
        (100, 1.00),
        (125, 1.25),
        (150, 1.50),
        (175, 1.75),
        (200, 2.00),
        (225, 2.25),
        (250, 2.50),
    ]
    installer_dir.mkdir(parents=True, exist_ok=True)

    for pct, factor in scales:
        # 1. WizardLargeImage (base 164x314)
        lw = int(round(164 * factor))
        lh = int(round(314 * factor))
        large = Image.new("RGB", (lw, lh), DARK_BG)
        ldraw = ImageDraw.Draw(large)

        # Top Golan Yellow banner card inside the wizard sidebar
        pad = max(8, int(round(12 * factor)))
        card_w = lw - 2 * pad
        card_h = int(round(card_w * 0.92))
        top_y = int(round(lh * 0.14))
        ldraw.rounded_rectangle(
            [pad, top_y, pad + card_w, top_y + card_h],
            radius=max(6, int(round(14 * factor))),
            fill=BRAND_YELLOW,
        )
        logo_w = int(round(card_w * 0.86))
        logo_h = int(round(logo_w * (full_alpha.height / full_alpha.width)))
        composite_tinted_alpha(
            large,
            full_alpha,
            logo_w,
            (pad + (card_w - logo_w) // 2, top_y + (card_h - logo_h) // 2),
            BRAND_INK,
        )

        # Bottom subtle yellow accent stripe
        stripe_h = max(4, int(round(6 * factor)))
        ldraw.rectangle([0, lh - stripe_h, lw, lh], fill=BRAND_YELLOW)
        large.save(installer_dir / f"wizard-large-{pct}.bmp", format="BMP")

        # 2. WizardSmallImage (base 55x55)
        sw = int(round(55 * factor))
        sh = int(round(55 * factor))
        small = Image.new("RGB", (sw, sh), BRAND_YELLOW)
        s_mark_w = int(round(sw * 0.84))
        s_mark_h = int(round(s_mark_w * (mark_alpha.height / mark_alpha.width)))
        composite_tinted_alpha(
            small,
            mark_alpha,
            s_mark_w,
            ((sw - s_mark_w) // 2, (sh - s_mark_h) // 2),
            BRAND_INK,
        )
        small.save(installer_dir / f"wizard-small-{pct}.bmp", format="BMP")


def main() -> None:
    root = repo_root()
    source_webp = root / "design" / "gdm-logo-source.webp"
    assets_dir = root / "GDM" / "src" / "GDM" / "Assets"
    assets_dir.mkdir(parents=True, exist_ok=True)

    mark_alpha, full_alpha = extract_clean_alpha_masks(source_webp, assets_dir)
    small_mark_alpha = make_simplified_small_mark(mark_alpha)

    # 1. App Assets (Assets/gdm.ico, gdm.png, gdm-logo-full.png)
    ico_sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    ico_frames = {
        sz: render_rounded_icon(sz, mark_alpha, small_mark_alpha)
        for sz in ico_sizes
    }
    save_multi_size_ico(assets_dir / "gdm.ico", ico_frames)

    if source_webp.exists():
        png_512 = render_rounded_icon(512, mark_alpha, small_mark_alpha)
        png_512.save(assets_dir / "gdm.png", format="PNG", optimize=True)

        full_card_512 = render_full_logo_card(512, full_alpha)
        full_card_512.save(assets_dir / "gdm-logo-full.png", format="PNG", optimize=True)

    # 2. Browser Extension Icons (extension/icons/icon16..128.png)
    ext_icons_dir = root / "GDM" / "extension" / "icons"
    ext_icons_dir.mkdir(parents=True, exist_ok=True)
    for sz in (16, 32, 48, 128):
        ico_frames[sz].save(ext_icons_dir / f"icon{sz}.png", format="PNG", optimize=True)

    # 3. Store Tiles & Promotional Images (GDM/store/)
    store_dir = root / "GDM" / "store"
    store_dir.mkdir(parents=True, exist_ok=True)
    ico_frames[128].save(store_dir / "icon-128.png", format="PNG", optimize=True)
    render_rounded_icon(300, mark_alpha, small_mark_alpha).save(
        store_dir / "icon-300.png", format="PNG", optimize=True
    )
    build_store_promo_tile(440, 280, mark_alpha, full_alpha).save(
        store_dir / "promo-small-440x280.png", format="PNG", optimize=True
    )
    build_store_promo_tile(920, 680, mark_alpha, full_alpha).save(
        store_dir / "promo-large-920x680.png", format="PNG", optimize=True
    )
    build_store_promo_tile(1400, 560, mark_alpha, full_alpha).save(
        store_dir / "promo-marquee-1400x560.png", format="PNG", optimize=True
    )
    build_store_screenshot_1280x800(root, mark_alpha, full_alpha).save(
        store_dir / "screenshot-1280x800.png", format="PNG", optimize=True
    )

    # 4. Inno Setup 6 Wizard BMPs (GDM/installer/)
    installer_dir = root / "GDM" / "installer"
    build_installer_wizard_bmps(installer_dir, mark_alpha, full_alpha)

    print("Successfully generated all Golan Download Manager 2.2.0 brand assets.")


if __name__ == "__main__":
    main()
