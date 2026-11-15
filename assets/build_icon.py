"""
DupClean 图标工程化处理（可复现）。

输入：ImageGen 生成的概念稿 PNG。
步骤：
  1) 无痕去除模型水印：圆角方块左右对称，用左下角同尺寸区域水平镜像覆盖右下角。
  2) 白底转透明：从画布边界做 flood fill，只清除与边界连通的白色，
     图标内部的白色对勾圆标被蓝色包围，不会被误删。
  3) 清除抗锯齿造成的浅色白边。
  4) 输出透明母版 app_master.png、多尺寸 app.ico 与尺寸预览图。
"""
from collections import deque
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
SRC = HERE / "Flat_modern_desktop_applicatio_2026-11-04T17-20-08.png"
MASTER = HERE / "app_master.png"
ICO = HERE.parent / "src" / "app.ico"
PREVIEW = HERE.parent / "release" / "图标预览.png"

W = H = 1024


def is_whiteish(c, loose=False):
    r, g, b = c[0], c[1], c[2]
    if loose:
        return r > 188 and g > 200 and b > 212 and (max(r, g, b) - min(r, g, b)) < 70
    return r >= 238 and g >= 238 and b >= 238


def main():
    im = Image.open(SRC).convert("RGBA")
    assert im.size == (W, H), f"unexpected size {im.size}"

    # 1) 水印位于右下角 x>=880, y>=945；取对称的左下角水平镜像覆盖
    patch = im.crop((0, 945, 144, H)).transpose(Image.Transpose.FLIP_LEFT_RIGHT)
    im.paste(patch, (W - 144, 945))

    px = im.load()

    # 2) 从四条边界出发，清除连通的白色背景
    seen = bytearray(W * H)
    q = deque()
    for x in range(W):
        q.append((x, 0)); q.append((x, H - 1))
    for y in range(H):
        q.append((0, y)); q.append((W - 1, y))
    while q:
        x, y = q.popleft()
        if x < 0 or x >= W or y < 0 or y >= H:
            continue
        i = y * W + x
        if seen[i]:
            continue
        seen[i] = 1
        c = px[x, y]
        if not is_whiteish(c, loose=True):
            continue
        px[x, y] = (c[0], c[1], c[2], 0)
        q.append((x + 1, y)); q.append((x - 1, y))
        q.append((x, y + 1)); q.append((x, y - 1))

    # 3) 与透明区相邻的浅色残留（抗锯齿白边）再清一轮
    for _ in range(2):
        targets = []
        for y in range(H):
            for x in range(W):
                c = px[x, y]
                if c[3] == 0 or not is_whiteish(c, loose=True):
                    continue
                near_transparent = any(
                    0 <= nx < W and 0 <= ny < H and px[nx, ny][3] == 0
                    for nx, ny in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1))
                )
                if near_transparent:
                    targets.append((x, y, c))
        if not targets:
            break
        for x, y, c in targets:
            px[x, y] = (c[0], c[1], c[2], 0)

    im.save(MASTER)

    # 4) 小尺寸增强：把绿色对勾适当加粗，避免 16/24/32 缩成一粒灰点。
    #    只加粗对勾本身，构图与配色保持不变，大尺寸仍使用原始母版。
    src_px = im.load()
    green_mask = Image.new("L", (W, H), 0)
    gm = green_mask.load()
    for y in range(H):
        for x in range(W):
            r, g, b, a = src_px[x, y]
            if a and g > 110 and g > r + 40 and g > b + 30:
                gm[x, y] = 255
    bold_mask = green_mask.filter(ImageFilter.MaxFilter(9))
    bold = im.copy()
    bold.paste((39, 165, 103, 255), (0, 0, W, H), bold_mask)

    # 5) 多尺寸 ICO：Pillow 的 append_images 允许为每个尺寸单独提供图像
    ICO.parent.mkdir(parents=True, exist_ok=True)
    sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    small = [bold.resize((s, s), Image.LANCZOS) for s in (16, 24, 32)]
    frames = {16: small[0], 24: small[1], 32: small[2]}
    im.save(ICO, format="ICO", sizes=sizes, append_images=small)

    # 6) 预览图：直接展示 ICO 各尺寸条目的真实内容
    PREVIEW.parent.mkdir(parents=True, exist_ok=True)
    canvas = Image.new("RGB", (760, 340), (247, 249, 252))
    d = ImageDraw.Draw(canvas)
    x = 30
    for s in (256, 48, 32, 16):
        thumb = frames.get(s) or im.resize((s, s), Image.LANCZOS)
        y = (340 - s) // 2
        canvas.paste(thumb, (x, y), thumb)
        d.text((x, 300), f"{s}x{s}", fill=(60, 60, 60))
        x += s + 60
    canvas.save(PREVIEW)

    opaque = sum(1 for y in range(H) for x in range(W) if px[x, y][3] > 0)
    print(f"MASTER {MASTER}")
    print(f"ICO    {ICO} sizes={[s[0] for s in sizes]}")
    print(f"PREVIEW {PREVIEW}")
    print(f"opaque_ratio={opaque / (W * H):.3f} corner_alpha={px[2, 2][3]} center_alpha={px[W // 2, H // 2][3]}")


if __name__ == "__main__":
    main()
