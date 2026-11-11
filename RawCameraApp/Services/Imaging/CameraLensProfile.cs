namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Các phân loại ống kính phổ biến trên thiết bị di động
/// </summary>
public enum CameraLensType
{
    UltraWide,     // Góc siêu rộng (0.5x / 0.6x, < 21mm quy đổi)
    WideMain,      // Camera chính (1x, ~23mm - 28mm)
    Telephoto,     // Ống kính Tele quang học hoặc 2x In-Sensor Crop (2x / 3x / 5x, >= 45mm)
    Macro,         // Ống kính chụp cận cảnh chuyên dụng
    FrontSelfie,   // Camera trước tiêu chuẩn (1x)
    FrontUltraWide // Camera trước góc rộng (0.8x nếu có)
}

/// <summary>
/// Các nhóm nhà sản xuất điện thoại Android (dùng để tinh chỉnh thuật toán xử lý ảnh theo profile ISP phần cứng)
/// </summary>
public enum OemCameraVendor
{
    Generic,
    XiaomiPocoRedmi,
    Samsung,
    GooglePixel,
    OppoOnePlusRealme,
    VivoIqoo,
    Sony
}

/// <summary>
/// Hồ sơ đặc tính quang học và phần cứng của từng ống kính trên máy
/// </summary>
public record CameraLensProfile(
    string DeviceId,
    Camera.MAUI.CameraInfo? MauiCamera,
    CameraLensType LensType,
    string ShortBadge,              // "0.6x", "1x", "2x", "Macro"
    string DisplayName,             // "Góc siêu rộng 0.6x", "Camera chính 1x"...
    string SpecSummary,             // "16mm f/2.2 • 8MP"
    float OpticalZoomRatio,         // 0.6f, 1.0f, 2.0f, 3.0f...
    float TargetHardwareZoom,       // ZoomFactor phần cứng (hoặc ControlZoomRatio)
    bool UsesLogicalZoomRatio,      // true nếu dùng ControlZoomRatio trên Logical Camera (Samsung / Pixel)
    bool IsSensorCropZoom,          // true nếu là In-Sensor Crop 2x trên cảm biến chính
    float FocalLengthMm,            // Tiêu cự vật lý (mm)
    int FocalLength35mmEquivalent,  // Tiêu cự quy đổi 35mm Full-Frame (mm)
    float Aperture,                 // Khẩu độ f-number (ví dụ 1.79, 2.2, 2.4)
    float SensorWidthMm,
    float SensorHeightMm,
    float SensorAreaMm2,
    float PixelPitchMicrons,        // Kích thước điểm ảnh thực tế (µm)
    int Megapixels,                 // Độ phân giải cảm biến xấp xỉ (MP)
    bool HasAutoFocus,
    bool HasFlash,
    OemCameraVendor Vendor,
    string DeviceModel,
    // Các hệ số hiệu chỉnh quang học tự động
    float IsoNoiseMultiplier,       // Hệ số nhân khử nhiễu (điểm ảnh nhỏ -> tăng khử nhiễu)
    float VignetteCorrectionStrength,// Cường độ bù sáng 4 góc cho góc siêu rộng
    float EdgeAcutanceBoost,        // Hệ số tăng cường độ nét vi mô (2x crop, Macro)
    // Khả năng siêu phân giải phần cứng (48MP / 50MP / 64MP / 108MP)
    bool SupportsUltraHighRes = false,
    int MaxNativeMegapixels = 12,
    int MaxNativeWidth = 4000,
    int MaxNativeHeight = 3000
);
