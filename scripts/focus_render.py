"""Frame one object (or a sphere around a coordinate) and render it.

Usage:
  blender -b <file.blend> --python focus_render.py -- <out.png> <mode> <arg> [pitch] [yaw]
    mode=name  arg = substring of the object name to focus on
    mode=xyz   arg = "x,y,z" world point to orbit
"""
import bpy
import math
import sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
out_png = argv[0]
mode = argv[1]
arg = argv[2]
pitch = float(argv[3]) if len(argv) > 3 else 22.0
yaw = float(argv[4]) if len(argv) > 4 else -60.0

scn = bpy.context.scene
scn.render.engine = "BLENDER_EEVEE_NEXT"
scn.render.resolution_x = 1400
scn.render.resolution_y = 900
try:
    scn.view_settings.view_transform = "Standard"
except Exception:
    pass

if mode == "name":
    target = None
    for ob in scn.objects:
        if ob.type == "MESH" and arg.lower() in ob.name.lower():
            target = ob
            break
    if target is None:
        raise RuntimeError("no object matches " + arg)
    center = target.matrix_world.translation.copy()
    extent = max(target.dimensions)
    print("FOCUS %s loc=%s dim=%s mats=%s" % (target.name, tuple(round(v, 1) for v in center),
                                              tuple(round(v, 2) for v in target.dimensions),
                                              [m.name for m in target.data.materials][:6]))
    radius = max(extent * 0.6, 3.0)
else:
    center = Vector([float(v) for v in arg.split(",")])
    radius = 40.0

cam_data = bpy.data.cameras.new("cam")
cam_data.lens = 50
cam_data.clip_start = max(radius * 0.002, 0.02)
cam_data.clip_end = radius * 60.0
cam = bpy.data.objects.new("cam", cam_data)
scn.collection.objects.link(cam)
scn.camera = cam

p = math.radians(pitch)
y = math.radians(yaw)
dist = radius * 3.2
cam.location = center + Vector((math.cos(p) * math.cos(y) * dist,
                                math.cos(p) * math.sin(y) * dist,
                                math.sin(p) * dist))
cam.rotation_euler = (center - cam.location).to_track_quat("-Z", "Y").to_euler()

sun_data = bpy.data.lights.new("sun", type="SUN")
sun_data.energy = 3.0
sun = bpy.data.objects.new("sun", sun_data)
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
scn.collection.objects.link(sun)

scn.render.filepath = out_png
scn.render.image_settings.file_format = "PNG"
bpy.ops.render.render(write_still=True)
print("RENDER_OK " + out_png)