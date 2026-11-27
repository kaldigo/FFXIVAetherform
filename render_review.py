"""Render geometry-derived shading and wireframes without a game or Blender."""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

from reference_surface import unit


def panel(image, triangles, groups, rect, view, bounds, wire=False):
    offset_x, offset_y, width, height = rect
    layer = Image.new("RGB", (width, height), "#18202a")
    draw = ImageDraw.Draw(layer)
    x0, y0 = 0, 0
    ax = 0 if view == "front" else 2
    depth_axis = 2 if view == "front" else 0
    xmin, xmax, ymin, ymax = bounds
    p = triangles
    selected = ((p[:, :, 1].max(1) >= ymin) & (p[:, :, 1].min(1) <= ymax) &
                (p[:, :, ax].max(1) >= xmin) & (p[:, :, ax].min(1) <= xmax))
    p, groups = p[selected], groups[selected]
    n = unit(np.cross(p[:, 1]-p[:, 0], p[:, 2]-p[:, 0]))
    light = unit(np.array([-.4, .6, .8]) if view == "front" else np.array([.8, .6, .4]))
    brightness = .35 + .65*np.maximum(0, n@light)
    scale = min(width/(xmax-xmin), height/(ymax-ymin))
    xy = p[:, :, [ax, 1]].copy()
    xy[:, :, 0] = x0 + width/2 + (xy[:, :, 0]-(xmin+xmax)/2)*scale
    xy[:, :, 1] = y0 + height/2 - (xy[:, :, 1]-(ymin+ymax)/2)*scale
    for i in np.argsort(p[:, :, depth_axis].mean(1)):
        base = np.array([99, 177, 200]) if "skin" in groups[i] else np.array([215, 177, 108])
        color = tuple(np.clip(base*brightness[i], 0, 255).astype(int))
        poly = [tuple(q) for q in xy[i]]
        draw.polygon(poly, fill=color)
        if wire:
            draw.line(poly+[poly[0]], fill=tuple(int(c*.60) for c in color), width=1)
    image.paste(layer, (offset_x, offset_y))


def render(folder):
    folder = Path(folder)
    d = np.load(folder/"vanilla-reference.npz")
    original = d["sourcePosition"]
    flat, curved = d["flatPosition"][d["triangles"]], d["position"][d["triangles"]]
    im = Image.new("RGB", (1920, 1360), "#18202a")
    draw = ImageDraw.Draw(im)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 24)
    except OSError:
        font = ImageFont.load_default()
    variants = [(original, d["sourceGroups"], "Original vanilla"),
                (flat, d["group"], "Flat split control"), (curved, d["group"], "Normals become geometry")]
    for col, (p, group, title) in enumerate(variants):
        draw.text((col*640+30, 18), title, fill="white", font=font)
        for row, view in enumerate(("front", "side")):
            bounds = (-195, 195, 840, 1448) if view == "front" else (-160, 180, 840, 1448)
            panel(im, p, group, (col*640+15, row*610+65, 610, 585), view, bounds, True)
    draw.text((25, 1320), "Geometry shading and wireframes | Blue: skin | Gold: stock coverage | Tail flap removed | millimetres", fill="white", font=font)
    im.save(folder/"torso-comparison.png")
    im = Image.new("RGB", (1500, 1500), "#18202a"); draw = ImageDraw.Draw(im)
    for col, view in enumerate(("front", "side")):
        draw.text((col*750+30, 20), "Curved vanilla body - "+view, fill="white", font=font)
        bounds = (-490, 490, 135, 1460)
        panel(im, curved, d["group"], (col*750+15, 65, 720, 1370), view, bounds, True)
    draw.text((25, 1450), "Stock body and smallclothes envelope; no hidden naked-body reconstruction", fill="white", font=font)
    im.save(folder/"body-review.png")
    print(folder/"torso-comparison.png")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("folder", type=Path)
    render(parser.parse_args().folder)
