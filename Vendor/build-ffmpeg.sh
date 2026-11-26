#!/bin/bash
# Rebuilds Vendor/ffmpeg: minimal static ffmpeg + dav1d, arm64, macOS 14+.
# Usage: Vendor/build-ffmpeg.sh <ffmpeg-src-dir> <dav1d-install-prefix>
set -euo pipefail
SRC="$1"; DAV1D="$2"; OUT="$(cd "$(dirname "$0")" && pwd)/ffmpeg"
cd "$SRC"
make distclean >/dev/null 2>&1 || true
PKG_CONFIG_PATH="$DAV1D/lib/pkgconfig" ./configure \
  --cc=clang --arch=arm64 --pkg-config-flags=--static \
  --enable-gpl --enable-static --disable-shared \
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
  --disable-ffplay --disable-ffprobe \
  --enable-zlib --enable-videotoolbox --enable-libdav1d \
  --enable-protocol=file,pipe \
  --enable-demuxer=mov,matroska,avi,mpegts,flv,mpegps \
  --enable-decoder=libdav1d,h264,hevc,prores,mpeg4,mpeg2video,mpeg1video,h263,vp8,vp9,mjpeg,png \
  --enable-parser=h264,hevc,mpeg4video,mpegvideo,vp8,vp9,av1,mjpeg,png,h263 \
  --enable-hwaccel=h264_videotoolbox,hevc_videotoolbox,prores_videotoolbox,vp9_videotoolbox,mpeg4_videotoolbox,mpeg2_videotoolbox,mpeg1_videotoolbox,h263_videotoolbox \
  --enable-bsf=vp9_superframe_split \
  --enable-filter=setpts,fps,format,crop,transpose,hflip,vflip,rotate,split,scale,boxblur,eq,overlay,hqdn3d,unsharp,setsar,concat,palettegen,paletteuse,null,buffer,buffersink,trim,copy,pad,anull,aformat,atrim,abuffer,abuffersink,scdet \
  --enable-encoder=gif,h264_videotoolbox,wrapped_avframe --enable-muxer=gif,mp4,mov,null \
  --extra-cflags="-mmacosx-version-min=14.0" --extra-ldflags="-mmacosx-version-min=14.0"
make -j"$(sysctl -n hw.ncpu)" ffmpeg
cp ffmpeg "$OUT"
echo "Built $OUT"
