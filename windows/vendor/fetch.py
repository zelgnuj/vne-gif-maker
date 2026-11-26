#!/usr/bin/env python3
"""Downloads the Windows builds of ffmpeg and gifsicle bundled with the app (pinned + sha256-checked).
Usage: python3 windows/vendor/fetch.py      (writes ffmpeg.exe, gifsicle.exe and licenses next to this file)

ffmpeg 9.1 "essentials" by Gyan Doshi (GPLv3 build): https://github.com/GyanD/codexffmpeg/releases/tag/9.1
gifsicle 1.97 win64 by Jernej Simončič, linked from https://www.lcdf.org/gifsicle/ (GPLv2)."""
import hashlib, io, os, sys, urllib.request, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PKGS = [
    ("https://github.com/GyanD/codexffmpeg/releases/download/9.1/ffmpeg-9.1-essentials_build.zip",
     "b9bbbb3acece000cbeec994f856649354e52c773807841e78908374b24d658fd",
     {"ffmpeg-9.1-essentials_build/bin/ffmpeg.exe": "ffmpeg.exe",
      "ffmpeg-9.1-essentials_build/LICENSE": "FFMPEG-LICENSE.txt"}),
    ("https://eternallybored.org/misc/gifsicle/releases/gifsicle-1.97-win64.zip",
     "bb3ad4e1521a3953152f406eaafabda153fb5986938405860a7e7f12a3d13227",
     {"gifsicle.exe": "gifsicle.exe", "doc/COPYING": "GIFSICLE-LICENSE.txt"}),
]

for url, sha, files in PKGS:
    if all(os.path.exists(os.path.join(HERE, out)) for out in files.values()):
        continue
    print("Downloading", url, flush=True)
    req = urllib.request.Request(url, headers={"User-Agent": "vne-gif-maker-build"})
    data = urllib.request.urlopen(req, timeout=600).read()
    got = hashlib.sha256(data).hexdigest()
    if got != sha:
        sys.exit(f"sha256 mismatch for {url}: {got}")
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        for name, out in files.items():
            with open(os.path.join(HERE, out), "wb") as f:
                f.write(z.read(name))
            print("  ->", out)
print("vendor ok")
