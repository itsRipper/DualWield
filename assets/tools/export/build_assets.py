"""Generate the editable DualWieldLab Blender sources.

blender -b --factory-startup --python-exit-code 1 -P assets/tools/export/build_assets.py -- [--root <repo>]

Writes:
  assets/blender/rigs/dw_fps_arms.blend
  assets/blender/pistols/dw_compact_pistol.blend
  assets/blender/animations/dw_reload_choreography.blend   (scenes DW_OffhandReload, DW_MainReloadHandoff,
                                                            DW_OffhandService)
Everything is original geometry and hand-authored keys; no game asset is read.
Re-running overwrites these three files, so hand edits belong in the .blend files once
they are adopted as the source of truth (export_clips.py reads the .blend, not this script).
"""
import argparse
import json
import math
import pathlib
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import dw_assets as dw  # noqa: E402

parser = argparse.ArgumentParser()
parser.add_argument("--root", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[3])
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
ROOT = args.root.resolve()
BLEND = ROOT / "assets" / "blender"


def fresh(name):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.name = name
    dw.configure_scene(scene, 0, 0)
    return scene


def collection(scene, name):
    col = bpy.data.collections.new(name)
    scene.collection.children.link(col)
    return col


def save(path):
    path.parent.mkdir(parents=True, exist_ok=True)
    for action in bpy.data.actions:
        action.use_fake_user = True
    bpy.ops.wm.save_as_mainfile(filepath=str(path), compress=True)
    return path


def rot(axis, degrees):
    return Quaternion(Vector(axis), math.radians(degrees))


def world_pose(location, rotation):
    return Matrix.Translation(location) @ rotation.to_matrix().to_4x4()


# --- standalone rig -------------------------------------------------------------
scene = fresh("DW_FPSArms")
col = collection(scene, "DW_Rig")
rig = dw.build_arms(col, scene)
controls = collection(scene, "DW_Controls")
for side in "LR":
    target = dw.empty("ctl_palm." + side, controls, 0.04)
    target.rotation_mode = "QUATERNION"
    target.location, target.rotation_quaternion = dw.IDLE_PALM[side], dw.grip_rotation(side)
    pole = dw.empty("pole." + side, controls, 0.03, "SPHERE")
    pole.location = (0.55 if side == "R" else -0.55, 0.05, -0.55)
    ik = dw.add_ik(rig, side, target, pole, controls)
    dw.calibrate_pole(scene, rig, side, ik)
save(BLEND / "rigs" / "dw_fps_arms.blend")

# --- standalone pistol ----------------------------------------------------------
scene = fresh("DW_CompactPistol")
col = collection(scene, "DW_CompactPistol")
weapon, anchor, magazine = dw.build_pistol("DW_Pistol", col)
for side in "LR":
    marker = dw.empty("DW_Pistol_grip." + side, col, 0.02, "SINGLE_ARROW")
    marker.parent = weapon
    marker.matrix_parent_inverse = Matrix.Identity(4)
    marker.matrix_basis = dw.grip_matrix(side)
save(BLEND / "pistols" / "dw_compact_pistol.blend")

# --- choreography: Alt+R left reload with right-hand assistance ------------------
scene = fresh("DW_OffhandReload")
dw.configure_scene(scene, 0, 152)
rig_col = collection(scene, "DW_Rig")
ctl_col = collection(scene, "DW_Controls")
left_col = collection(scene, "DW_LeftPistol")
right_col = collection(scene, "DW_RightPistol")
view_col = collection(scene, "DW_View")
rig = dw.build_arms(rig_col, scene)
palm = {}
for side in "LR":
    target = dw.empty("ctl_palm." + side, ctl_col, 0.04)
    target.rotation_mode = "QUATERNION"
    target.location, target.rotation_quaternion = dw.IDLE_PALM[side], dw.grip_rotation(side)
    pole = dw.empty("pole." + side, ctl_col, 0.03, "SPHERE")
    pole.location = (0.55 if side == "R" else -0.55, 0.05, -0.55)
    ik = dw.add_ik(rig, side, target, pole, ctl_col)
    dw.calibrate_pole(scene, rig, side, ik)
    palm[side] = target
reload_ctl = dw.empty("ctl_reload", ctl_col, 0.02, "CUBE")
reload_ctl.location = (0, 0.3, 0.12)

guns = {}
for side, col in (("L", left_col), ("R", right_col)):
    weapon, anchor, magazine = dw.build_pistol(side + "_Pistol", col)
    weapon.parent = palm[side]
    weapon.matrix_parent_inverse = Matrix.Identity(4)
    weapon.matrix_basis = dw.grip_matrix(side).inverted()
    guns[side] = (weapon, anchor, magazine)
hand_mag = dw.magazine_mesh("Hand_magazine", left_col)
hand_mag.parent = palm["R"]
hand_mag.matrix_parent_inverse = Matrix.Identity(4)
dw.add_camera(scene, view_col)

# Right wrist poses in the left gun frame (origin = seated magazine anchor). Contact points
# follow the installed Glock geometry: baseplate ~0.112 below the anchor along the grip
# rake, slide rear serrations ~0.08 behind and ~0.06 above it, ~0.04 slide travel.
GRAB_ROT = dw.hand_rotation((-0.6, 0.8, 0.0), (0.0, 0.0, -1.0))
GRAB = Vector((0.032, -0.072, -0.138))
SLANT = Vector((0.0, -0.33, -0.94)).normalized()
RACK_ROT = dw.hand_rotation((-1.0, -0.15, 0.0), (0.0, 0.0, 1.0))
RACK = Vector((0.055, -0.085, 0.092))
SLIDE_TRAVEL = Vector((0.0, -0.04, 0.0))
AWAY = Vector((0.07, -0.07, -0.03))
TILT = {deg: rot((1, 0, 0), deg) @ GRAB_ROT for deg in (10, 4)}
hand_mag.matrix_basis = world_pose(GRAB, GRAB_ROT).inverted()

# Left wrist: idle and the inward presentation (muzzle up/in, magazine well toward the right hand).
L0 = (dw.IDLE_PALM["L"], dw.grip_rotation("L"))
present_rot = rot((0, 0, 1), -15) @ rot((0, 1, 0), -35) @ rot((1, 0, 0), 12)
LP = (Vector((-0.07, 0.37, -0.14)), present_rot @ dw.grip_rotation("L"))
LP_OVER = (LP[0] + Vector((0.006, 0.004, 0.006)), rot((0, 1, 0), -4) @ LP[1])
LP_HIGH = (LP[0] + Vector((0.0, 0.0, 0.012)), LP[1])
for frame, (location, rotation) in [(0, L0), (4, (L0[0] + Vector((0, 0, 0.004)), L0[1])), (19, LP_OVER), (24, LP),
                                    (60, (LP[0] + Vector((0.004, 0, 0.003)), LP[1])), (84, LP_HIGH),
                                    (100, LP), (102, (LP[0] + Vector((0, 0, -0.01)), rot((1, 0, 0), -3) @ LP[1])),
                                    (108, LP), (116, (LP[0] + Vector((0.006, -0.008, 0)), LP[1])), (124, LP),
                                    (142, L0), (152, L0)]:
    dw.key_pose(palm["L"], frame, location, rotation)

R0 = (dw.IDLE_PALM["R"], dw.grip_rotation("R"))
R_LOW = (Vector((0.20, 0.24, -0.40)), rot((1, 0, 0), -60) @ dw.grip_rotation("R"))
R_MID = (Vector((0.15, 0.33, -0.27)), rot((1, 0, 0), -25) @ dw.grip_rotation("R"))
R_FOLLOW = (Vector((0.18, 0.28, -0.34)), rot((1, 0, 0), -48) @ dw.grip_rotation("R"))
R_SETTLE = (dw.IDLE_PALM["R"] + Vector((0.0, 0.004, 0.008)), rot((1, 0, 0), 3) @ dw.grip_rotation("R"))
for frame, pose in [(0, R0), (6, R_MID), (10, R_FOLLOW), (14, R_LOW), (134, R_LOW), (136, R_LOW),
                    (144, (Vector((0.14, 0.35, -0.23)), dw.grip_rotation("R"))), (148, R_SETTLE), (152, R0)]:
    dw.key_pose(palm["R"], frame, *pose)

child = palm["R"].constraints.new("CHILD_OF")
child.name = "AttachToLeftGun"
child.target = guns["L"][0]
child.inverse_matrix = Matrix.Identity(4)
for frame, value in [(0, 0.0), (14, 0.0), (24, 1.0), (122, 1.0), (134, 0.0), (152, 0.0)]:
    child.influence = value
    palm["R"].keyframe_insert('constraints["AttachToLeftGun"].influence', frame=frame)


def gun_local(frame, location, rotation):
    scene.frame_set(frame)
    return guns["L"][0].matrix_world.inverted() @ world_pose(location, rotation)


stow = gun_local(46, Vector((0.17, 0.22, -0.42)), dw.hand_rotation((0.1, 0.3, -1.0), (1.0, 0.0, 0.0)))
dig = gun_local(54, Vector((0.16, 0.235, -0.44)), dw.hand_rotation((0.1, 0.35, -1.0), (1.0, 0.0, 0.0)))
rise = gun_local(58, Vector((0.10, 0.29, -0.33)), dw.hand_rotation((-0.3, 0.6, -0.6), (0.3, 0.0, -1.0)))
local_keys = [(24, GRAB, GRAB_ROT), (26, GRAB, GRAB_ROT), (30, GRAB + SLANT * 0.035, GRAB_ROT), (34, GRAB + SLANT * 0.08, rot((0, 1, 0), 8) @ GRAB_ROT),
              (36, GRAB + SLANT * 0.13 + Vector((0.04, 0, 0)), GRAB_ROT),
              (46, stow.to_translation(), stow.to_quaternion()), (54, dig.to_translation(), dig.to_quaternion()),
              # Approach the well tilted, straighten as the magazine enters, then seat and press.
              (58, rise.to_translation(), rise.to_quaternion()), (78, GRAB + SLANT * 0.10, TILT[10]),
              (88, GRAB + SLANT * 0.045, TILT[4]), (94, GRAB + SLANT * 0.02, GRAB_ROT), (100, GRAB, GRAB_ROT),
              (102, GRAB - SLANT * 0.005, GRAB_ROT), (104, GRAB + SLANT * 0.004, GRAB_ROT), (108, AWAY, GRAB_ROT),
              # Overhand rack: grip the rear serrations, pull the full travel, release.
              (112, RACK, RACK_ROT), (113, RACK, RACK_ROT), (116, RACK + SLIDE_TRAVEL, RACK_ROT),
              (118, RACK + SLIDE_TRAVEL + Vector((0.025, 0.005, 0.012)), RACK_ROT),
              (120, Vector((0.075, -0.09, 0.03)), GRAB_ROT), (122, AWAY, GRAB_ROT)]
for frame, location, rotation in local_keys:
    dw.key_pose(palm["R"], frame, location, rotation)

for frame, value in [(0, 1), (14, 1), (18, 0.3), (21, 0.12), (23.5, 0.5), (25, 0.9), (46, 0.85), (50, 0.6), (52, 0.85),
                     (100, 0.85), (104, 0.6), (106, 0.3), (108, 0.3), (111, 0.45), (113, 0.95), (116, 0.95), (117, 0.2),
                     (122, 0.3), (130, 0.5), (136, 1), (152, 1)]:
    dw.key_prop(palm["R"], "Rcurl", frame, value)
for prop, keys in {"Rvis": [(0, 1), (14, 0), (136, 1)], "Mseat": [(0, 1), (26, 0), (100, 1)],
                   "Mhand": [(0, 0), (26, 1), (46, 0), (52, 1), (100, 0)]}.items():
    for frame, value in keys:
        dw.key_prop(reload_ctl, prop, frame, value, constant=True)

dw.add_curl_driver(rig, "R", palm["R"])
for part in ("_frame", "_slide", "_magazine"):
    dw.visibility_driver(bpy.data.objects["R_Pistol" + part], reload_ctl, "Rvis")
dw.visibility_driver(guns["L"][2], reload_ctl, "Mseat")
dw.visibility_driver(hand_mag, reload_ctl, "Mhand")

for name, frame in [("holstered", 14), ("mag_grab", 24), ("mag_seated", 100), ("rack_start", 108),
                    ("chamber", 116), ("rack_end", 122), ("draw_start", 134)]:
    scene.timeline_markers.new(name, frame=frame)
# Native weapon handling sounds: snd:<R|L>:<WeaponSoundPlayer event>[@swap|@chamber].
# R = the right pistol's own sound bank, L = the left pistol's. "@swap"/"@chamber" play
# only after that native transaction verified; events inside a skipped rack never play.
for name, frame in [("snd:R:SndHolster", 6), ("snd:L:SndHandTouch", 23), ("snd:L:SndMagButton", 26), ("snd:L:SndMagOut", 30),
                    ("snd:L:SndMagPullIn", 44), ("snd:L:SndMagPullOut", 52), ("snd:L:SndMagIn@swap", 100),
                    ("snd:L:SndHandOn", 111), ("snd:L:SndSlideOut", 114), ("snd:L:SndSlideIn@chamber", 116),
                    ("snd:L:SndHandOff", 119), ("snd:R:SndDraw", 136)]:
    scene.timeline_markers.new(name, frame=frame)
# Segments are retimed at runtime to the offhand weapon's own native clip lengths.
scene["dw_segments"] = json.dumps([
    {"name": "holster", "start": 0, "end": 24, "native": ["idle_to_out"], "exclude": []},
    {"name": "mag_out", "start": 24, "end": 58, "native": ["mag_out"], "exclude": ["all", "idle_to", "inv_to"]},
    {"name": "mag_in", "start": 58, "end": 108, "native": ["mag_in"], "exclude": []},
    {"name": "rack", "start": 108, "end": 122, "native": ["reload_catch"], "exclude": ["to_idle"]},
    {"name": "draw", "start": 122, "end": 152, "native": ["out_to_idle"], "exclude": ["arm"]},
])
scene["dw_clip"] = "dw_offhand_reload"
scene.frame_set(0)

# --- timing curves for R: left holster -> native right reload -> left draw ----------
handoff = bpy.data.scenes.new("DW_MainReloadHandoff")
dw.configure_scene(handoff, 0, 32)
hcol = bpy.data.collections.new("DW_HandoffControls")
handoff.collection.children.link(hcol)
ctl = dw.empty("ctl_handoff", hcol, 0.05, "CUBE")
for frame, value in [(0, 0.0), (3, 0.12), (9, 1.0)]:
    dw.key_prop(ctl, "release", frame, value)
for frame, value in [(20, 0.0), (24, 0.35), (32, 1.0)]:
    dw.key_prop(ctl, "capture", frame, value)
for name, frame in [("release_end", 9), ("capture_start", 20), ("gun_show", 26), ("capture_end", 32)]:
    handoff.timeline_markers.new(name, frame=frame)
handoff["holster_rate"] = 1.35
handoff["draw_rate"] = 1.15
handoff["dw_clip"] = "dw_main_reload_handoff"


def curve_scene(name, end, curves, marks, constants):
    sc = bpy.data.scenes.new(name)
    dw.configure_scene(sc, 0, end)
    col = bpy.data.collections.new(name + "Controls")
    sc.collection.children.link(col)
    control = dw.empty("ctl_" + name, col, 0.05, "CUBE")
    for prop, keys in curves.items():
        for frame, value in keys:
            dw.key_prop(control, prop, frame, value)
    for mark, frame in marks:
        sc.timeline_markers.new(mark, frame=frame)
    for key, value in constants.items():
        sc[key] = value
    sc["dw_curves"] = json.dumps(list(curves))
    return sc


# --- left native service (examine / chamber check / malfunction clear / magazine check) ---
# The right pistol holsters and draws on the Alt+R clip (frames 0-14 and draw_start-end).
# While the left pistol's own native clip plays, the right palm eases from that holster
# pose onto the mirrored native support hand ("reach", seconds*30 from native start) and
# eases back after the native operation ("release"); "curl" closes the fingers on contact.
service = curve_scene("DW_OffhandService", 24, {
    "reach": [(0, 0.0), (3, 0.15), (9, 0.85), (12, 1.0)],
    "release": [(12, 1.0), (16, 0.75), (24, 0.0)],
    "curl": [(0, 1.0), (6, 0.35), (10, 0.55), (12, 0.85)],
}, [("reach_end", 12), ("release_start", 12), ("release_end", 24)], {"holster_rate": 1.25, "draw_rate": 1.15})
service["dw_clip"] = "dw_offhand_service"

out = save(BLEND / "animations" / "dw_reload_choreography.blend")
print("@@JSON@@" + json.dumps({"ok": True, "blender": bpy.app.version_string, "files": [
    str(BLEND / "rigs" / "dw_fps_arms.blend"), str(BLEND / "pistols" / "dw_compact_pistol.blend"), str(out)]}))
