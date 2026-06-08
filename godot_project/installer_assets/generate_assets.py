#!/usr/bin/env python3
"""Generate Inno Setup installer assets for Velos Content Creation Suite."""

from PIL import Image, ImageDraw, ImageFont
import os, subprocess, tempfile

OUT = os.path.dirname(os.path.abspath(__file__))
ASSETS = os.path.join(os.path.dirname(OUT), "Assets")

def render_svg(svg_path, width, height):
    """Convert SVG to PNG at given size using rsvg-convert."""
    out = tempfile.NamedTemporaryFile(suffix=".png", delete=False)
    out.close()
    subprocess.run(
        ["rsvg-convert", "-w", str(width), "-h", str(height),
         "-o", out.name, svg_path],
        check=True, capture_output=True)
    img = Image.open(out.name).convert("RGBA")
    os.unlink(out.name)
    return img

def create_wizard_image():
    """Create the 164x314 sidebar image for the modern wizard."""
    W, H = 164, 314
    img = Image.new("RGBA", (W, H), (0x1a, 0x1a, 0x2e, 0xff))
    draw = ImageDraw.Draw(img)

    # Gradient overlay — subtle lighter band near bottom
    for y in range(H):
        t = y / H
        r = int(0x1a + (0x16 * t))
        g = int(0x1a + (0x23 * t))
        b = int(0x2e + (0x60 * t))
        draw.line([(0, y), (W, y)], fill=(r, g, b, 0xff))

    # Decorative accent bar at top
    draw.rectangle([0, 0, W, 3], fill=(0x4a, 0x9e, 0xff, 0xff))

    # Render the icon SVG at 80x80
    icon_svg = os.path.join(ASSETS, "..", "icon.svg")
    if os.path.exists(icon_svg):
        icon = render_svg(icon_svg, 80, 80)
        # Center icon near top
        ix = (W - 80) // 2
        iy = 40
        img.paste(icon, (ix, iy), icon)

    # "VelosCCS" title
    try:
        font_large = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 18)
    except (IOError, OSError):
        font_large = ImageFont.load_default()
    draw.text((82, 140), "Velos", fill=(0xff, 0xff, 0xff, 0xff),
              font=font_large, anchor="mm")
    try:
        font_small_sub = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 11)
    except (IOError, OSError):
        font_small_sub = ImageFont.load_default()
    draw.text((82, 158), "Content Creation Suite", fill=(0xcc, 0xcc, 0xee, 0xff),
              font=font_small_sub, anchor="mm")

    try:
        font_small = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 11)
    except (IOError, OSError):
        font_small = ImageFont.load_default()

    # Feature bullets
    features = [
        "Auto transcription",
        "AI highlight detection",
        "PiP / facecam layouts",
        "Burn-in export",
    ]
    for i, feat in enumerate(features):
        y = 210 + i * 22
        draw.ellipse([35, y + 4, 43, y + 12], fill=(0x4a, 0x9e, 0xff, 0xff))
        draw.text((50, y + 8), feat, fill=(0xcc, 0xcc, 0xee, 0xff),
                  font=font_small, anchor="lm")

    # Version at bottom
    draw.text((82, H - 20), "v4.0.2", fill=(0x66, 0x66, 0x99, 0xff),
              font=font_small, anchor="mm")

    # Subtitle "AI-Powered Video Clips" (repositioned to below the suite name)
    draw.text((82, 175), "AI-Powered Video Clips", fill=(0x88, 0x88, 0xbb, 0xff),
              font=font_small, anchor="mm")

    path = os.path.join(OUT, "WizardImageFile.png")
    img.save(path, "PNG")
    print(f"  Created {path} ({W}x{H})")
    return path

def create_small_image():
    """Create the 55x55 small wizard image."""
    size = 55
    img = Image.new("RGBA", (size, size), (0x1a, 0x1a, 0x2e, 0xff))
    draw = ImageDraw.Draw(img)

    # Decorative border
    draw.rectangle([0, 0, size - 1, size - 1], outline=(0x4a, 0x9e, 0xff, 0xff), width=1)

    # Render icon at 44x44
    icon_svg = os.path.join(ASSETS, "..", "icon.svg")
    if os.path.exists(icon_svg):
        icon = render_svg(icon_svg, 44, 44)
        ix = (size - 44) // 2
        iy = (size - 44) // 2
        img.paste(icon, (ix, iy), icon)

    path = os.path.join(OUT, "WizardSmallImageFile.bmp")
    # Inno Setup needs BMP for small image
    img_bmp = img.convert("RGB")
    img_bmp.save(path, "BMP")
    print(f"  Created {path} ({size}x{size})")
    return path

def create_icon():
    """Create .ico from the SVG icon."""
    icon_svg = os.path.join(ASSETS, "..", "icon.svg")
    if not os.path.exists(icon_svg):
        print("  SKIP: icon.svg not found")
        return None

    # Render at max resolution, let PIL downscale for sub-sizes
    max_size = 256
    img = render_svg(icon_svg, max_size, max_size)

    sizes = [16, 32, 48, 64, 128, 256]
    path = os.path.join(OUT, "SetupIcon.ico")
    img.save(path, "ICO", sizes=[(s, s) for s in sizes])
    print(f"  Created {path} ({len(sizes)} sizes)")
    return path

if __name__ == "__main__":
    print("Generating Velos Content Creation Suite installer assets...")
    create_wizard_image()
    create_small_image()
    create_icon()
    print("Done.")
