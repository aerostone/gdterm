#!/usr/bin/env python3
"""Generate gdterm application icon — multi-size ICO using struct packing."""
from PIL import Image, ImageDraw, ImageFont
import struct
import os

def draw_icon(size):
    """Draw gdterm icon at given size."""
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    s = size
    pad = max(1, s // 16)
    r = max(2, s // 8)

    # Background: dark rounded rectangle
    bg_color = (25, 25, 30, 255)
    border_color = (60, 60, 70, 255)
    draw.rounded_rectangle(
        [pad, pad, s - pad - 1, s - pad - 1],
        radius=r, fill=bg_color, outline=border_color, width=max(1, s // 64)
    )

    # Title bar: 3 dots
    dot_y = pad + max(2, s // 10)
    dot_r = max(1, s // 24)
    dot_colors = [(255, 95, 87, 255), (254, 188, 46, 255), (40, 200, 64, 255)]
    dot_start_x = pad + max(3, s // 6)
    for i, color in enumerate(dot_colors):
        cx = dot_start_x + i * (dot_r * 2 + max(1, s // 16))
        draw.ellipse([cx - dot_r, dot_y - dot_r, cx + dot_r, dot_y + dot_r], fill=color)

    # Terminal prompt ">_"
    text_area_top = dot_y + dot_r + max(2, s // 8)
    text_area_bottom = s - pad - max(2, s // 8)
    prompt_color = (78, 201, 176, 255)
    cursor_color = (78, 201, 176, 200)
    prompt_size = max(8, int((text_area_bottom - text_area_top) * 0.65))

    font_paths = [
        '/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf',
        '/usr/share/fonts/truetype/liberation/LiberationMono-Bold.ttf',
    ]
    font = None
    for fp in font_paths:
        if os.path.exists(fp):
            font = ImageFont.truetype(fp, prompt_size)
            break
    if font is None:
        font = ImageFont.load_default()

    text_x = pad + max(3, s // 7)
    text_y = text_area_top + (text_area_bottom - text_area_top - prompt_size) // 2
    draw.text((text_x, text_y), ">", fill=prompt_color, font=font)

    try:
        gt_w = draw.textbbox((0, 0), ">", font=font)[2]
    except:
        gt_w = int(prompt_size * 0.6)
    draw.text((text_x + gt_w + max(1, s // 32), text_y), "_", fill=cursor_color, font=font)

    # Scanlines (retro)
    if s >= 48:
        for y in range(pad + r, s - pad - r, 3):
            draw.line([(pad + r, y), (s - pad - r, y)], fill=(255, 255, 255, 6), width=1)

    # "gd" watermark
    if s >= 48:
        gd_size = max(6, s // 8)
        try:
            gd_font = ImageFont.truetype(font.path, gd_size)
        except:
            gd_font = font
        draw.text((pad + max(3, s // 6), s - pad - gd_size - max(2, s // 16)),
                  "gd", fill=(100, 100, 110, 200), font=gd_font)

    return img


def save_as_ico(images, sizes, output_path):
    """Manually pack ICO file with multiple sizes."""
    count = len(images)
    # ICO header: reserved(2) + type(2) + count(2)
    header = struct.pack('<HHH', 0, 1, count)
    # Each entry: 16 bytes
    entry_size = 16
    data_offset = 6 + count * entry_size  # header + all entries

    entries = []
    png_datas = []
    for img, (w, h) in zip(images, sizes):
        # Save as PNG in memory
        import io
        buf = io.BytesIO()
        img.save(buf, format='PNG')
        png_data = buf.getvalue()
        png_datas.append(png_data)
        # width, height (0=256), color count, reserved, planes, bpp, size, offset
        entry = struct.pack('<BBBBHHII',
                            w if w < 256 else 0,
                            h if h < 256 else 0,
                            0, 0, 1, 32,
                            len(png_data), data_offset)
        entries.append(entry)
        data_offset += len(png_data)

    with open(output_path, 'wb') as f:
        f.write(header)
        for e in entries:
            f.write(e)
        for d in png_datas:
            f.write(d)


if __name__ == '__main__':
    out_dir = '/data/develop/dotnet/gdterm/src/Gdterm.UI/Resources'
    os.makedirs(out_dir, exist_ok=True)

    sizes = [16, 24, 32, 48, 64, 128, 256]
    images = []
    for sz in sizes:
        img = draw_icon(sz)
        images.append(img)
        img.save(os.path.join(out_dir, f'gdterm_{sz}.png'), 'PNG')
        print(f"  ✓ {sz}x{sz}")

    ico_path = os.path.join(out_dir, 'gdterm.ico')
    save_as_ico(images, [(s, s) for s in sizes], ico_path)
    print(f"  ✓ Multi-size ICO → {ico_path} ({os.path.getsize(ico_path)} bytes)")

    # Main PNG
    images[-1].save(os.path.join(out_dir, 'gdterm.png'), 'PNG')
    print(f"  ✓ 256x256 PNG → gdterm.png")
