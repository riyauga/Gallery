"""生成 assets/Gallery.ico —— 把标题栏左上角那个字形渲成应用图标。

字形取 Segoe Fluent Icons 的 E91B（就是 MainWindow.xaml 里标题栏那个 FontIcon 用的码位），
字体用系统自带的 C:\\Windows\\Fonts\\SegoeIcons.ttf（不进仓库、不随项目分发）。
样式：深灰 #2B2B2B 圆角方底 + 浅描边 + 白字形；尺寸 16/20/24/32/40/48/64/128/256。
扩展单独生成透明底、大主体的16/32/48/128px PNG；桌面图标保持原圆角底板。

用法：
    python tools/make_icon.py                      # 写 assets/Gallery.ico
    python tools/make_icon.py --preview <png路径>   # 另外导一张 256 预览图，方便肉眼核对
"""

import argparse
import os

from PIL import Image, ImageDraw, ImageFont

GLYPH = "\uE91B"
FONT_PATH = r"C:\Windows\Fonts\SegoeIcons.ttf"
BACKGROUND = (0x2B, 0x2B, 0x2B, 0xFF)
GLYPH_COLOR = (0xFF, 0xFF, 0xFF, 0xFF)
EDGE_COLOR = (0x5A, 0x5A, 0x5A, 0xFF)
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
MASTER = 256
SUPERSAMPLE = 4
CORNER_RATIO = 0.22
GLYPH_RATIO = 0.60


def render_master() -> Image.Image:
    size = MASTER * SUPERSAMPLE
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(canvas)
    draw.rounded_rectangle(
        [0, 0, size - 1, size - 1],
        radius=round(size * CORNER_RATIO),
        fill=BACKGROUND,
        outline=EDGE_COLOR,
        width=max(2, round(size * 0.008)),
    )

    font = ImageFont.truetype(FONT_PATH, round(size * 0.72))
    glyph = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ImageDraw.Draw(glyph).text((size / 2, size / 2), GLYPH, font=font, fill=GLYPH_COLOR, anchor="mm")

    box = glyph.getbbox()
    if box is None:
        raise SystemExit("字形没渲出来，检查字体文件与码位")
    ink = glyph.crop(box)
    scale = (size * GLYPH_RATIO) / max(ink.width, ink.height)
    ink = ink.resize((max(1, round(ink.width * scale)), max(1, round(ink.height * scale))), Image.LANCZOS)
    canvas.alpha_composite(ink, ((size - ink.width) // 2, (size - ink.height) // 2))
    return canvas.resize((MASTER, MASTER), Image.LANCZOS)


def write_extension_icons(root):
    font = ImageFont.truetype(FONT_PATH, MASTER * SUPERSAMPLE)
    glyph = Image.new("RGBA", (MASTER * SUPERSAMPLE * 2, MASTER * SUPERSAMPLE * 2))
    ImageDraw.Draw(glyph).text((MASTER * SUPERSAMPLE, MASTER * SUPERSAMPLE), GLYPH, font=font, fill="white", stroke_width=round(MASTER * SUPERSAMPLE * .025), stroke_fill=(45, 45, 45, 255), anchor="mm")
    glyph = glyph.crop(glyph.getbbox())
    for edge in (16, 32, 48, 128):
        ink = glyph.copy()
        ink.thumbnail((round(edge * .94), round(edge * .94)), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (edge, edge))
        canvas.alpha_composite(ink, ((edge - ink.width) // 2, (edge - ink.height) // 2))
        canvas.save(os.path.join(root, "extension", "icons", f"icon{edge}.png"))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--preview", help="额外导出一张 256 预览 PNG 的路径")
    parser.add_argument("--extension-only", action="store_true", help="只生成透明底扩展图标，不修改桌面图标")
    args = parser.parse_args()
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if args.extension_only:
        write_extension_icons(root)
        return

    master = render_master()
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    target = os.path.join(root, "assets", "Gallery.ico")
    master.save(target, format="ICO", sizes=[(s, s) for s in SIZES])
    print(f"wrote {target} ({os.path.getsize(target)} bytes, sizes={SIZES})")

    write_extension_icons(root)

    if args.preview:
        master.save(args.preview)
        print(f"wrote {args.preview}")


if __name__ == "__main__":
    main()
