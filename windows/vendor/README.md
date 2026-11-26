# windows/vendor

Công cụ dòng lệnh đóng gói kèm bản Windows (không đưa lên git, tải lại bằng `python3 windows/vendor/fetch.py`; script kiểm tra sha256):

- **ffmpeg 9.1** bản "essentials" (x64, build tĩnh, GPLv3) của Gyan Doshi — https://github.com/GyanD/codexffmpeg/releases/tag/9.1
  Có sẵn các bộ lọc app dùng: `scdet`, `palettegen`, `paletteuse`, `hqdn3d`, `unsharp`, `eq`, `boxblur`… và bộ giải mã H.264, HEVC, ProRes, AV1 (dav1d), VP9.
- **gifsicle 1.97** win64 của Jernej Simončič — https://eternallybored.org/misc/gifsicle/ (trang gifsicle chính thức trỏ tới), GPLv2.

Giấy phép đi kèm: `FFMPEG-LICENSE.txt`, `GIFSICLE-LICENSE.txt` (được chép vào bộ cài).
