#!/usr/bin/env python3
"""Convert bundled MP3 source sounds to PCM WAV so Unity decodes them reliably at runtime."""
import subprocess
import sys
from pathlib import Path
from imageio_ffmpeg import get_ffmpeg_exe

audio_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("Audio")
ffmpeg = get_ffmpeg_exe()
sources = sorted(audio_dir.glob("*.mp3"))
if not sources:
    raise RuntimeError(f"No MP3 source audio files found in {audio_dir}")

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
    print(f"Converted {source.name} -> {output.name} ({output.stat().st_size} bytes)")
print(f"Created {len(sources)} Unity-compatible PCM WAV files in {audio_dir}")
