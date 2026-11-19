"""Render a quick preview of a built .blend (validation only, not saved into the file).

Usage: blender -b <file.blend> --python render_preview.py -- <out.png> [yaw_deg] [pitch_deg]
"""
import bpy
import math
import sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
out_png = argv[0]
yaw = float(argv[1]) if len(argv) > 1 else -55.0
pitch = float(argv[2]) if len(argv) > 2 else 28.0

scn = bpy.context.scene
scn.render.engine = "BLENDER_EEVEE_NEXT"
scn.render.resolution_x = 1600
scn.render.resolution_y = 900
scn.render.film_transparent = False
try:
    scn.view_settings.view_transform = "Standard"
except Exception:
    pass

import numpy as np

pts = []
for ob in scn.objects:
    if ob.type != "MESH":
        continue
    m = ob.matrix_world
    for c in ob.bound_box:
        w = m @ Vector(c)
        pts.append((w.x, w.y, w.z))
pts = np.array(pts, dtype=np.float64)
count = len(pts) // 8

# percentile-based framing so a few giant skybox pieces cannot swallow the shot
lo = np.percentile(pts, 2.0, axis=0)
hi = np.percentile(pts, 98.0, axis=0)
center = Vector(((lo + hi) / 2.0).tolist())
radius = max(float(np.linalg.norm(hi - lo) * 0.5), 1.0)
print("BOUNDS n=%d p2=%s p98=%s center=%s radius=%.1f" % (count, lo.round(1), hi.round(1), center, radius))

cam_data = bpy.data.cameras.new("prev_cam")
cam_data.lens = 35
cam_data.clip_start = max(radius * 0.001, 0.05)
cam_data.clip_end = radius * 20.0
cam = bpy.data.objects.new("prev_cam", cam_data)
scn.collection.objects.link(cam)
scn.camera = cam

yaw_r = math.radians(yaw)
pitch_r = math.radians(pitch)
dist = radius * 1.6
cam.location = center + Vector((
    math.cos(pitch_r) * math.cos(yaw_r) * dist,
    math.cos(pitch_r) * math.sin(yaw_r) * dist,
    math.sin(pitch_r) * dist,
))
direction = center - cam.location
cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()

sun_data = bpy.data.lights.new("prev_sun", type="SUN")
sun_data.energy = 3.0
sun = bpy.data.objects.new("prev_sun", sun_data)
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
scn.collection.objects.link(sun)

scn.render.filepath = out_png
scn.render.image_settings.file_format = "PNG"
bpy.ops.render.render(write_still=True)
print("RENDER_OK " + out_png)