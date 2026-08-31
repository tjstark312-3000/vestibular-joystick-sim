from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "assets" / "vforce-logo-vm.png"
ICON = ROOT / "src" / "VestibularJoystickSim" / "VMocion.ico"
PREVIEW = ROOT / "assets" / "VMocion-icon-preview.png"

CANVAS_SIZE = 1024
ICON_SIZES = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40),
              (48, 48), (64, 64), (128, 128), (256, 256)]


def main() -> None:
    logo = Image.open(SOURCE).convert("RGBA")
    bounds = logo.getbbox()
    if bounds:
        logo = logo.crop(bounds)

    scale = min(900 / logo.width, 560 / logo.height)
    logo = logo.resize(
        (round(logo.width * scale), round(logo.height * scale)),
        Image.Resampling.LANCZOS,
    )

    canvas = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle(
        (16, 16, CANVAS_SIZE - 16, CANVAS_SIZE - 16),
        radius=210,
        fill=(8, 8, 13, 255),
        outline=(118, 57, 255, 255),
        width=18,
    )

    position = ((CANVAS_SIZE - logo.width) // 2, (CANVAS_SIZE - logo.height) // 2)
    glow_alpha = logo.getchannel("A").filter(ImageFilter.GaussianBlur(26))
    glow = Image.new("RGBA", logo.size, (122, 60, 255, 0))
    glow.putalpha(glow_alpha.point(lambda value: value * 2 // 3))
    canvas.alpha_composite(glow, position)
    canvas.alpha_composite(logo, position)

    ICON.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(PREVIEW, "PNG")
    canvas.save(ICON, "ICO", sizes=ICON_SIZES)


if __name__ == "__main__":
    main()
