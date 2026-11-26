#!/bin/bash
# Build "VnExpress GIF Maker.app" into ./build.
#   --install  copy to /Applications
#   --dist     also make dist/VnExpress-GIF-Maker.dmg (to share) and .zip (for the in-app updater)
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/VNEGifMaker"

NAME="VnExpress GIF Maker"
APP="build/$NAME.app"

# App icon (regenerated when the tool or the mark changes).
if [ ! -f Resources/AppIcon.icns ] || [ tools/make-icon.swift -nt Resources/AppIcon.icns ] \
   || [ Sources/VNEGifMaker/BrandMark.swift -nt Resources/AppIcon.icns ]; then
  mkdir -p .build
  swiftc -O -parse-as-library -o .build/make-icon tools/make-icon.swift Sources/VNEGifMaker/BrandMark.swift
  ICONSET="$(mktemp -d)/AppIcon.iconset"
  .build/make-icon "$ICONSET" >/dev/null
  iconutil -c icns "$ICONSET" -o Resources/AppIcon.icns
fi
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/VNEGifMaker"
strip -x "$APP/Contents/MacOS/VNEGifMaker" 2>/dev/null || true
cp Resources/Info.plist "$APP/Contents/Info.plist"
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/"

# Bundled ffmpeg (see Vendor/README.md). Signed first, then the app.
if [ -x Vendor/ffmpeg ]; then
  cp Vendor/ffmpeg "$APP/Contents/Resources/ffmpeg"
  cp Vendor/FFMPEG-LICENSE.txt "$APP/Contents/Resources/"
  codesign --force --sign - --options runtime "$APP/Contents/Resources/ffmpeg" >/dev/null
else
  echo "warning: Vendor/ffmpeg missing — app will need Homebrew ffmpeg" >&2
fi
if [ -x Vendor/gifsicle ]; then
  cp Vendor/gifsicle "$APP/Contents/Resources/gifsicle"
  cp Vendor/GIFSICLE-LICENSE.txt "$APP/Contents/Resources/"
  codesign --force --sign - --options runtime "$APP/Contents/Resources/gifsicle" >/dev/null
fi
xattr -cr "$APP"
codesign --force --sign - "$APP" >/dev/null

echo "Built: $APP ($(du -sh "$APP" | cut -f1))"

for arg in "$@"; do
  case "$arg" in
    --install)
      rm -rf "/Applications/VNE GIF Maker.app" "/Applications/$NAME.app"   # old name too
      cp -R "$APP" /Applications/
      echo "Installed to /Applications/$NAME.app"
      ;;
    --dist)
      mkdir -p dist
      STAGE="$(mktemp -d)/$NAME"
      mkdir -p "$STAGE"
      cp -R "$APP" "$STAGE/"
      cp "Resources/Cách mở lần đầu.txt" "$STAGE/"
      rm -f dist/VnExpress-GIF-Maker.zip
      ditto -c -k --sequesterRsrc --keepParent "$STAGE" dist/VnExpress-GIF-Maker.zip
      rm -rf "$(dirname "$STAGE")"
      echo "Dist: dist/VnExpress-GIF-Maker.zip ($(du -h dist/VnExpress-GIF-Maker.zip | cut -f1))"
      # Disk image for people: open it, drag the app onto Applications.
      DMGSTAGE="$(mktemp -d)/$NAME"
      mkdir -p "$DMGSTAGE"
      cp -R "$APP" "$DMGSTAGE/"
      cp "Resources/Cách mở lần đầu.txt" "$DMGSTAGE/"
      ln -s /Applications "$DMGSTAGE/Applications"
      rm -f dist/VnExpress-GIF-Maker.dmg
      hdiutil create -quiet -volname "$NAME" -srcfolder "$DMGSTAGE" -fs HFS+ -format UDZO -ov dist/VnExpress-GIF-Maker.dmg
      rm -rf "$(dirname "$DMGSTAGE")"
      echo "Dist: dist/VnExpress-GIF-Maker.dmg ($(du -h dist/VnExpress-GIF-Maker.dmg | cut -f1))"
      ;;
  esac
done
