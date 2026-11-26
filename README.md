# VnExpress GIF Maker

App cắt và ghép video thành GIF ngang dưới 8 MB, làm thumbnail cho VnExpress. Có bản macOS và bản Windows.

## Cài đặt trên Mac

Yêu cầu: Mac chip Apple, macOS 14 trở lên.

1. Tải **VnExpress-GIF-Maker.dmg** ở [Releases](https://github.com/zelgnuj/vne-gif-maker/releases/latest), mở ra, kéo app vào **Applications**.
2. Lần đầu mở: bấm **Xong** ở thông báo của macOS, vào **Cài đặt hệ thống › Quyền riêng tư & Bảo mật**, bấm **Vẫn mở**.

App tự kiểm tra và cài bản mới khi mở.

## Cài đặt trên Windows

Yêu cầu: Windows 10 (bản 1903 trở lên) hoặc Windows 11, 64-bit. Không cần cài thêm gì (app dùng .NET Framework 4.8 có sẵn trong Windows).

1. Tải **VnExpress-GIF-Maker-Windows-Setup.exe** ở [Releases](https://github.com/zelgnuj/vne-gif-maker/releases/latest) rồi mở. App được cài cho riêng tài khoản của bạn, không cần quyền quản trị.
2. Nếu Windows hiện *“Windows đã bảo vệ PC của bạn”* (SmartScreen, do app chưa được ký số): bấm **Thông tin thêm** › **Vẫn chạy**.

Không cài được? Dùng bản di động **VnExpress-GIF-Maker-Windows-Portable.zip**: giải nén cả thư mục, mở `VnExpressGifMaker.exe`.

Bản cài đặt tự kiểm tra và cập nhật khi mở; bản di động chỉ báo có bản mới.

## Cách dùng

1. Kéo video vào cửa sổ.
2. Chọn đoạn trên timeline: kéo hai mép vùng chọn, hoặc dùng ↑/↓ để nhảy cảnh và X để chọn trọn cảnh.
3. ⌘D (Windows: Ctrl+D) lấy thêm đoạn từ cùng video; kéo thẻ ở dải **Trình tự** để đổi thứ tự.
4. Chọn tỉ lệ ở nút **Tỉ lệ**, chỉnh từng đoạn ở tab **Chỉnh sửa**.
5. **Xuất GIF** (⌘↩, Windows: Ctrl+Enter).

Phím tắt chung: Space phát/dừng · ←/→ từng khung (Shift: 1 giây) · I/O điểm vào/ra · ↑/↓ nhảy cảnh · X chọn cảnh · S bật/tắt bắt dính · =/- thu phóng timeline. Trên Windows: Ctrl + cuộn chuột để thu phóng timeline, cuộn chuột trên khung xem trước để thu phóng hình.

## Phát triển

```bash
./build.sh --install              # build bản Mac và cài vào /Applications
windows/build.sh                  # build bản Windows ngay trên Mac → windows/dist
windows/test.sh                   # kiểm thử phần lõi bản Windows trên Mac
./release.sh 1.4 "Ghi chú"        # phát hành cả hai bản lên GitHub Releases
```

### Bản Windows (`windows/`)

C#/WPF trên .NET Framework 4.8: app gọn (khoảng 300 KB, bộ cài 29 MB vì có kèm ffmpeg), giao diện native, không cần runtime riêng. Build hoàn toàn trên Mac bằng .NET 8 SDK (`~/.dotnet`) và `makensis` (`brew install makensis`); `windows/vendor/fetch.py` tải ffmpeg/gifsicle bản Windows, có kiểm tra sha256.

- `src/VneGif.Core`: phần lõi dùng chung, chuyển từ `Edit.swift`, `GifEncoder.swift`, `SceneDetector.swift`. Chuỗi bộ lọc ffmpeg giống hệt bản Mac từng ký tự (`windows/test.sh` so với Swift).
- `src/VneGif.App`: giao diện WPF. Khung xem trước do chính ffmpeg dựng bằng cùng chuỗi bộ lọc lúc xuất, nên thấy gì xuất nấy.
- `.github/workflows/windows.yml`: chạy kiểm thử lõi và một lượt dùng thử tự động trên máy Windows của GitHub, lưu ảnh chụp màn hình.
