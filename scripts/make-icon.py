"""Draws EKrecorder's icon (a blue rounded square with a white ring and a red record dot) at every size Windows
uses and writes src/EKrecorder/EKrecorder.ico. Run: python3 scripts/make-icon.py (needs Pillow)."""
import os
from PIL import Image, ImageDraw

BLUE = (0x1E, 0x6B, 0xFF, 255)
RED = (0xE5, 0x39, 0x35, 255)
WHITE = (255, 255, 255, 255)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def draw(size):
    scale = 8  # supersampling, then a high-quality reduction
    big = size * scale
    image = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(image)
    inset = big * 0.04
    d.rounded_rectangle([inset, inset, big - inset, big - inset], radius=big * 0.22, fill=BLUE)
    c = big / 2
    ring = big * 0.31
    d.ellipse([c - ring, c - ring, c + ring, c + ring], fill=WHITE)
    dot = big * 0.20
    d.ellipse([c - dot, c - dot, c + dot, c + dot], fill=RED)
    return image.resize((size, size), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(root, "src", "EKrecorder", "EKrecorder.ico")
    images = [draw(s) for s in SIZES]
    images[-1].save(target, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    print(f"wrote {target} ({os.path.getsize(target)} bytes)")


if __name__ == "__main__":
    main()
