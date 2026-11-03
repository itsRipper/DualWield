"""Bake the choreography .blend into runtime clips loaded by DualWieldLab.dll.

blender -b assets/blender/animations/dw_reload_choreography.blend --factory-startup --python-exit-code 1 \
    -P assets/tools/export/export_clips.py -- [--out assets/exports/unity]

Every frame of each scene is evaluated (constraints, IK, drivers) and converted to Unity's
left-handed spaces with explicit basis changes:
  look space  Blender (x right, y forward, z up) -> Unity (x right, y up, z forward)
  gun space   Blender (x right, y muzzle, z top) -> EFT weapon node (x right, -y muzzle, z top)
Rotations are exported as deltas (M R M) so they are applied on top of live native poses;
no Blender bone-axis convention reaches the game.
"""
import argparse
import json
import pathlib
import sys

import bpy
from mathutils import Matrix

parser = argparse.ArgumentParser()
parser.add_argument("--out", type=pathlib.Path, default=pathlib.Path(__file__).resolve().parents[2] / "exports" / "unity")
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
OUT = args.out.resolve()
OUT.mkdir(parents=True, exist_ok=True)

LOOK = Matrix(((1, 0, 0), (0, 0, 1), (0, 1, 0)))
GUN = Matrix(((1, 0, 0), (0, -1, 0), (0, 0, 1)))


def vec(basis, v):
    return [round(c, 6) for c in basis @ v]


def quat(basis, r):
    q = (basis @ r @ basis).to_quaternion().normalized()
    return [round(q.x, 6), round(q.y, 6), round(q.z, 6), round(q.w, 6)]


def rotation(m):
    return m.to_3x3().normalized()


def markers(scene):
    return {m.name: m.frame for m in sorted(scene.timeline_markers, key=lambda m: m.frame)}


def header(scene):
    return {"name": scene["dw_clip"], "fps": scene.render.fps, "start": scene.frame_start,
            "frames": scene.frame_end - scene.frame_start + 1, "markers": markers(scene),
            "source": bpy.path.basename(bpy.data.filepath) + "#" + scene.name,
            "blender": bpy.app.version_string}


def export_offhand(scene):
    ob = bpy.data.objects
    left, right, gun = ob["ctl_palm.L"], ob["ctl_palm.R"], ob["L_Pistol_weapon"]
    anchor, right_gun, ctl = ob["L_Pistol_mod_magazine"], ob["R_Pistol_weapon"], ob["ctl_reload"]
    seated, held = ob["L_Pistol_magazine"], ob["Hand_magazine"]
    attach = right.constraints["AttachToLeftGun"]
    channels = {k: [] for k in ("L.pos", "L.rot", "R.pos", "R.rot", "G.pos", "G.rot", "G.w",
                                "M.pos", "M.rot", "R.curl", "R.vis", "M.vis")}
    scene.frame_set(scene.frame_start)
    l0, r0 = left.matrix_world.copy(), right.matrix_world.copy()
    hold0 = rotation(right_gun.matrix_world).inverted() @ rotation(r0)
    seated0 = rotation(gun.matrix_world).inverted() @ rotation(seated.matrix_world)
    for frame in range(scene.frame_start, scene.frame_end + 1):
        scene.frame_set(frame)
        lw, rw, gw = left.matrix_world, right.matrix_world, gun.matrix_world
        gun_rot = rotation(gw)
        origin = anchor.matrix_world.to_translation()
        channels["L.pos"] += vec(LOOK, lw.to_translation() - l0.to_translation())
        channels["L.rot"] += quat(LOOK, rotation(lw) @ rotation(l0).inverted())
        channels["R.pos"] += vec(LOOK, rw.to_translation() - r0.to_translation())
        channels["R.rot"] += quat(LOOK, rotation(rw) @ rotation(r0).inverted())
        channels["G.pos"] += vec(GUN, gun_rot.inverted() @ (rw.to_translation() - origin))
        channels["G.rot"] += quat(GUN, gun_rot.inverted() @ rotation(rw) @ hold0.inverted())
        channels["G.w"].append(round(attach.influence, 6))
        in_hand = ctl["Mhand"] >= 0.5
        mag = held.matrix_world if in_hand else seated.matrix_world
        channels["M.pos"] += vec(GUN, gun_rot.inverted() @ (mag.to_translation() - origin))
        channels["M.rot"] += quat(GUN, gun_rot.inverted() @ rotation(mag) @ seated0.inverted())
        channels["R.curl"].append(round(float(right["Rcurl"]), 6))
        channels["R.vis"].append(1.0 if ctl["Rvis"] >= 0.5 else 0.0)
        channels["M.vis"].append(1.0 if ctl["Mhand"] >= 0.5 or ctl["Mseat"] >= 0.5 else 0.0)
    clip = header(scene)
    clip["segments"] = json.loads(scene["dw_segments"])
    clip["channels"] = channels
    return clip


def prop_curve(ob, prop):
    from bpy_extras import anim_utils
    anim = ob.animation_data
    bag = anim_utils.action_get_channelbag_for_slot(anim.action, anim.action_slot)
    return next(f for f in bag.fcurves if f.data_path == f'["{prop}"]')


def export_curves(scene, control, names, constants):
    # Not the active scene in background mode, so evaluate its F-curves directly.
    ctl = bpy.data.objects[control]
    curves = {name: prop_curve(ctl, name) for name in names}
    channels = {name: [round(min(1.0, max(0.0, curve.evaluate(frame))), 6)
                       for frame in range(scene.frame_start, scene.frame_end + 1)]
                for name, curve in curves.items()}
    clip = header(scene)
    clip["constants"] = {key: float(scene[key]) for key in constants}
    clip["channels"] = channels
    return clip


def export_handoff(scene):
    return export_curves(scene, "ctl_handoff", ("release", "capture"), ("holster_rate", "draw_rate"))


def export_named_curves(scene):
    constants = [key for key in scene.keys() if key not in ("dw_clip", "dw_curves") and isinstance(scene[key], (int, float))]
    return export_curves(scene, "ctl_" + scene.name, json.loads(scene["dw_curves"]), constants)


written = []
for scene, export in ((bpy.data.scenes["DW_OffhandReload"], export_offhand),
                      (bpy.data.scenes["DW_MainReloadHandoff"], export_handoff),
                      (bpy.data.scenes["DW_OffhandService"], export_named_curves)):
    clip = export(scene)
    path = OUT / (clip["name"] + ".json")
    path.write_text(json.dumps(clip, separators=(",", ":")), encoding="utf-8")
    written.append({"file": str(path), "bytes": path.stat().st_size, "frames": clip["frames"],
                    "markers": clip["markers"]})
print("@@JSON@@" + json.dumps({"ok": True, "clips": written}))
