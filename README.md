# RawCameraApp (RAW • PRO)

Ứng dụng máy ảnh chuyên nghiệp được phát triển bằng **.NET MAUI (Android)** tối ưu hóa cho thiết bị di động (hỗ trợ kiểm thử trên Poco X3 Pro và các thiết bị Android chạy Camera2 API).

## Tính năng nổi bật

- **Chụp ảnh RAW & Chế độ Pro**: Điều khiển thủ công phơi sáng (ISO, Shutter Speed), cân bằng trắng (White Balance), lấy nét (Focus).
- **Xem trước & Khung ngắm**: Hỗ trợ lưới căn chỉnh (Grid lines), chuyển đổi đa ống kính (Wide, Ultra-Wide, Macro), đèn Flash tự động.
- **AI Enhancement**: Tích hợp mô hình ONNX Runtime để xử lý nâng cao chất lượng ảnh.
- **Thư viện ảnh tích hợp**: Xem nhanh chi tiết ảnh chụp, hỗ trợ chọn nhiều ảnh và hiển thị lightbox.

## Yêu cầu môi trường

- **.NET 9.0 SDK** (với workload `maui-android`)
- **Microsoft OpenJDK 17**
- **Android SDK** (API Level 24+)

## Hướng dẫn cài đặt & chạy nhanh

1. Chạy file `setup-env.bat` (Run as Administrator) để tự động cài đặt OpenJDK 17 và workload `maui-android`.
2. Kết nối điện thoại Android qua cổng USB và bật **USB Debugging**.
3. Chạy file `run-on-device.bat` để build và cài đặt trực tiếp app lên thiết bị.
