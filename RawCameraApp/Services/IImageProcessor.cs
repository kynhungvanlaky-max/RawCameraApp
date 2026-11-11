using RawCameraApp.Services.Imaging;

namespace RawCameraApp.Services;

/// <summary>
/// Pipeline xử lý ảnh tính toán (Computational Photography Pipeline).
/// </summary>
public interface IImageProcessor
{
    Task<byte[]> ProcessAsync(
        byte[] rawBytes,
        IPhonePhotoStyle style = IPhonePhotoStyle.Standard,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null);

    Task<byte[]> ProcessAiAsync(
        byte[] rawBytes,
        AiCameraMode aiMode,
        float aiStrength,
        AiTelemetrySnapshot? liveTelemetry,
        float focusNormX = 0.5f,
        float focusNormY = 0.5f,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null);

    Task<byte[]> ProcessUltraHd48MpAsync(
        byte[] rawBytes,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null);
}

/// <summary>
/// Processor gốc: Pass-through 100% dữ liệu cảm biến
/// </summary>
public class RawPassThroughProcessor : IImageProcessor
{
    public Task<byte[]> ProcessAsync(
        byte[] rawBytes,
        IPhonePhotoStyle style = IPhonePhotoStyle.Standard,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null)
    {
        return Task.FromResult(rawBytes);
    }

    public Task<byte[]> ProcessAiAsync(
        byte[] rawBytes,
        AiCameraMode aiMode,
        float aiStrength,
        AiTelemetrySnapshot? liveTelemetry,
        float focusNormX = 0.5f,
        float focusNormY = 0.5f,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null)
    {
        return Task.FromResult(rawBytes);
    }

    public Task<byte[]> ProcessUltraHd48MpAsync(
        byte[] rawBytes,
        bool mirrorHorizontal = false,
        CameraLensProfile? lensProfile = null)
    {
        return Task.FromResult(rawBytes);
    }
}
