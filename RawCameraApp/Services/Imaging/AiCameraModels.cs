namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Các chế độ xử lý ảnh AI chuyên biệt trong AI CAM
/// </summary>
public enum AiCameraMode
{
    Auto,        // AI Tự động nhận diện bối cảnh và phối hợp thuật toán tối ưu
    Portrait,    // Chân dung & Làm đẹp da (Bilateral Pore-Preserving) + Bokeh dịu
    NightSight,  // Chụp đêm siêu sáng Zero-DCE + Khử nhiễu đa tầng Spatial Denoise
    Landscape,   // Bầu trời trong xanh (Deep Cyan-Blue) & Tán cây tươi tốt (Lush Emerald)
    SuperDetail  // Siêu nét vi mô (Edge-Aware Unsharp) + Khử mù sương (Dehaze)
}

/// <summary>
/// Các bối cảnh được AI phân tích và phân loại theo thời gian thực
/// </summary>
public enum AiDetectedScene
{
    Neutral,        // Cảnh tiêu chuẩn
    Portrait,       // Chân dung / Có người trong khung hình
    NightLowLight,  // Cảnh đêm / Thiếu sáng nặng
    BacklitHdr,     // Cảnh ngược sáng / Chênh sáng mạnh
    LandscapeSky,   // Phong cảnh / Bầu trời rộng
    FoliageNature,  // Cây cối / Hoa lá / Thiên nhiên xanh
    FoodWarm        // Đồ ăn / Vật thể cận cảnh tông ấm
}

/// <summary>
/// Bản chụp trạng thái AI thời gian thực phục vụ hiển thị Telemetry và điều khiển phần cứng Camera2
/// </summary>
public record AiTelemetrySnapshot(
    AiDetectedScene DetectedScene,
    float Confidence,
    string SceneName,
    string SceneIcon,
    float MeanLuma,
    float ShadowRatio,
    float HighlightRatio,
    float SkinCoverage,
    float SkyCoverage,
    float FoliageCoverage,
    float RecommendedEvShift,
    float WbGainR,
    float WbGainB,
    int FaceCount,
    long ProcessingTimeMs,
    float FaceNormX = 0.5f,
    float FaceNormY = 0.45f
);
