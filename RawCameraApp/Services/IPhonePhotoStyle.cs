namespace RawCameraApp.Services;

/// <summary>
/// Các phong cách màu quang học RAW • PRO Photonic Style
/// </summary>
public enum IPhonePhotoStyle
{
    Standard,     // Tiêu chuẩn: Màu sắc tự nhiên, dải sáng cân bằng
    Warm,         // Ấm áp: Tông vàng hổ phách, tôn sắc da
    Vibrant,      // Rực rỡ: Tươi tắn sinh động cho cảnh quan
    RichContrast, // Tương phản: Vùng tối sâu, màu sắc đậm đà
    Cool,         // Mát mẻ: Tông lam trong trẻo hiện đại
    RawOriginal   // Ảnh gốc RAW: Dữ liệu cảm biến nguyên bản
}

public class StyleParameters
{
    public float WarmthRed { get; set; } = 1.0f;
    public float WarmthGreen { get; set; } = 1.0f;
    public float WarmthBlue { get; set; } = 1.0f;
    public float ShadowLift { get; set; } = 0.08f;       // Cứu sáng vùng tối nhẹ nhàng
    public float HighlightRollOff { get; set; } = 0.18f; // Nén vùng chói mượt mà
    public float ContrastSlope { get; set; } = 1.04f;    // Độ dốc tương phản midtones
    public float VibranceBoost { get; set; } = 0.12f;    // Tăng độ tươi màu nhạt
    public float LocalToneStrength { get; set; } = 0.15f;// Sức mạnh Local Tone Mapping
    public float MicroContrast { get; set; } = 0.12f;    // Độ nét vi mô không viền trắng

    public static StyleParameters Get(IPhonePhotoStyle style) => style switch
    {
        IPhonePhotoStyle.Standard => new StyleParameters
        {
            WarmthRed = 1.018f,
            WarmthGreen = 1.005f,
            WarmthBlue = 0.990f,
            ShadowLift = 0.08f,
            HighlightRollOff = 0.18f,
            ContrastSlope = 1.04f,
            VibranceBoost = 0.12f,
            LocalToneStrength = 0.15f,
            MicroContrast = 0.12f
        },
        IPhonePhotoStyle.Warm => new StyleParameters
        {
            WarmthRed = 1.038f,
            WarmthGreen = 1.012f,
            WarmthBlue = 0.965f,
            ShadowLift = 0.09f,
            HighlightRollOff = 0.18f,
            ContrastSlope = 1.03f,
            VibranceBoost = 0.10f,
            LocalToneStrength = 0.14f,
            MicroContrast = 0.11f
        },
        IPhonePhotoStyle.Vibrant => new StyleParameters
        {
            WarmthRed = 1.010f,
            WarmthGreen = 1.008f,
            WarmthBlue = 0.995f,
            ShadowLift = 0.10f,
            HighlightRollOff = 0.16f,
            ContrastSlope = 1.05f,
            VibranceBoost = 0.22f,
            LocalToneStrength = 0.18f,
            MicroContrast = 0.13f
        },
        IPhonePhotoStyle.RichContrast => new StyleParameters
        {
            WarmthRed = 1.012f,
            WarmthGreen = 1.000f,
            WarmthBlue = 0.985f,
            ShadowLift = 0.02f,   // Vùng tối sâu hơn
            HighlightRollOff = 0.22f,
            ContrastSlope = 1.12f, // Độ tương phản cao
            VibranceBoost = 0.14f,
            LocalToneStrength = 0.10f,
            MicroContrast = 0.15f
        },
        IPhonePhotoStyle.Cool => new StyleParameters
        {
            WarmthRed = 0.978f,
            WarmthGreen = 0.995f,
            WarmthBlue = 1.030f,
            ShadowLift = 0.08f,
            HighlightRollOff = 0.18f,
            ContrastSlope = 1.04f,
            VibranceBoost = 0.10f,
            LocalToneStrength = 0.14f,
            MicroContrast = 0.12f
        },
        _ => new StyleParameters()
    };
}
