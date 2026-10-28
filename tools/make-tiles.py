#!/usr/bin/env python3
"""Плитки Windows Phone из исходной иконки приложения.

На начальном экране плитка — это не картинка приложения, а цветной
прямоугольник системы, поверх которого лежит белый значок. Поэтому здесь из
исходной иконки вынимается только белая фигура (пузырь с машиной), а фон
отбрасывается: цвет плитке даст сама система, а подпись она нарисует поверх.

Исключение — логотип для магазина: он показывается на светлом фоне, и белым
по белому там ничего не увидишь, так что для него берётся исходная картинка
целиком.

Запуск: python3 tools/make-tiles.py
"""

import os

from PIL import Image, ImageChops

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

SOURCE = os.path.join(ROOT, "tools", "icon-source.jpg")
ASSETS = os.path.join(ROOT, "Lovit", "Assets")

# Порог белизны. Фон иконки — насыщенный синий и фиолетовый, у него самый
# тёмный канал около 60; у белой фигуры все каналы под 255. Граница между
# ними и разделяет значок и фон.
LOW = 120
HIGH = 235


def glyph():
    """Белая фигура на прозрачном фоне, обрезанная по своим краям."""
    image = Image.open(SOURCE).convert("RGB")

    # Самый тёмный канал: у белого он высокий, у любого насыщенного цвета —
    # низкий, каким бы ярким тот ни казался.
    channels = image.split()
    darkest = ImageChops.darker(ImageChops.darker(channels[0], channels[1]), channels[2])

    alpha = darkest.point(lambda value: clamp((value - LOW) * 255 // (HIGH - LOW)))

    white = Image.new("RGBA", image.size, (255, 255, 255, 0))
    white.putalpha(alpha)

    return white.crop(white.getbbox())


def clamp(value):
    if value < 0:
        return 0
    if value > 255:
        return 255
    return value


def place(mark, size, box, anchor="center"):
    """Кладёт значок в плитку заданного размера, вписывая его в долю ширины."""
    width, height = size
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))

    side = int(min(width, height) * box)
    scaled = mark.resize((side, side), Image.LANCZOS)

    if anchor == "center":
        left = (width - side) // 2
    else:
        # Широкая плитка: значок у левого края, как в системных приложениях,
        # — правую половину займёт подпись.
        left = int(height * 0.22)

    # Немного выше середины: снизу система напишет имя приложения, и по
    # оптическому центру значок иначе окажется прижатым к подписи.
    top = (height - side) // 2 - int(height * 0.04)

    canvas.paste(scaled, (left, max(top, 0)), scaled)
    return canvas


def save(image, name):
    path = os.path.join(ASSETS, name)
    image.save(path, "PNG")
    print("%s  %dx%d" % (name, image.size[0], image.size[1]))


def main():
    if not os.path.isdir(ASSETS):
        os.makedirs(ASSETS)

    mark = glyph()

    # Размеры — масштаб 240 % от базовых: именно его берут телефоны с
    # экраном 720p и выше, а на остальных система уменьшит сама.
    save(place(mark, (360, 360), 0.52), "Logo.scale-240.png")
    save(place(mark, (170, 170), 0.66), "Square71x71Logo.scale-240.png")
    save(place(mark, (106, 106), 0.70), "SmallLogo.scale-240.png")
    save(place(mark, (744, 360), 0.52, anchor="left"), "WideLogo.scale-240.png")
    save(place(mark, (1152, 1920), 0.34), "SplashScreen.scale-240.png")

    store = Image.open(SOURCE).convert("RGB").resize((120, 120), Image.LANCZOS)
    save(store, "StoreLogo.scale-240.png")


if __name__ == "__main__":
    main()
