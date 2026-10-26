#!/usr/bin/env python3
"""Convert source audio to conservative PCM WAV files Unity's WAV decoder can read.

FFmpeg may preserve RIFF LIST/INFO metadata chunks. Although valid RIFF, those
chunks have triggered empty AudioClips in the game's Unity decoder on this mod's
runtime path. Rewriting through Python's wave module emits a minimal RIFF file
with only the PCM fmt and data chunks.
"""
import os
import struct
import subprocess
import sys
import wave
from pathlib import Path
from imageio_ffmpeg import get_ffmpeg_exe

audio_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Audio")
ffmpeg = get_ffmpeg_exe()
sources = sorted(audio_dir.glob("*.mp3"))
if not sources:
    raise RuntimeError(f"No MP3 source audio files found in {audio_dir}")


def canonicalize_pcm_wav(path: Path) -> None:
    """Rewrite a WAV as header-minimal, uncompressed 16-bit PCM."""
    temporary = path.with_name(path.stem + ".canonical.tmp.wav")
    try:
        with wave.open(str(path), "rb") as source:
            params = source.getparams()
            if params.comptype != "NONE":
                raise RuntimeError(f"{path.name}: compressed WAV is not supported")
            if params.sampwidth != 2:
                raise RuntimeError(f"{path.name}: expected PCM16, got {params.sampwidth * 8}-bit samples")
            if params.framerate != 44100:
                raise RuntimeError(f"{path.name}: expected 44100 Hz, got {params.framerate} Hz")
            if params.nchannels not in (1, 2):
                raise RuntimeError(f"{path.name}: expected mono/stereo, got {params.nchannels} channels")
            frames = source.readframes(params.nframes)

        with wave.open(str(temporary), "wb") as destination:
            destination.setnchannels(params.nchannels)
            destination.setsampwidth(2)
            destination.setframerate(44100)
            destination.writeframes(frames)

        data = temporary.read_bytes()
        if len(data) < 44 or data[:4] != b"RIFF" or data[8:12] != b"WAVE":
            raise RuntimeError(f"{path.name}: canonical output has invalid RIFF/WAVE header")
        if struct.unpack_from("<I", data, 4)[0] != len(data) - 8:
            raise RuntimeError(f"{path.name}: RIFF size does not match file size")

        # This parser deliberately accepts only fmt and data: no LIST/INFO,
        # JUNK, BEXT, or other optional chunks are allowed in packaged audio.
        offset = 12
        chunks = []
        while offset + 8 <= len(data):
            chunk_id = data[offset:offset + 4]
            chunk_size = struct.unpack_from("<I", data, offset + 4)[0]
            offset += 8
            if offset + chunk_size > len(data):
                raise RuntimeError(f"{path.name}: truncated {chunk_id!r} chunk")
            chunks.append(chunk_id)
            offset += chunk_size + (chunk_size & 1)
        if offset != len(data) or chunks != [b"fmt ", b"data"]:
            raise RuntimeError(f"{path.name}: expected only fmt/data chunks, got {chunks!r}")

        os.replace(temporary, path)
        # Re-open the final file with the standard decoder, independent of the writer.
        with wave.open(str(path), "rb") as check:
            if check.getnframes() <= 0 or check.getnframes() != params.nframes:
                raise RuntimeError(f"{path.name}: frame-count mismatch after canonicalization")
        print(
            f"Canonical PCM WAV: {path.name} ({params.nchannels} ch, "
            f"44100 Hz, PCM16, {params.nframes} frames, no metadata chunks)"
        )
    finally:
        if temporary.exists():
            temporary.unlink()


for source in sources:
    output = source.with_suffix(".wav")
    command = [
        ffmpeg, "-hide_banner", "-loglevel", "error", "-y",
        "-i", str(source), "-vn", "-c:a", "pcm_s16le", "-ar", "44100",
        str(output)
    ]
    result = subprocess.run(command, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(f"FFmpeg could not convert {source.name}: {result.stderr}")
    if not output.exists() or output.stat().st_size < 2048:
        raise RuntimeError(f"Converted WAV is missing or too small: {output}")

# Normalize generated files and any WAV shipped in the source folder, including
# Blaster_Impact.wav which is already PCM and would otherwise retain its LIST chunk.
wav_files = sorted(audio_dir.glob("*.wav"))
if not wav_files:
    raise RuntimeError(f"No WAV files found after conversion in {audio_dir}")
for wav_path in wav_files:
    canonicalize_pcm_wav(wav_path)

print(f"Created/normalized {len(wav_files)} Unity-friendly PCM WAV files in {audio_dir}")
