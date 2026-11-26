#!/bin/bash
# Publish a new version to GitHub Releases (the apps' updaters pick it up).
#   ./release.sh 1.3 "Ghi chú thay đổi"
#   ./release.sh            # publish the version already in Info.plist
# Builds both the macOS app (dmg + zip) and the Windows app (setup + portable zip, see windows/build.sh);
# both carry the same version number.
set -euo pipefail
cd "$(dirname "$0")"
PLIST=Resources/Info.plist
PB=/usr/libexec/PlistBuddy

if [ $# -ge 1 ]; then
  V="$1"
  $PB -c "Set :CFBundleShortVersionString $V" "$PLIST"
  B=$(( $($PB -c "Print :CFBundleVersion" "$PLIST") + 1 ))
  $PB -c "Set :CFBundleVersion $B" "$PLIST"
else
  V=$($PB -c "Print :CFBundleShortVersionString" "$PLIST")
fi
NOTES="${2:-Phiên bản $V}"

if git rev-parse "v$V" >/dev/null 2>&1; then
  echo "Tag v$V đã tồn tại — hãy dùng số phiên bản mới." >&2; exit 1
fi

./build.sh --dist
windows/build.sh "$V"
git add -A
git diff --cached --quiet || git commit -q -m "Release $V"
git tag "v$V"
git push -q origin HEAD
git push -q origin "v$V"
gh release create "v$V" dist/VnExpress-GIF-Maker.dmg dist/VnExpress-GIF-Maker.zip \
  windows/dist/VnExpress-GIF-Maker-Windows-Setup.exe windows/dist/VnExpress-GIF-Maker-Windows-Portable.zip --title "VnExpress GIF Maker $V" --notes "$NOTES"
echo "Đã phát hành v$V"
