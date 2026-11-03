"""Render a Workbench contact sheet of the choreography for quick visual review.

blender -b assets/blender/animations/dw_reload_choreography.blend --factory-startup -P assets/tools/export/review_frames.py \
    -- --out artifacts/animation-review [--frames 0 16 24 ...]
"""
import argparse
import math
import pathlib
import sys

import bpy

parser = argparse.ArgumentParser()
parser.add_argument("--out", type=pathlib.Path, required=True)
parser.add_argument("--frames", type=int, nargs="*", default=[0, 10, 16, 24, 32, 46, 66, 92, 100, 112, 116, 126, 140, 152])
parser.add_argument("--res", type=int, default=360)
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
out = args.out.resolve()
out.mkdir(parents=True, exist_ok=True)
scene = bpy.data.scenes["DW_OffhandReload"]
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.shading.light = "STUDIO"
scene.display.shading.color_type = "MATERIAL"
scene.render.resolution_x = scene.render.resolution_y = args.res
scene.render.film_transparent = False
scene.display.shading.background_type = "VIEWPORT"
scene.display.shading.background_color = (0.55, 0.58, 0.62)
scene.camera.rotation_euler = (math.radians(72), 0, 0)
side_data = bpy.data.cameras.new("DW_ReviewSide")
side_data.lens = 30
side = bpy.data.objects.new("DW_ReviewSide", side_data)
scene.collection.objects.link(side)
side.location = (0.95, 0.22, -0.05)
side.rotation_euler = (math.radians(95), 0, math.radians(90))
cameras = {"view": scene.camera, "side": side}
paths = []
for frame in args.frames:
    scene.frame_set(frame)
    for name, camera in cameras.items():
        scene.camera = camera
        path = out / f"{name}_{frame:03d}.png"
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True, scene=scene.name)
        paths.append(path)
cols = len(args.frames)
sheet = bpy.data.images.new("sheet", args.res * cols, args.res * 2)
pixels = [0.0] * (args.res * cols * args.res * 2 * 4)
for row, name in enumerate(("side", "view")):
    for col, frame in enumerate(args.frames):
        image = bpy.data.images.load(str(out / f"{name}_{frame:03d}.png"))
        src = list(image.pixels)
        for y in range(args.res):
            start = ((row * args.res + y) * args.res * cols + col * args.res) * 4
            pixels[start:start + args.res * 4] = src[y * args.res * 4:(y + 1) * args.res * 4]
sheet.pixels = pixels
sheet.filepath_raw = str(out / "contact_sheet.png")
sheet.file_format = "PNG"
sheet.save()
print("@@JSON@@" + '{"ok": true, "sheet": "' + str(out / "contact_sheet.png").replace("\\", "/") + '"}')

