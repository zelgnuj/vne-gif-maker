# VnExpress GIF Maker

App macOS cắt và ghép video thành GIF ngang dưới 8 MB, làm thumbnail cho VnExpress.

## Cài đặt

Yêu cầu: Mac chip Apple, macOS 14 trở lên.

1. Tải **VnExpress-GIF-Maker.dmg** ở [Releases](https://github.com/zelgnuj/vne-gif-maker/releases/latest), mở ra, kéo app vào **Applications**.
2. Lần đầu mở: bấm **Xong** ở thông báo của macOS, vào **Cài đặt hệ thống › Quyền riêng tư & Bảo mật**, bấm **Vẫn mở**.

App tự kiểm tra và cài bản mới khi mở.

## Cách dùng

1. Kéo video vào cửa sổ.
2. Chọn đoạn trên timeline: kéo hai mép vùng chọn, hoặc dùng ↑/↓ để nhảy cảnh và X để chọn trọn cảnh.
3. ⌘D lấy thêm đoạn từ cùng video; kéo thẻ ở dải **Trình tự** để đổi thứ tự.
4. Chọn tỉ lệ ở nút **Tỉ lệ**, chỉnh từng đoạn ở tab **Chỉnh sửa**.
5. **Xuất GIF** (⌘↩).

## Phát triển

```bash
./build.sh --install              # build và cài vào /Applications
./release.sh 1.3 "Ghi chú"        # phát hành bản mới lên GitHub Releases
```
