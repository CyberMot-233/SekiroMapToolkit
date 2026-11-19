# -*- coding: utf-8 -*-
"""把网络上的只狼插画裁成 1920x1080 壁纸：cover 裁切 -> 提饱和 -> 自动曝光 -> 冷调 -> 暗角 -> JPEG。

目标：绘画/插画质感，压暗到「放文字按钮仍然清楚」的程度。
"""
import os

import numpy as np
from PIL import Image, ImageEnhance

SRC = r"E:\mapper\只狼\_bg3"
SRC2 = r"E:\mapper\只狼\_bg4"
SRC3 = r"E:\mapper\只狼\_bg5"
DST = r"E:\mapper\只狼\src\SekiroMapToolkit.Avalonia\Resources\bg"
W, H = 1920, 1080

TARGET_MEAN = 0.170     # 目标平均亮度（0-1）——压到这个数，白色标题才压得住
SATURATION = 1.14
CONTRAST = 1.05
VIGNETTE = 0.42         # 四角额外压暗强度
TINT = (0.94, 0.99, 1.10)   # 轻微冷调

JOBS = [
    ("gpv12q.png", "bg_01.jpg", 0.40),   # 红树 / 满月 / 剪影   偏上裁，保住月亮
    ("p95g9j.png", "bg_02.jpg", 0.45),   # 水墨城堡剪影
    ("d8gwem.png", "bg_03.jpg", 0.44),   # 云海群山 远僧
    ("l33k2q.jpg", "bg_19.jpg", 0.42),   # 雾中墓地 手绘
    ("mdr829.jpg", "bg_20.jpg", 0.42),   # 芦苇海 落日武士
    ("8529vj.png", "bg_04.jpg", 0.40),   # 云中白龙
    ("9od7md.png", "bg_05.jpg", 0.42),   # 落日骑马 剪影
    ("21996g.jpg", "bg_06.jpg", 0.40),   # 双人刀锋相接（动画绘）
    ("jxjvm5.png", "bg_07.jpg", 0.42),   # 落日林间
    ("q6ywxd.jpg", "bg_08.jpg", 0.44),   # 红叶对决
    ("7pm1qy.jpg", "bg_09.jpg", 0.42),   # 雷雨 海上鸟居
    ("l8o25q.png", "bg_10.jpg", 0.46),   # 芒草 金色残阳
    ("q67l85.jpg", "bg_11.jpg", 0.40),   # 巨鬼与武士
    ("yxplm7.png", "bg_12.jpg", 0.42),   # 血色剪影 拔刀
    # 第二批：再补 5 张，全是手绘 / 厚涂
    ("015xp3.jpg", "bg_13.jpg", 0.42),   # 暮色朱桥 枯树
    ("4xqdxo.jpg", "bg_14.jpg", 0.42),   # 军旗骑兵 山坡
    ("q6821q.jpg", "bg_15.jpg", 0.42),   # 少女 雨叶 特写
    ("z8w2mj.jpg", "bg_16.jpg", 0.44),   # 赤甲武者 厚涂
    ("e8v82k.png", "bg_17.jpg", 0.40),   # 落日剪影 拔刀
    ("726l8o.png", "bg_18.jpg", 0.42),   # 赤林独行
]


def cover(im, top_bias=0.42, w=W, h=H):
    im = im.convert("RGB")
    sw, sh = im.size
    scale = max(w / sw, h / sh)
    nw, nh = max(w, int(round(sw * scale))), max(h, int(round(sh * scale)))
    im = im.resize((nw, nh), Image.LANCZOS)
    left = (nw - w) // 2
    top = int((nh - h) * top_bias)
    return im.crop((left, top, left + w, top + h))


def mean_of(arr):
    lum = arr[..., 0] * 0.2126 + arr[..., 1] * 0.7152 + arr[..., 2] * 0.0722
    return float(lum.mean() / 255.0)


def vignette_mask(w=W, h=H, strength=VIGNETTE):
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    cx, cy = w / 2.0, h / 2.0
    dx = (xx - cx) / cx
    dy = (yy - cy) / cy
    r = np.sqrt(dx * dx + dy * dy) / np.sqrt(2.0)      # 0 中心 -> 1 角
    v = 1.0 - strength * np.power(np.clip(r, 0, 1), 2.1)
    return v[..., None]


MASK = vignette_mask()


def grade(im, boost=1.0):
    im = ImageEnhance.Color(im).enhance(SATURATION)
    a = np.asarray(im, dtype=np.float32)
    m = mean_of(a)
    gain = 1.0 if m <= 1e-6 else max(0.30, min(6.0, TARGET_MEAN / m)) * boost
    a = a * gain
    # 冷调
    a[..., 0] *= TINT[0]
    a[..., 1] *= TINT[1]
    a[..., 2] *= TINT[2]
    # 抬一点暗部，避免整张糊成黑块（保留形状）
    a = 255.0 * np.power(np.clip(a / 255.0, 0.0, 1.0), 0.92)
    a = a + 7.0
    # 对比（绕中灰拉）
    a = (a - 118.0) * CONTRAST + 118.0
    # 暗角
    a = a * MASK
    # 轻微压向夜蓝，避免死黑发灰
    a = a * 0.965 + np.array([5.0, 9.0, 14.0], dtype=np.float32) * 0.035
    a = np.clip(a, 0, 255).astype(np.uint8)
    return Image.fromarray(a, "RGB")


def main():
    os.makedirs(DST, exist_ok=True)
    for old in os.listdir(DST):
        if old.endswith(".jpg"):
            os.remove(os.path.join(DST, old))

    for src, out, bias in JOBS:
        p = os.path.join(SRC, src)
        if not os.path.exists(p):
            p = os.path.join(SRC2, src)
        if not os.path.exists(p):
            p = os.path.join(SRC3, src)
        if not os.path.exists(p):
            print("MISS", src)
            continue
        im = cover(Image.open(p), bias)
        before = mean_of(np.asarray(im, dtype=np.float32))
        im = grade(im)
        q = os.path.join(DST, out)
        im.save(q, "JPEG", quality=88, optimize=True, progressive=True)
        after = mean_of(np.asarray(im, dtype=np.float32))
        print("%-10s %s  mean %.3f -> %.3f  %3d KB" % (src, out, before, after, os.path.getsize(q) // 1024))


if __name__ == "__main__":
    main()
