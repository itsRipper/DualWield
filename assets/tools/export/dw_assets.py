"""Shared builders for the DualWieldLab Blender sources.

Scene convention (all DualWieldLab .blend files):
  origin  = midpoint between the two upper-arm heads (the runtime "hands look" origin)
  +X right, +Y look forward, +Z up, metres, 30 fps.
Pistol convention: the `*_weapon` empty is the gun frame (+X right side of the gun,
+Y muzzle, +Z top) with its origin at the seated magazine anchor (`*_mod_magazine`).
Hand controls: local +Y points from wrist to knuckles, local +Z is the back of the hand.
"""
import math
import bmesh
import bpy
from mathutils import Matrix, Quaternion, Vector

FPS = 30
UPPER, LOWER = 0.2965, 0.251
SHOULDER_X = 0.19
IDLE_PALM = {"L": Vector((-0.12, 0.40, -0.17)), "R": Vector((0.12, 0.40, -0.17))}

# Side profile (Y forward, Z up) of an original compact polymer pistol, origin at the
# top-centre of the magazine. Roughly Glock-sized; deliberately not a scan of any model.
FRAME_PROFILE = [(-0.080, 0.025), (0.092, 0.025), (0.092, 0.008), (0.062, 0.008),
                 (0.060, -0.026), (0.022, -0.030), (0.020, -0.010), (-0.013, -0.102),
                 (-0.053, -0.102), (-0.068, -0.002), (-0.080, 0.010)]
SLIDE_PROFILE = [(-0.093, 0.023), (0.094, 0.023), (0.094, 0.062), (0.088, 0.066),
                 (-0.086, 0.066), (-0.093, 0.060)]
MAG_PROFILE = [(0.018, 0.0), (-0.018, 0.0), (-0.052, -0.098), (-0.056, -0.112),
                (-0.012, -0.112), (-0.014, -0.098)]
# Wrist (hand control) position in the gun frame while holding the pistol grip. Measured
# from the installed Glock prefab: weapon_R_hand_marker relative to mod_magazine is
# (+0.041, 0.121 behind, 0.037 below) in EFT weapon axes; the left grip mirrors it.
GRIP_IN_GUN = {"R": Vector((0.041, -0.121, -0.037)), "L": Vector((-0.041, -0.121, -0.037))}


def hand_rotation(fingers, back):
    y = Vector(fingers).normalized()
    z = Vector(back)
    z = (z - y * z.dot(y)).normalized()
    x = y.cross(z)
    return Matrix((x, y, z)).transposed().to_quaternion()


def grip_rotation(side):
    return hand_rotation((0.0, 0.94, 0.34), (1.0 if side == "R" else -1.0, 0.0, 0.0))


def grip_matrix(side):
    return Matrix.Translation(GRIP_IN_GUN[side]) @ grip_rotation(side).to_matrix().to_4x4()


def material(name, color, roughness=0.6, metallic=0.0):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = color
    bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = color
        bsdf.inputs["Roughness"].default_value = roughness
        bsdf.inputs["Metallic"].default_value = metallic
    return mat


def _finish(bm, me, mat):
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    for poly in me.polygons:
        poly.use_smooth = False
    uv = me.uv_layers.new(name="UVMap")
    for poly in me.polygons:
        # Box projection on the dominant normal axis (no degenerate UVs on any side).
        axis = max(range(3), key=lambda i: abs(poly.normal[i]))
        u_axis, v_axis = [(1, 2), (0, 2), (0, 1)][axis]
        for index in poly.loop_indices:
            co = me.vertices[me.loops[index].vertex_index].co
            uv.data[index].uv = (co[u_axis] * 4.0 + 0.5, co[v_axis] * 4.0 + 0.5)


def extruded(name, profile, width, mat, collection, x_offset=0.0):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    verts = [bm.verts.new((x_offset - width / 2, y, z)) for y, z in profile]
    face = bm.faces.new(verts)
    extrusion = bmesh.ops.extrude_face_region(bm, geom=[face])
    moved = [g for g in extrusion["geom"] if isinstance(g, bmesh.types.BMVert)]
    bmesh.ops.translate(bm, vec=(width, 0, 0), verts=moved)
    _finish(bm, me, mat)
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    return ob


def tube(name, a, b, radius, mat, collection, segments=12):
    me = bpy.data.meshes.new(name)
    bm = bmesh.new()
    direction = (b - a).normalized()
    u = direction.cross(Vector((0, 0, 1)))
    if u.length < 0.01:
        u = direction.cross(Vector((1, 0, 0)))
    u.normalize()
    v = direction.cross(u).normalized()
    rings = []
    for t, scale in ((0.0, 0.55), (0.06, 0.9), (0.2, 1.0), (0.8, 1.0), (0.94, 0.9), (1.0, 0.55)):
        centre = a.lerp(b, t)
        rings.append([bm.verts.new(centre + (u * math.cos(i * 2 * math.pi / segments)
                                             + v * math.sin(i * 2 * math.pi / segments)) * radius * scale)
                      for i in range(segments)])
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])
    for r0, r1 in zip(rings, rings[1:]):
        for i in range(segments):
            j = (i + 1) % segments
            bm.faces.new((r0[i], r0[j], r1[j], r1[i]))
    _finish(bm, me, mat)
    for poly in me.polygons:
        poly.use_smooth = True
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    return ob


def empty(name, collection, size=0.03, kind="ARROWS"):
    ob = bpy.data.objects.new(name, None)
    ob.empty_display_type = kind
    ob.empty_display_size = size
    collection.objects.link(ob)
    return ob


def build_pistol(prefix, collection, with_magazine=True):
    polymer = material("M_DW_Polymer", (0.045, 0.047, 0.05, 1.0), 0.75)
    steel = material("M_DW_Slide", (0.085, 0.088, 0.092, 1.0), 0.35, 0.8)
    mag_mat = material("M_DW_Magazine", (0.03, 0.03, 0.032, 1.0), 0.65)
    weapon = empty(prefix + "_weapon", collection, 0.05)
    frame = extruded(prefix + "_frame", FRAME_PROFILE, 0.029, polymer, collection)
    slide = extruded(prefix + "_slide", SLIDE_PROFILE, 0.0254, steel, collection)
    anchor = empty(prefix + "_mod_magazine", collection, 0.02, "PLAIN_AXES")
    for ob in (frame, slide, anchor):
        ob.parent = weapon
    magazine = None
    if with_magazine:
        magazine = magazine_mesh(prefix + "_magazine", collection, mag_mat)
        magazine.parent = anchor
    return weapon, anchor, magazine


def magazine_mesh(name, collection, mat=None):
    mat = mat or material("M_DW_Magazine", (0.03, 0.03, 0.032, 1.0), 0.65)
    body = extruded(name, MAG_PROFILE, 0.022, mat, collection)
    return body


def solve_elbow(shoulder, wrist, side):
    delta = wrist - shoulder
    reach = min(delta.length, (UPPER + LOWER) * 0.97)
    direction = delta.normalized()
    along = (UPPER * UPPER - LOWER * LOWER + reach * reach) / (2 * reach)
    height = math.sqrt(max(0.0, UPPER * UPPER - along * along))
    pole = Vector((-0.9 if side == "L" else 0.9, -0.2, -0.6))
    bend = (pole - direction * pole.dot(direction)).normalized()
    return shoulder + direction * along + bend * height


def build_arms(collection, scene):
    sleeve = material("M_DW_Sleeve", (0.16, 0.17, 0.12, 1.0), 0.85)
    glove = material("M_DW_Glove", (0.05, 0.05, 0.045, 1.0), 0.7)
    data = bpy.data.armatures.new("DW_FPSArms")
    rig = bpy.data.objects.new("DW_FPSArms", data)
    collection.objects.link(rig)
    view_layer = scene.view_layers[0]
    view_layer.objects.active = rig
    with bpy.context.temp_override(scene=scene, view_layer=view_layer, active_object=rig, object=rig):
        bpy.ops.object.mode_set(mode="EDIT")
        for side, sx in (("L", -1.0), ("R", 1.0)):
            shoulder = Vector((sx * SHOULDER_X, 0.0, 0.0))
            wrist = IDLE_PALM[side].copy()
            elbow = solve_elbow(shoulder, wrist, side)
            back = grip_rotation(side) @ Vector((0, 0, 1))
            upper = data.edit_bones.new("upperarm." + side)
            upper.head, upper.tail = shoulder, elbow
            fore = data.edit_bones.new("forearm." + side)
            fore.head, fore.tail, fore.parent, fore.use_connect = elbow, wrist, upper, True
            palm = data.edit_bones.new("palm." + side)
            palm.head = wrist
            palm.tail = wrist + grip_rotation(side) @ Vector((0, 0.085, 0))
            palm.parent = fore
            palm.align_roll(back)
            for bone in (upper, fore):
                bone.align_roll(Vector((0, 0, 1)))
            for finger, offset, length in (("fingers", (0.0, 0.085, 0.0), (0.045, 0.035)),
                                           ("thumb", (-sx * 0.025, 0.03, -0.015), (0.03, 0.028))):
                parent = palm
                head = wrist + grip_rotation(side) @ Vector(offset)
                for i, seg in enumerate(length, 1):
                    bone = data.edit_bones.new(f"{finger}.{i}.{side}")
                    bone.head = head
                    bone.tail = head + grip_rotation(side) @ Vector((0, seg, 0))
                    bone.parent = parent
                    bone.use_connect = i > 1
                    bone.align_roll(back)
                    parent, head = bone, bone.tail
        bpy.ops.object.mode_set(mode="OBJECT")
    for bone in data.bones:
        name = bone.name
        side = name[-1]
        head = rig.matrix_world @ bone.head_local
        tail = rig.matrix_world @ bone.tail_local
        radius = {"upperarm": 0.045, "forearm": 0.036, "palm": 0.03}.get(name.split(".")[0], 0.011)
        ob = tube("DW_Arm_" + name, head, tail, radius, glove if name.split(".")[0] in ("palm", "fingers", "thumb") else sleeve, collection)
        ob.parent = rig
        group = ob.vertex_groups.new(name=name)
        group.add(list(range(len(ob.data.vertices))), 1.0, "REPLACE")
        mod = ob.modifiers.new("Armature", "ARMATURE")
        mod.object = rig
    return rig


def add_ik(rig, side, target, pole, collection):
    pb = rig.pose.bones["forearm." + side]
    ik = pb.constraints.new("IK")
    ik.target, ik.pole_target, ik.chain_count = target, pole, 2
    rot = rig.pose.bones["palm." + side].constraints.new("COPY_ROTATION")
    rot.target = target
    return ik


def calibrate_pole(scene, rig, side, ik):
    expected = solve_elbow(Vector(((-1 if side == "L" else 1) * SHOULDER_X, 0, 0)), IDLE_PALM[side], side)
    best = (1e9, 0.0)
    for degrees in range(-180, 180, 5):
        ik.pole_angle = math.radians(degrees)
        bpy.context.view_layer.update() if bpy.context.view_layer else scene.view_layers[0].update()
        scene.frame_set(scene.frame_current)
        elbow = rig.matrix_world @ rig.pose.bones["forearm." + side].head
        best = min(best, ((elbow - expected).length, degrees))
    ik.pole_angle = math.radians(best[1])
    scene.frame_set(scene.frame_current)
    return best


def add_curl_driver(rig, side, control, prop="Rcurl", amount=1.25):
    for name in ("fingers.1", "fingers.2", "thumb.1", "thumb.2"):
        pb = rig.pose.bones[f"{name}.{side}"]
        pb.rotation_mode = "XYZ"
        fcurve = pb.driver_add("rotation_euler", 0)
        driver = fcurve.driver
        driver.type = "SCRIPTED"
        var = driver.variables.new()
        var.name = "c"
        var.targets[0].id = control
        var.targets[0].data_path = f'["{prop}"]'
        scale = amount * (0.6 if name.startswith("thumb") else 1.0)
        driver.expression = f"c*{scale:.3f}"


def visibility_driver(ob, control, prop):
    for path in ("hide_viewport", "hide_render"):
        fcurve = ob.driver_add(path)
        driver = fcurve.driver
        driver.type = "SCRIPTED"
        var = driver.variables.new()
        var.name = "v"
        var.targets[0].id = control
        var.targets[0].data_path = f'["{prop}"]'
        driver.expression = "v<0.5"


def key_pose(ob, frame, location, rotation):
    ob.location = location
    ob.rotation_quaternion = rotation
    ob.keyframe_insert("location", frame=frame)
    ob.keyframe_insert("rotation_quaternion", frame=frame)


def key_prop(ob, prop, frame, value, constant=False):
    ob[prop] = float(value)
    ob.keyframe_insert(f'["{prop}"]', frame=frame)
    if constant:
        set_constant(ob, f'["{prop}"]')


def set_constant(owner, data_path):
    anim = owner.animation_data
    if not anim or not anim.action:
        return
    from bpy_extras import anim_utils
    channelbag = anim_utils.action_get_channelbag_for_slot(anim.action, anim.action_slot)
    for fcurve in channelbag.fcurves:
        if fcurve.data_path == data_path:
            for point in fcurve.keyframe_points:
                point.interpolation = "CONSTANT"


def configure_scene(scene, start, end):
    scene.render.fps = FPS
    scene.frame_start, scene.frame_end = start, end
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0


def add_camera(scene, collection):
    data = bpy.data.cameras.new("DW_ViewCamera")
    data.lens_unit = "FOV"
    data.angle = math.radians(80)
    data.clip_start = 0.01
    cam = bpy.data.objects.new("DW_ViewCamera", data)
    collection.objects.link(cam)
    cam.location = (0.0, -0.04, 0.16)
    cam.rotation_euler = (math.radians(90), 0, 0)
    scene.camera = cam
    light_data = bpy.data.lights.new("DW_Key", "AREA")
    light_data.energy = 60
    light = bpy.data.objects.new("DW_Key", light_data)
    collection.objects.link(light)
    light.location = (0.4, -0.3, 0.6)
    return cam
