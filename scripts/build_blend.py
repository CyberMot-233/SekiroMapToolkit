"""Build a .blend for one exported Sekiro map.

Usage:
  blender -b --python build_blend.py -- <data_dir> <out.blend>

<data_dir> holds meshes.bin, scene.json and tex/*.dds produced by SekiroMapExport.
Vertices/normals are already converted to Blender's Z-up space, object transforms
too, so nothing here needs axis juggling.
"""
import bpy
import json
import os
import struct
import sys

import numpy as np
from mathutils import Quaternion, Vector


def read_scene(data_dir):
    with open(os.path.join(data_dir, "scene.json"), "r", encoding="utf-8") as f:
        return json.load(f)


def read_meshes(bin_path):
    f = open(bin_path, "rb")
    magic = f.read(4)
    if magic != b"SKMS":
        raise RuntimeError("bad mesh bundle magic: %r" % magic)
    version, mesh_count = struct.unpack("<ii", f.read(8))
    meshes = []
    for _ in range(mesh_count):
        (nlen,) = struct.unpack("<i", f.read(4))
        name = f.read(nlen).decode("utf-8", "replace")
        (sub_count,) = struct.unpack("<i", f.read(4))
        subs = []
        for _ in range(sub_count):
            mat_idx, vcount, icount = struct.unpack("<iii", f.read(12))
            vraw = f.read(vcount * 32)
            iraw = f.read(icount * 4)
            verts = np.frombuffer(vraw, dtype=np.float32).reshape(vcount, 8)
            idx = np.frombuffer(iraw, dtype=np.uint32).astype(np.int64)
            subs.append((mat_idx, verts, idx))
        meshes.append((name, subs))
    f.close()
    return meshes


def build_mesh_object(name, subs, materials):
    me = bpy.data.meshes.new(name)
    verts = np.concatenate([s[1][:, 0:3] for s in subs], axis=0).astype(np.float32)
    nrm = np.concatenate([s[1][:, 3:6] for s in subs], axis=0).astype(np.float32)
    uv = np.concatenate([s[1][:, 6:8] for s in subs], axis=0).astype(np.float32)

    # only the materials this model actually uses become slots, so slots stay small
    used = {}
    local_mats = []
    base = 0
    tri_chunks = []
    mat_per_poly = []
    for mat_idx, v, idx in subs:
        g = mat_idx if 0 <= mat_idx < len(materials) else -1
        if g not in used:
            used[g] = len(local_mats)
            local_mats.append(materials[g] if g >= 0 else None)
        tri_chunks.append(idx.reshape(-1, 3) + base)
        mat_per_poly.append(np.full(idx.size // 3, used[g], dtype=np.int32))
        base += v.shape[0]
    tris = np.concatenate(tri_chunks, axis=0)
    mat_per_poly = np.concatenate(mat_per_poly)

    nv = verts.shape[0]
    ntri = tris.shape[0]
    me.vertices.add(nv)
    me.vertices.foreach_set("co", verts.ravel())

    loop_idx = tris.ravel()
    me.loops.add(loop_idx.size)
    me.loops.foreach_set("vertex_index", loop_idx.astype(np.int32))

    me.polygons.add(ntri)
    me.polygons.foreach_set("loop_start", np.arange(0, ntri * 3, 3, dtype=np.int32))
    me.polygons.foreach_set("material_index", mat_per_poly)
    me.update(calc_edges=False)

    uv_layer = me.uv_layers.new(name="UVMap")
    uv_flat = uv[loop_idx].ravel().astype(np.float32)
    uv_layer.data.foreach_set("uv", uv_flat)

    try:
        me.normals_split_custom_set_from_vertices(nrm.ravel())
    except Exception:
        pass

    for m in local_mats:
        me.materials.append(m)
    return me


ALPHA_HINTS = ("雲", "云", "cloud", "葉", "leaf", "藁", "wa", "紙", "簾", "rope", "縄", "草",
               "alpha", "spider", "蜘蛛", "洗濯", "cloth", "蔓", "蔦", "枝葉", "布", "衣")
BLEND_HINTS = ("雲", "云", "cloud")


# ---------------------------------------------------------------------------
# textures are shipped as opaque PNG (no alpha channel)
# ---------------------------------------------------------------------------

def write_png_rgb(path, w, h, arr):
    """arr: uint8 (h, w, 3), row order top -> bottom."""
    import struct
    import zlib
    raw = bytearray()
    for y in range(h):
        raw.append(0)
        raw += arr[y].tobytes()

    def chunk(tag, data):
        return (struct.pack(">I", len(data)) + tag + data +
                struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 6)))
        f.write(chunk(b"IEND", b""))


def alpha_bleed(rgb, alpha, threshold=0.5, rounds=8):
    """Fill texels with alpha<threshold using the average of already-valid neighbours."""
    h, w, _ = rgb.shape
    valid = alpha >= threshold
    if valid.all() or not valid.any():
        if not valid.any():
            rgb[:] = 0.5
        return rgb
    out = rgb.copy()
    filled = valid.copy()
    for _ in range(rounds):
        if filled.all():
            break
        acc = np.zeros_like(out)
        cnt = np.zeros((h, w), dtype=np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dy == 0 and dx == 0:
                    continue
                sy = slice(max(0, dy), h + min(0, dy))
                sx = slice(max(0, dx), w + min(0, dx))
                ty = slice(max(0, -dy), h + min(0, -dy))
                tx = slice(max(0, -dx), w + min(0, -dx))
                m = filled[sy, sx]
                acc[ty, tx] += out[sy, sx] * m[:, :, None]
                cnt[ty, tx] += m
        grow = (~filled) & (cnt > 0)
        if not grow.any():
            break
        out[grow] = acc[grow] / cnt[grow][:, None]
        filled |= grow
    # still-empty texels fall back to the most common opaque colour nearby
    if not filled.all():
        ref = out[valid].mean(axis=0)
        out[~filled] = ref
    return out

def srgb_encode(x):
    return np.where(x <= 0.0031308, x * 12.92, 1.055 * np.power(np.maximum(x, 0.0), 1.0 / 2.4) - 0.055)


def convert_textures(tex_dir, normal_names):
    """DDS -> opaque PNG, alpha dropped. normals stay untouched (Non-Color)."""
    done = 0
    for name in sorted(os.listdir(tex_dir)):
        if not name.lower().endswith(".dds"):
            continue
        src = os.path.join(tex_dir, name)
        dst = os.path.join(tex_dir, name[:-4] + ".png")
        non_color = name in normal_names
        img = bpy.data.images.load(src, check_existing=False)
        try:
            w, h = img.size
            if w == 0 or h == 0:
                img.reload()
                w, h = img.size
            buf = np.empty(w * h * 4, dtype=np.float32)
            img.pixels.foreach_get(buf)
            px = buf.reshape(h, w, 4)[::-1]                  # flip to top-down
            rgb = np.clip(px[:, :, :3], 0.0, 1.0)
            alpha = px[:, :, 3]
            if not non_color:
                rgb = srgb_encode(rgb)
            # alpha is dropped, so bleed opaque colour into the transparent texels,
            # otherwise cut-out folgere/paper cards turn into black shards
            rgb = alpha_bleed(rgb, alpha)
            arr = np.clip(rgb * 255.0 + 0.5, 0, 255).astype(np.uint8)
            write_png_rgb(dst, w, h, arr)
            done += 1
        finally:
            bpy.data.images.remove(img)
    for name in os.listdir(tex_dir):
        if name.lower().endswith(".dds"):
            try:
                os.remove(os.path.join(tex_dir, name))
            except OSError:
                pass
    return done


def png_name(name):
    if not name:
        return name
    return name[:-4] + ".png" if name.lower().endswith(".dds") else name


def make_material(defs, tex_dir, cache):
    key = (defs.get("albedo", ""), defs.get("normal", ""), defs.get("name", ""))
    if key in cache:
        return cache[key]

    mat = bpy.data.materials.new(defs.get("name") or "mat")
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    if bsdf is None:
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        out = nt.nodes.get("Material Output") or nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(bsdf.outputs[0], out.inputs[0])

    alb = defs.get("albedo") or ""
    nrm = defs.get("normal") or ""
    mname = (defs.get("name") or "") + " " + alb
    mname_l = mname.lower()

    if alb:
        p = os.path.join(tex_dir, png_name(alb))
        img = bpy.data.images.load(p, check_existing=True)
        img.colorspace_settings.name = "sRGB"
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = img
        tex.interpolation = "Linear"
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        if bsdf.inputs.get("Roughness"):
            bsdf.inputs["Roughness"].default_value = 0.75
        if bsdf.inputs.get("Metallic"):
            bsdf.inputs["Metallic"].default_value = 0.0
    else:
        bsdf.inputs["Base Color"].default_value = (0.35, 0.35, 0.35, 1.0)

    if nrm:
        p = os.path.join(tex_dir, png_name(nrm))
        img = bpy.data.images.load(p, check_existing=True)
        img.colorspace_settings.name = "Non-Color"
        tex = nt.nodes.new("ShaderNodeTexImage")
        tex.image = img
        nm = nt.nodes.new("ShaderNodeNormalMap")
        nt.links.new(tex.outputs["Color"], nm.inputs["Color"])
        nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])

    mat.use_backface_culling = False
    cache[key] = mat
    return mat


def wipe():
    # keep the default UI layout (3D viewport etc.) but drop the startup objects
    bpy.ops.wm.read_factory_settings(use_empty=False)
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        if me.users == 0:
            bpy.data.meshes.remove(me)


def set_viewport_material_preview():
    """Open the file showing textures: every 3D viewport gets Material Preview shading."""
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type != "VIEW_3D":
                continue
            for space in area.spaces:
                if space.type != "VIEW_3D":
                    continue
                try:
                    space.shading.type = "MATERIAL"
                    space.shading.use_scene_lights = False
                    space.shading.use_scene_world = True
                    space.clip_start = 0.1
                    space.clip_end = 100000.0
                except Exception:
                    pass


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    data_dir = argv[0]
    out_blend = argv[1]

    scene = read_scene(data_dir)
    map_name = scene.get("map", "map")
    tex_dir = os.path.join(data_dir, "tex")

    wipe()
    scn = bpy.context.scene
    scn.render.engine = "BLENDER_EEVEE_NEXT" if hasattr(bpy.types, "RenderEngine") else scn.render.engine
    try:
        scn.render.engine = "BLENDER_EEVEE_NEXT"
    except Exception:
        pass
    try:
        scn.view_settings.view_transform = "Standard"
    except Exception:
        pass

    world = bpy.data.worlds.new("SekiroWorld")
    world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs[0].default_value = (0.35, 0.37, 0.42, 1.0)
        bg.inputs[1].default_value = 1.0
    scn.world = world

    coll = bpy.data.collections.new(map_name)
    scn.collection.children.link(coll)

    # every texture becomes an opaque PNG (alpha channel removed) before wiring materials
    normal_src = set()
    for d in scene.get("materials", []):
        n = d.get("normal") or ""
        if n.lower().endswith(".dds"):
            normal_src.add(n)
        elif n:
            normal_src.add(n[:-4] + ".dds" if n.lower().endswith(".png") else n)
    if os.path.isdir(tex_dir):
        png_count = convert_textures(tex_dir, normal_src)
    else:
        png_count = 0

    mat_cache = {}
    materials = []
    for d in scene.get("materials", []):
        materials.append(make_material(d, tex_dir, mat_cache))

    meshes = read_meshes(os.path.join(data_dir, "meshes.bin"))
    data_by_index = []
    for i, (name, subs) in enumerate(meshes):
        if not subs:
            data_by_index.append(None)
            continue
        me = build_mesh_object(name, subs, materials)
        data_by_index.append(me)

    n_obj = 0
    for o in scene.get("objects", []):
        mi = o["mesh"]
        if mi < 0 or mi >= len(data_by_index):
            continue
        me = data_by_index[mi]
        if me is None:
            continue
        ob = bpy.data.objects.new(o["name"] or ("obj%d" % mi), me)
        loc = o["loc"]
        quat = o["quat"]
        sc = o["scale"]
        ob.location = Vector((loc[0], loc[1], loc[2]))
        ob.rotation_mode = "QUATERNION"
        ob.rotation_quaternion = Quaternion((quat[0], quat[1], quat[2], quat[3]))
        ob.scale = Vector((sc[0], sc[1], sc[2]))
        coll.objects.link(ob)
        n_obj += 1

    # keep textures inside the .blend so nothing is missing when it is reopened
    packed = 0
    for img in list(bpy.data.images):
        if img.source == "FILE" and not img.packed_file:
            try:
                img.pack()
                packed += 1
            except Exception:
                pass

    # remove orphan mesh datablocks that no object uses (empty model entries)
    for me in list(bpy.data.meshes):
        if me.users == 0:
            bpy.data.meshes.remove(me)

    set_viewport_material_preview()

    bpy.ops.wm.save_as_mainfile(filepath=out_blend, compress=True)
    print("BUILD_OK map=%s objects=%d meshes=%d materials=%d images=%d packed=%d png=%d"
          % (map_name, n_obj, len([m for m in data_by_index if m]), len(materials),
             len(bpy.data.images), packed, png_count))


main()



