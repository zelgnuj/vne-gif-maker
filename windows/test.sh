#!/bin/bash
# Runs the core self-test on the Mac: same C# code as the Windows app, with Homebrew ffmpeg/gifsicle,
# plus a check that the filter graphs match the Swift app's Edit.swift byte for byte.
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
OUT="$(mktemp -d)"
swiftc -O -o "$OUT/parity" ../Sources/VNEGifMaker/Edit.swift tests/VneGif.SelfTest/parity/main.swift 2>/dev/null
"$OUT/parity" > "$OUT/parity.txt"
export SWIFT_PARITY="$OUT/parity.txt"
export VNE_FFMPEG="${VNE_FFMPEG:-$(command -v ffmpeg)}" VNE_GIFSICLE="${VNE_GIFSICLE:-$(command -v gifsicle)}"
"$DOTNET" run -c Release --project tests/VneGif.SelfTest
