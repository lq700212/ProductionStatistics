#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""截图后处理：清理 PrintWindow 的黑边，并把主界面统一缩到 1800 宽。

为什么要清四角：
  WPF 主窗口是圆角无边框窗，PrintWindow(flag=2) 会把圆角外的透明区域渲染成纯黑。
  图片放进文档后四角出现四个黑弧，既难看又像是截图失败。
  这里只清“从四个角连通、且接近纯黑”的像素，不碰窗口内部的深色主题背景：
  深色主题的底色是 (14,20,34)，与纯黑之间有浅色圆角边框隔开，泛洪不会越过边框。

为什么要裁对话框黑边：
  “另存为”“导出成功”这类 Win32 对话框的 PrintWindow 截图，左/右/下各带 10 px
  纯黑阴影像框；不裁掉会像贴在黑框里。只裁整行/整列 99% 近黑的外圈，内容不受影响。

为什么缩到 1800：
  150% DPI 下主窗物理尺寸 2250×1380，直接进 PDF 体积大且超过版面宽度；
  等比缩到 1800 宽后正文仍然清晰（相当于 1200 DIP），文件体积也降下来。

用法：
  python postprocess.py [图片目录]
  缺省目录：../../docs/images（相对本脚本）
"""
import os
import sys
from collections import deque

from PIL import Image

MAX_BLACK = 24          # 三通道都 <=24 视为“圆角外的纯黑”
CORNER_BOX = 64         # 泛洪限制在角点附近，避免异常图把整张图吃掉
MAX_FILL = 5000         # 单角填充超过这个数就判为异常，整张图保持原样
TARGET_WIDTH = 1800


def is_near_black(p):
    return max(p[0], p[1], p[2]) <= MAX_BLACK


def clean_corner(px, w, h, sx, sy):
    """从 (sx,sy) 泛洪，把连通的近黑像素涂白；返回填充像素数。"""
    if not is_near_black(px[sx, sy]):
        return 0
    seen = {(sx, sy)}
    q = deque([(sx, sy)])
    filled = 0
    while q:
        x, y = q.popleft()
        if not is_near_black(px[x, y]):
            continue
        px[x, y] = (255, 255, 255, 255)
        filled += 1
        for nx, ny in ((x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1)):
            if (0 <= nx < w and 0 <= ny < h and (nx, ny) not in seen
                    and abs(nx - sx) <= CORNER_BOX and abs(ny - sy) <= CORNER_BOX):
                seen.add((nx, ny))
                q.append((nx, ny))
    return filled


def trim_black_border(im):
    """裁掉 PrintWindow 给窗口加的纯黑外框。

    对话框（09 保存对话框 / 10 导出成功）的截图四周有 10 px 纯黑边，
    来自 PrintWindow 的窗口阴影；主界面大图的四角黑弧由 clean_corner 处理，
    这里不会误裁主界面（清理后四角是白色，不满足“整行/整列 99% 近黑”）。
    """
    w, h = im.size
    px = im.load()

    def is_black(p):
        return max(p[0], p[1], p[2]) <= MAX_BLACK

    def row_black(y):
        return sum(1 for x in range(w) if is_black(px[x, y])) >= w * 0.99

    def col_black(x):
        return sum(1 for y in range(h) if is_black(px[x, y])) >= h * 0.99

    limit_x = int(w * 0.15)
    limit_y = int(h * 0.15)
    left = 0
    while left < limit_x and col_black(left):
        left += 1
    right = 0
    while right < limit_x and col_black(w - 1 - right):
        right += 1
    top = 0
    while top < limit_y and row_black(top):
        top += 1
    bottom = 0
    while bottom < limit_y and row_black(h - 1 - bottom):
        bottom += 1
    if left or right or top or bottom:
        im = im.crop((left, top, w - right, h - bottom))
    return im, (left, top, right, bottom)


def process(path):
    im = Image.open(path)
    if im.mode != "RGBA":
        im = im.convert("RGBA")
    im, trimmed = trim_black_border(im)
    w, h = im.size
    work = im.copy()
    px = work.load()
    filled = 0
    if w >= 2000:  # 只处理主界面大图；保存/导出对话框没有圆角伪影
        for sx, sy in ((0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1)):
            n = clean_corner(px, w, h, sx, sy)
            if n > MAX_FILL:
                print("  %s 角点(%d,%d)填充 %d 像素异常，跳过整张" % (os.path.basename(path), sx, sy, n))
                work = im.copy()
                filled = 0
                break
            filled += n
    if work.width > TARGET_WIDTH:
        nh = int(round(work.height * TARGET_WIDTH / float(work.width)))
        work = work.resize((TARGET_WIDTH, nh), Image.LANCZOS)
    work = work.convert("RGB")
    work.save(path, "PNG", optimize=True)
    return filled, work.size, trimmed


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(here, "..", "..", "docs", "images")
    root = os.path.abspath(root)
    if not os.path.isdir(root):
        print("目录不存在: " + root)
        return 2
    files = sorted(f for f in os.listdir(root) if f.lower().endswith(".png"))
    if not files:
        print("没有 PNG: " + root)
        return 2
    for f in files:
        p = os.path.join(root, f)
        before = os.path.getsize(p)
        filled, size, trimmed = process(p)
        after = os.path.getsize(p)
        print("%-32s corner=%4d  trim=%s  %dx%d  %dKB -> %dKB"
              % (f, filled, trimmed, size[0], size[1], before // 1024, after // 1024))
    print("POSTPROCESS DONE %d files" % len(files))
    return 0


if __name__ == "__main__":
    sys.exit(main())
