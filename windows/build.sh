#!/bin/bash
# Builds the Windows app on the Mac (no Windows machine needed):
#   windows/build.sh [version]     version defaults to Resources/Info.plist (same as the macOS app)
# Output in windows/dist:
#   VnExpress-GIF-Maker-Windows-Setup.exe    installer (per-user, updates itself)
#   VnExpress-GIF-Maker-Windows-Portable.zip unzip-and-run folder
# Needs: .NET 8 SDK (~/.dotnet or PATH), makensis (brew install makensis), python3.
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="${DOTNET:-$(command -v dotnet || echo "$HOME/.dotnet/dotnet")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
V="${1:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' ../Resources/Info.plist)}"
NAME="VnExpress GIF Maker"

python3 vendor/fetch.py >/dev/null
rm -rf src/VneGif.App/bin/Release
"$DOTNET" build src/VneGif.App -c Release -p:Version="$V" -nologo -v quiet -clp:ErrorsOnly
BIN=src/VneGif.App/bin/Release/net48

STAGE="$(mktemp -d)/$NAME"
mkdir -p "$STAGE"
cp "$BIN/VnExpressGifMaker.exe" "$BIN/VnExpressGifMaker.exe.config" "$BIN/VneGif.Core.dll" "$STAGE/"
cp vendor/ffmpeg.exe vendor/gifsicle.exe vendor/FFMPEG-LICENSE.txt vendor/GIFSICLE-LICENSE.txt "$STAGE/"
# UTF-8 with BOM so every Notepad shows the Vietnamese text correctly.
printf '\xEF\xBB\xBF' > "$STAGE/HUONG-DAN.txt"; cat installer/HUONG-DAN.txt >> "$STAGE/HUONG-DAN.txt"

mkdir -p dist
rm -f dist/VnExpress-GIF-Maker-Windows-Portable.zip dist/VnExpress-GIF-Maker-Windows-Setup.exe
(cd "$(dirname "$STAGE")" && python3 - "$NAME" "$OLDPWD/dist/VnExpress-GIF-Maker-Windows-Portable.zip" <<'PY'
import os, sys, zipfile
root, out = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for d, _, files in os.walk(root):
        for f in sorted(files):
            z.write(os.path.join(d, f))
PY
)
LC_ALL=en_US.UTF-8 makensis -INPUTCHARSET UTF8 -V2 -DVERSION="$V" -DSTAGE="$STAGE" -DOUTFILE="$PWD/dist/VnExpress-GIF-Maker-Windows-Setup.exe" installer/setup.nsi
rm -rf "$(dirname "$STAGE")"
echo "Windows $V:"
ls -lh dist | awk 'NR>1 {print "  dist/" $NF " (" $5 ")"}'
