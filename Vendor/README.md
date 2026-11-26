# Vendor/ffmpeg

Bản ffmpeg 9.0.1 tối giản, build tĩnh (arm64, macOS 14+) kèm dav1d 1.5.5 (giải mã AV1 bằng phần mềm), chỉ dùng thư viện hệ thống. `build.sh` nhúng nó vào app.
Nguồn: https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz (sha256 cf38e0e28c7e5605942c4a77755349b0145804a397af37eb1fb4c77cb237f635), giấy phép GPLv2+.

Build lại:
```bash
# dav1d: meson setup build --buildtype=release --default-library=static -Denable_tools=false -Denable_tests=false \
#   -Dc_args=-mmacosx-version-min=14.0 -Dc_link_args=-mmacosx-version-min=14.0 --prefix=$DAV1D && ninja -C build install
PKG_CONFIG_PATH=$DAV1D/lib/pkgconfig ./configure --cc=clang --pkg-config-flags=--static --enable-libdav1d --arch=arm64 --enable-gpl --enable-static --disable-shared \
  --disable-everything --disable-autodetect --disable-network --disable-doc --disable-debug \
  --disable-ffplay --disable-ffprobe --enable-zlib --enable-videotoolbox \
  --enable-protocol=file,pipe \
  --enable-demuxer=mov,matroska,avi,mpegts,flv,mpegps \
  --enable-decoder=libdav1d,h264,hevc,prores,mpeg4,mpeg2video,mpeg1video,h263,vp8,vp9,mjpeg,png \
  --enable-parser=h264,hevc,mpeg4video,mpegvideo,vp8,vp9,av1,mjpeg,png,h263 \
  --enable-hwaccel=h264_videotoolbox,hevc_videotoolbox,prores_videotoolbox,vp9_videotoolbox,mpeg4_videotoolbox,mpeg2_videotoolbox,mpeg1_videotoolbox,h263_videotoolbox \
  --enable-bsf=vp9_superframe_split \
  --enable-filter=setpts,fps,format,crop,transpose,hflip,vflip,rotate,split,scale,boxblur,eq,overlay,hqdn3d,unsharp,setsar,concat,palettegen,paletteuse,null,buffer,buffersink,trim,copy,pad,anull,aformat,atrim,abuffer,abuffersink \
  --enable-encoder=gif,h264_videotoolbox --enable-muxer=gif,mp4,mov \
  --extra-cflags=-mmacosx-version-min=14.0 --extra-ldflags=-mmacosx-version-min=14.0
make -j10 ffmpeg
```

# Vendor/gifsicle

gifsicle 1.96 (https://www.lcdf.org/gifsicle/gifsicle-1.96.tar.gz,
sha256 fd23d279681a6dfe3c15264e33f344045b3ba473da4d19f49e67a50994b077fb), GPLv2.
Dùng cho bước tối ưu/nén có mất mát khi GIF vượt giới hạn dung lượng.

```bash
./configure --disable-gifview --disable-gifdiff CFLAGS="-O2 -mmacosx-version-min=14.0" LDFLAGS="-mmacosx-version-min=14.0"
make && cp src/gifsicle <repo>/Vendor/gifsicle
```
