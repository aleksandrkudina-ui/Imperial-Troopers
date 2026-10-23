#!/usr/bin/env python3
"""Retarget selected Quaternius Universal Animation Library clips to the supplied Mixamo Stormtrooper skeleton.

Input animation source is the CC0 Quaternius Universal Animation Library Standard FBX.
No Unity Editor is required. Uses assimp-py to read FBX animation keys and emits ITAN runtime clips.
"""
import math
import struct
import sys
from pathlib import Path
import assimp_py

FLAGS = (
    assimp_py.Process_Triangulate
    | assimp_py.Process_GenSmoothNormals
    | assimp_py.Process_LimitBoneWeights
    | assimp_py.Process_MakeLeftHanded
    | assimp_py.Process_FlipWindingOrder
    | assimp_py.Process_ValidateDataStructure
)

target_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Stormtrooper.fbx")
source_path = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("UAL1_Standard.fbx")
out_path = Path(sys.argv[3]) if len(sys.argv) > 3 else Path("Stormtrooper.ianim")

target_scene = assimp_py.import_file(str(target_path), FLAGS)
source_scene = assimp_py.import_file(str(source_path), FLAGS)
if not getattr(source_scene, "animations", None):
    raise RuntimeError("Animation source FBX contains no skeletal clips.")
if not getattr(target_scene, "root_node", None):
    raise RuntimeError("Stormtrooper FBX has no root node.")

def flatten(node, parent=-1, result=None):
    if result is None:
        result = []
    index = len(result)
    matrix = tuple(tuple(float(v) for v in row) for row in node.transformation)
    result.append({"name": str(node.name), "parent": parent, "matrix": matrix})
    for child in node.children:
        flatten(child, index, result)
    return result

def quat_from_rot(m):
    tr = m[0][0] + m[1][1] + m[2][2]
    if tr > 0:
        s = math.sqrt(max(1e-12, tr + 1.0)) * 2
        w = 0.25 * s
        x = (m[2][1] - m[1][2]) / s
        y = (m[0][2] - m[2][0]) / s
        z = (m[1][0] - m[0][1]) / s
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(max(1e-12, 1 + m[0][0] - m[1][1] - m[2][2])) * 2
        w = (m[2][1] - m[1][2]) / s
        x = 0.25 * s
        y = (m[0][1] + m[1][0]) / s
        z = (m[0][2] + m[2][0]) / s
    elif m[1][1] > m[2][2]:
        s = math.sqrt(max(1e-12, 1 + m[1][1] - m[0][0] - m[2][2])) * 2
        w = (m[0][2] - m[2][0]) / s
        x = (m[0][1] + m[1][0]) / s
        y = 0.25 * s
        z = (m[1][2] + m[2][1]) / s
    else:
        s = math.sqrt(max(1e-12, 1 + m[2][2] - m[0][0] - m[1][1])) * 2
        w = (m[1][0] - m[0][1]) / s
        x = (m[0][2] + m[2][0]) / s
        y = (m[1][2] + m[2][1]) / s
        z = 0.25 * s
    length = math.sqrt(x*x + y*y + z*z + w*w) or 1.0
    return (x/length, y/length, z/length, w/length)

def decompose(matrix):
    cols = [[matrix[r][c] for r in range(3)] for c in range(3)]
    scale = [math.sqrt(sum(v*v for v in col)) for col in cols]
    scale = [v if v > 1e-8 else 1.0 for v in scale]
    det = (
        matrix[0][0] * (matrix[1][1]*matrix[2][2] - matrix[1][2]*matrix[2][1])
        - matrix[0][1] * (matrix[1][0]*matrix[2][2] - matrix[1][2]*matrix[2][0])
        + matrix[0][2] * (matrix[1][0]*matrix[2][1] - matrix[1][1]*matrix[2][0])
    )
    if det < 0:
        scale[0] *= -1
    rot = tuple(tuple(matrix[r][c] / scale[c] for c in range(3)) for r in range(3))
    pos = (matrix[0][3], matrix[1][3], matrix[2][3])
    return pos, quat_from_rot(rot), tuple(scale)

def normalized_name(value):
    name = str(value or "").split("|")[-1].split(":")[-1]
    if "_$AssimpFbx$_" in name:
        name = name.split("_$AssimpFbx$_", 1)[0]
    return "".join(ch.lower() for ch in name if ch.isalnum())

# Semantic names allow Unreal/Quaternius humanoid tracks to drive the Mixamo bones.
ALIASES = {
    "hips": {"hips", "pelvis"},
    "spine": {"spine", "spine01"},
    "spine1": {"spine02", "spine2"},
    "spine2": {"spine03", "spine3", "chest", "upperchest"},
    "neck": {"neck", "neck01", "neck1"},
    "head": {"head"},
    "leftshoulder": {"leftshoulder", "shoulderl", "claviclel", "leftclavicle"},
    "rightshoulder": {"rightshoulder", "shoulderr", "clavicler", "rightclavicle"},
    "leftarm": {"leftarm", "leftupperarm", "upperarml", "lupperarm"},
    "rightarm": {"rightarm", "rightupperarm", "upperarmr", "rupperarm"},
    "leftforearm": {"leftforearm", "leftlowerarm", "lowerarml", "forearml", "lelbow"},
    "rightforearm": {"rightforearm", "rightlowerarm", "lowerarmr", "forearmr", "relbow"},
    "lefthand": {"lefthand", "handl", "lhand"},
    "righthand": {"righthand", "handr", "rhand"},
    "leftupleg": {"leftupleg", "leftupperleg", "leftthigh", "thighl", "upperlegl"},
    "rightupleg": {"rightupleg", "rightupperleg", "rightthigh", "thighr", "upperlegr"},
    "leftleg": {"leftleg", "leftlowerleg", "leftcalf", "calfl", "lowerlegl", "shinl"},
    "rightleg": {"rightleg", "rightlowerleg", "rightcalf", "calfr", "lowerlegr", "shinr"},
    "leftfoot": {"leftfoot", "footl", "lfoot"},
    "rightfoot": {"rightfoot", "footr", "rfoot"},
    "lefttoebase": {"lefttoebase", "balll", "toel"},
    "righttoebase": {"righttoebase", "ballr", "toer"},
}

def semantic(name):
    norm = normalized_name(name)
    for key, names in ALIASES.items():
        if norm in names or norm == key:
            return key
    # A source can number the spine hierarchy with leading zeros.
    aliases = {
        "spine001": "spine", "spine002": "spine1", "spine003": "spine2",
        "upperarml": "leftarm", "upperarmr": "rightarm",
        "lowerarml": "leftforearm", "lowerarmr": "rightforearm",
        "thighl": "leftupleg", "thighr": "rightupleg",
        "calfl": "leftleg", "calfr": "rightleg",
        "footl": "leftfoot", "footr": "rightfoot",
        "balll": "lefttoebase", "ballr": "righttoebase",
    }
    return aliases.get(norm)

target_nodes = flatten(target_scene.root_node)
source_nodes = flatten(source_scene.root_node)
target_bones = {}
source_bones = {}
for node in target_nodes:
    if "_$AssimpFbx$_" in node["name"]:
        continue
    key = semantic(node["name"])
    if key and key not in target_bones:
        pos, rot, scale = decompose(node["matrix"])
        target_bones[key] = dict(node=node, pos=pos, rot=rot, scale=scale)
for node in source_nodes:
    if "_$AssimpFbx$_" in node["name"]:
        continue
    key = semantic(node["name"])
    if key and key not in source_bones:
        pos, rot, scale = decompose(node["matrix"])
        source_bones[key] = dict(node=node, pos=pos, rot=rot, scale=scale)

def qnorm(q):
    length = math.sqrt(sum(v*v for v in q)) or 1.0
    return tuple(v/length for v in q)

def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw*bx + ax*bw + ay*bz - az*by,
        aw*by - ax*bz + ay*bw + az*bx,
        aw*bz + ax*by - ay*bx + az*bw,
        aw*bw - ax*bx - ay*by - az*bz,
    )

def qinv(q):
    x, y, z, w = q
    d = x*x + y*y + z*z + w*w
    return (-x/d, -y/d, -z/d, w/d) if d > 1e-12 else (0, 0, 0, 1)

def qslerp(a, b, t):
    dot = sum(a[i]*b[i] for i in range(4))
    if dot < 0:
        b = tuple(-v for v in b)
        dot = -dot
    if dot > 0.9995:
        return qnorm(tuple(a[i] + t*(b[i]-a[i]) for i in range(4)))
    theta = math.acos(max(-1.0, min(1.0, dot)))
    sin_theta = math.sin(theta)
    if abs(sin_theta) < 1e-8:
        return a
    aa = math.sin((1-t)*theta) / sin_theta
    bb = math.sin(t*theta) / sin_theta
    return qnorm(tuple(aa*a[i] + bb*b[i] for i in range(4)))

def vec_lerp(a, b, t):
    return tuple(a[i] + t*(b[i]-a[i]) for i in range(len(a)))

def flat_values(values):
    return [float(v) for v in values]

def grouped(values, size):
    vals = flat_values(values)
    return [tuple(vals[i:i+size]) for i in range(0, len(vals), size) if len(vals[i:i+size]) == size]

def seconds_for_ticks(ticks, rate):
    return float(ticks) / float(rate) if rate and float(rate) > 1e-8 else float(ticks)

def sample_vec(times, values, t, fallback):
    if not times or not values:
        return fallback
    if t <= times[0]:
        return values[0]
    if t >= times[-1]:
        return values[-1]
    for i in range(len(times)-1):
        if times[i] <= t <= times[i+1]:
            span = times[i+1] - times[i]
            f = 0.0 if span <= 1e-8 else (t-times[i])/span
            return vec_lerp(values[i], values[i+1], f)
    return values[-1]

def sample_quat(times, values, t, fallback):
    if not times or not values:
        return fallback
    if t <= times[0]:
        return values[0]
    if t >= times[-1]:
        return values[-1]
    for i in range(len(times)-1):
        if times[i] <= t <= times[i+1]:
            span = times[i+1] - times[i]
            f = 0.0 if span <= 1e-8 else (t-times[i])/span
            return qslerp(values[i], values[i+1], f)
    return values[-1]

def channel_semantic(channel):
    return semantic(getattr(channel, "node_name", getattr(channel, "name", "")))

def clip_label(name):
    norm = "".join(ch.lower() for ch in str(name or "") if ch.isalnum())
    # Detect weapon-specific clips before generic "idle", otherwise Pistol_Idle_Loop
    # would incorrectly replace the standing idle cycle.
    if any(token in norm for token in ("pistolaim", "rifleaim", "gunidle", "rifleidle", "aimidle")) or norm in ("aim", "pistolaime", "pistolaidlelo"):
        return "aim"
    if any(token in norm for token in ("pistolshoot", "rifleshoot", "gunshoot", "shoot", "fire")):
        return "fire"
    choices = [
        ("death", ("death01", "deathloop", "death", "die")),
        ("walk", ("walkloop", "walkfwdloop", "walkforwardloop", "walkfwd", "walk")),
        ("jog", ("jogfwdloop", "jogloop", "runfwdloop", "runloop", "jogfwd", "jog", "run", "sprint")),
        ("idle", ("idleloop", "idle")),
    ]
    for label, tokens in choices:
        if any(norm == token for token in tokens):
            return label
    for label, tokens in choices:
        if any(token in norm for token in tokens):
            return label
    return None

def choose_clips(animations):
    candidates = {}
    for anim in animations:
        name = str(getattr(anim, "name", "") or "")
        label = clip_label(name)
        duration = seconds_for_ticks(getattr(anim, "duration", 0), getattr(anim, "ticks_per_second", 0))
        if label and duration >= 0.15:
            score = 0
            normalized = "".join(ch.lower() for ch in name if ch.isalnum())
            if label == "idle" and "loop" in normalized: score += 10
            if label == "walk" and "loop" in normalized: score += 10
            if label == "jog" and "fwd" in normalized: score += 10
            if label == "jog" and "loop" in normalized: score += 5
            if label == "death" and "01" in normalized: score += 5
            if label not in candidates or score > candidates[label][0]:
                candidates[label] = (score, anim, duration)
    required = ("idle", "walk", "jog", "death")
    missing = [label for label in required if label not in candidates]
    if missing:
        available = [str(getattr(a, "name", "")) + " (" + str(round(seconds_for_ticks(getattr(a,"duration",0), getattr(a,"ticks_per_second",0)), 3)) + "s)" for a in animations]
        raise RuntimeError("Could not find required usable clips " + ", ".join(missing) + ". Source clips: " + "; ".join(available))
    return candidates

selected = choose_clips(source_scene.animations)
print("UAL animation source: " + str(source_path))
print("Target mapped bones: " + ", ".join(sorted(target_bones.keys())))
print("Source mapped bones: " + ", ".join(sorted(source_bones.keys())))
if len(set(target_bones.keys()) & set(source_bones.keys())) < 12:
    raise RuntimeError("Too few common semantic bones for retargeting; source rig mapping needs review.")

# Sample clips at 30 Hz and retarget local rotation deltas from the source bind pose to target bind pose.
out_clips = []
for label in ("idle", "walk", "jog", "death", "aim", "fire"):
    if label not in selected:
        continue
    _, anim, duration = selected[label]
    rate = float(getattr(anim, "ticks_per_second", 0) or 1.0)
    num_frames = max(2, min(600, int(math.ceil(duration * 30.0)) + 1))
    frame_times = [min(duration, i / 30.0) for i in range(num_frames)]
    channels = {}
    for channel in anim.channels:
        key = channel_semantic(channel)
        if key:
            channels[key] = channel
    tracks = []
    for key, target in target_bones.items():
        source = source_bones.get(key)
        channel = channels.get(key)
        if source is None or channel is None:
            continue
        pos_times = [seconds_for_ticks(v, rate) for v in flat_values(getattr(channel, "position_key_times", []))]
        pos_values = grouped(getattr(channel, "position_key_values", []), 3)
        rot_times = [seconds_for_ticks(v, rate) for v in flat_values(getattr(channel, "rotation_key_times", []))]
        rot_values = [qnorm(q) for q in grouped(getattr(channel, "rotation_key_values", []), 4)]
        bind_source_pos = source["pos"]
        bind_source_rot = source["rot"]
        bind_target_pos = target["pos"]
        bind_target_rot = target["rot"]
        keys = []
        for t in frame_times:
            source_rot = sample_quat(rot_times, rot_values, t, bind_source_rot)
            delta = qmul(qinv(bind_source_rot), source_rot)
            target_rot = qnorm(qmul(bind_target_rot, delta))
            # Preserve target skeleton bone lengths and prevent clip root-motion from moving the NPC capsule.
            target_pos = bind_target_pos
            target_scale = target["scale"]
            keys.append((t, target_pos, target_rot, target_scale))
        tracks.append((target["node"]["name"], keys))
    if len(tracks) < 12:
        raise RuntimeError("Clip " + label + " has only " + str(len(tracks)) + " mapped tracks; retargeting is unsafe.")
    out_clips.append((label, duration, tracks))
    print("Retargeted clip " + label + " from '" + str(getattr(anim, "name", "")) + "': " + str(round(duration, 2)) + " s, " + str(len(tracks)) + " bone tracks.")

out_path.parent.mkdir(parents=True, exist_ok=True)
with out_path.open("wb") as stream:
    stream.write(b"ITAN")
    stream.write(struct.pack("<ii", 1, len(out_clips)))
    for label, duration, tracks in out_clips:
        name = label.encode("utf-8")
        stream.write(struct.pack("<i", len(name)))
        stream.write(name)
        stream.write(struct.pack("<fi", float(duration), len(tracks)))
        for node_name, keys in tracks:
            encoded = node_name.encode("utf-8")
            stream.write(struct.pack("<ii", len(encoded), len(keys)))
            stream.write(encoded)
            for t, pos, rot, scale in keys:
                stream.write(struct.pack("<f3f4f3f", float(t), *pos, *rot, *scale))
print("Wrote " + str(out_path) + " (" + str(out_path.stat().st_size) + " bytes, " + str(len(out_clips)) + " clips)")
