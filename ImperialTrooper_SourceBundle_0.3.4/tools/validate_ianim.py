#!/usr/bin/env python3
"""Semantic validation for the custom ITAN skeletal-animation format.

This is an offline data-integrity test, not proof that Unity will render the
animation correctly in a live Risk of Rain 2 session.
"""
from __future__ import annotations

import math
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

EXPECTED_CLIPS = {"idle", "walk", "jog", "death", "aim", "fire"}
MAX_CLIPS = 16
MAX_TRACKS = 256
MAX_KEYS = 2000
MAX_STRING_BYTES = 4096


class ValidationError(RuntimeError):
    pass


@dataclass
class Key:
    time: float
    position: tuple[float, float, float]
    rotation: tuple[float, float, float, float]
    scale: tuple[float, float, float]


@dataclass
class Track:
    name: str
    keys: list[Key]


@dataclass
class Clip:
    name: str
    duration: float
    tracks: list[Track]


class Reader:
    def __init__(self, data: bytes) -> None:
        self.data = data
        self.offset = 0

    def read(self, size: int) -> bytes:
        if size < 0 or self.offset + size > len(self.data):
            raise ValidationError("unexpected end of animation file")
        result = self.data[self.offset : self.offset + size]
        self.offset += size
        return result

    def i32(self) -> int:
        return struct.unpack("<i", self.read(4))[0]

    def f32(self) -> float:
        return struct.unpack("<f", self.read(4))[0]

    def string(self, label: str) -> str:
        length = self.i32()
        if length < 0 or length > MAX_STRING_BYTES:
            raise ValidationError(f"invalid {label} string length: {length}")
        try:
            return self.read(length).decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ValidationError(f"{label} is not valid UTF-8") from exc


def finite(values: tuple[float, ...], label: str) -> None:
    if not all(math.isfinite(v) for v in values):
        raise ValidationError(f"{label} contains NaN or Infinity")


def parse(path: Path) -> list[Clip]:
    data = path.read_bytes()
    r = Reader(data)
    if r.read(4) != b"ITAN":
        raise ValidationError("bad ITAN magic header")
    version = r.i32()
    if version != 1:
        raise ValidationError(f"unsupported ITAN version {version}")
    clip_count = r.i32()
    if not 1 <= clip_count <= MAX_CLIPS:
        raise ValidationError(f"invalid clip count: {clip_count}")

    clips: list[Clip] = []
    for _ in range(clip_count):
        name = r.string("clip name")
        duration = r.f32()
        finite((duration,), f"{name} duration")
        if not name or duration <= 0.0 or duration > 120.0:
            raise ValidationError(f"invalid duration/name for clip {name!r}")
        track_count = r.i32()
        if not 1 <= track_count <= MAX_TRACKS:
            raise ValidationError(f"{name}: invalid track count {track_count}")

        tracks: list[Track] = []
        names: set[str] = set()
        for _ in range(track_count):
            track_name = r.string("track name")
            if not track_name or track_name in names:
                raise ValidationError(f"{name}: empty or duplicate track {track_name!r}")
            names.add(track_name)
            key_count = r.i32()
            if not 2 <= key_count <= MAX_KEYS:
                raise ValidationError(f"{name}/{track_name}: invalid key count {key_count}")

            keys: list[Key] = []
            previous_time = -1.0
            for _ in range(key_count):
                t = r.f32()
                px, py, pz = struct.unpack("<3f", r.read(12))
                qx, qy, qz, qw = struct.unpack("<4f", r.read(16))
                sx, sy, sz = struct.unpack("<3f", r.read(12))
                finite((t, px, py, pz, qx, qy, qz, qw, sx, sy, sz), f"{name}/{track_name} key")
                if t < 0.0 or t > duration + 0.05:
                    raise ValidationError(f"{name}/{track_name}: key time {t} outside clip")
                if t < previous_time:
                    raise ValidationError(f"{name}/{track_name}: keys are not time-sorted")
                previous_time = t
                q_length = math.sqrt(qx * qx + qy * qy + qz * qz + qw * qw)
                if not 0.5 <= q_length <= 1.5:
                    raise ValidationError(f"{name}/{track_name}: invalid quaternion length {q_length:.3f}")
                keys.append(Key(t, (px, py, pz), (qx/q_length, qy/q_length, qz/q_length, qw/q_length), (sx, sy, sz)))
            tracks.append(Track(track_name, keys))
        clips.append(Clip(name, duration, tracks))

    if r.offset != len(data):
        raise ValidationError(f"{len(data) - r.offset} trailing bytes after final clip")
    names = [c.name.lower() for c in clips]
    if len(set(names)) != len(names):
        raise ValidationError("duplicate clip names")
    missing = EXPECTED_CLIPS - set(names)
    if missing:
        raise ValidationError("missing required clips: " + ", ".join(sorted(missing)))
    return clips


def max_rotation_angle(track: Track) -> float:
    q0 = track.keys[0].rotation
    largest = 0.0
    for key in track.keys[1:]:
        dot = abs(sum(a * b for a, b in zip(q0, key.rotation)))
        dot = max(0.0, min(1.0, dot))
        largest = max(largest, 2.0 * math.acos(dot))
    return largest


def validate_motion(clips: list[Clip]) -> None:
    by_name = {clip.name.lower(): clip for clip in clips}
    # Motion checks prevent an animation file with correct metadata but static
    # or empty pose data from passing the CI gate.
    minimum_varied_tracks = {
        "idle": 5,
        "walk": 8,
        "jog": 8,
        "death": 8,
        "aim": 4,
        "fire": 4,
    }
    leg_fragments = ("leftupleg", "rightupleg", "leftleg", "rightleg", "leftupperleg", "rightupperleg")
    arm_fragments = ("leftarm", "rightarm", "leftforearm", "rightforearm", "lefthand", "righthand")

    for name in sorted(EXPECTED_CLIPS):
        clip = by_name[name]
        motions = {track.name.lower(): max_rotation_angle(track) for track in clip.tracks}
        moved = sum(angle > 0.02 for angle in motions.values())
        if moved < minimum_varied_tracks[name]:
            raise ValidationError(
                f"{name}: only {moved} tracks contain meaningful rotation; "
                f"expected at least {minimum_varied_tracks[name]}"
            )
        if name in ("walk", "jog"):
            moved_legs = sum(
                angle > 0.20 and any(fragment in track_name for fragment in leg_fragments)
                for track_name, angle in motions.items()
            )
            if moved_legs < 2:
                raise ValidationError(f"{name}: fewer than two leg tracks have a gait")
        if name in ("aim", "fire"):
            moved_arms = sum(
                angle > 0.03 and any(fragment in track_name for fragment in arm_fragments)
                for track_name, angle in motions.items()
            )
            if moved_arms < 2:
                raise ValidationError(f"{name}: upper-body/arm pose has insufficient motion")
        if name == "death":
            if not any("hips" in track_name and angle > 0.25 for track_name, angle in motions.items()):
                raise ValidationError("death: no meaningful hip/fall movement detected")
        print(
            f"Validated clip {clip.name}: {clip.duration:.3f}s, "
            f"{len(clip.tracks)} tracks, {moved} tracks with rotational movement"
        )


def main() -> int:
    if len(sys.argv) != 2:
        print("Usage: validate_ianim.py <Stormtrooper.ianim>", file=sys.stderr)
        return 2
    path = Path(sys.argv[1])
    if not path.is_file():
        print(f"ERROR: animation file not found: {path}", file=sys.stderr)
        return 2
    try:
        clips = parse(path)
        validate_motion(clips)
    except (OSError, ValidationError, struct.error) as exc:
        print(f"ERROR: animation validation failed: {exc}", file=sys.stderr)
        return 1
    print(f"PASS: all {len(clips)} required clips are structurally valid and contain nontrivial movement.")
    print("NOTE: this is offline data validation, not a substitute for in-game animation verification.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
