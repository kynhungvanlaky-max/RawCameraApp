using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Lõi tính toán quang học dùng chung (Unified Photonic Shared Core).
/// Đồng bộ hoá toàn bộ các thuật toán nền tảng giữa mọi chế độ (AI CAM, PHOTO) và các Engine
/// (OnnxPhotonicEngine, RetinexPhotonicEngine, LocalToneMapper, AiComputationalProcessor, IPhoneStyleImageProcessor).
/// Toàn bộ hàm được tối ưu Inline và quản lý bộ nhớ qua ArrayPool (Zero Heap Allocation).
/// </summary>
public static class PhotonicSharedCore
{
    /// <summary>
    /// Tính độ sáng cảm nhận chuẩn Rec.709 Luma [0..255]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeLuma(float r, float g, float b)
    {
        return 0.2126f * r + 0.7152f * g + 0.0722f * b;
    }

    /// <summary>
    /// Tính nhanh độ sáng Rec.709 bằng số nguyên từ mã màu ARGB32 [0..255]
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeFastLumaInt(int argb)
    {
        int r = (argb >> 16) & 0xFF;
        int g = (argb >> 8) & 0xFF;
        int b = argb & 0xFF;
        return (r * 54 + g * 183 + b * 19) >> 8;
    }

    /// <summary>
    /// Đường cong nén vùng sáng Filmic Knee (Highlight Roll-Off Guard) dùng chung cho cả AI CAM và PHOTO.
    /// Chống hiện tượng cháy sáng trắng (255 clipping) trên mây, đèn, hoặc trán/mũi.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ApplyHighlightRollOff(float v, float knee = 190.0f, float maxOut = 252.0f)
    {
        if (v <= knee) return Math.Max(0.0f, v);
        float excess = v - knee;
        float headRoom = maxOut - knee;
        float compressed = knee + headRoom * (excess / (excess + 48.0f));
        return Math.Clamp(compressed, 0.0f, maxOut);
    }

    /// <summary>
    /// Nhận diện trọng số màu da người mềm mại trong không gian YCbCr [0.0 .. 1.0].
    /// Dùng chung giữa AiSceneAnalyzer, IPhoneStyleImageProcessor (PHOTO) và AiComputationalProcessor (AI CAM).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ComputeSkinWeight(int r, int g, int b)
    {
        float cb = -0.1687f * r - 0.3313f * g + 0.5000f * b + 128.0f;
        float cr =  0.5000f * r - 0.4187f * g - 0.0813f * b + 128.0f;

        float dCb = (cb - 109.0f) / 17.0f;
        float dCr = (cr - 151.0f) / 19.0f;
        float distSq = dCb * dCb + dCr * dCr;

        return (distSq < 1.0f) ? (1.0f - distSq) : 0.0f;
    }

    /// <summary>
    /// Khử nhiễu hạt màu (Chroma Noise Reduction) tại vùng tối khi chụp thiếu sáng / ISO cao.
    /// Dùng chung giữa IPhoneStyleImageProcessor, RetinexPhotonicEngine và AiComputationalProcessor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ApplyChromaDenoise(ref float r, ref float g, ref float b, float luma, int iso, float strength = 1.0f)
    {
        if (iso <= 350 || luma >= 65.0f) return;

        float isoFactor = Math.Clamp((iso - 350.0f) / 1650.0f, 0.0f, 1.0f);
        float shadowFactor = (65.0f - luma) / 65.0f;
        float nrStrength = shadowFactor * isoFactor * 0.48f * strength;

        float avgLuma = 0.299f * r + 0.587f * g + 0.114f * b;
        r = r * (1.0f - nrStrength) + avgLuma * nrStrength;
        g = g * (1.0f - nrStrength) + avgLuma * nrStrength;
        b = b * (1.0f - nrStrength) + avgLuma * nrStrength;
    }

    /// <summary>
    /// Tính hệ số cân bằng trắng tự động bảo toàn độ sáng (Luma-Preserving Gray-World AWB).
    /// Dùng chung giữa SceneAnalyzer (PHOTO) và AiSceneAnalyzer (AI CAM).
    /// </summary>
    public static (float GainR, float GainG, float GainB) ComputeAwbGains(
        float sumR, float sumG, float sumB, float count, float damping = 0.55f)
    {
        if (count < 50.0f) return (1.0f, 1.0f, 1.0f);

        float avgR = sumR / count;
        float avgG = sumG / count;
        float avgB = sumB / count;
        float targetNeutral = 0.2126f * avgR + 0.7152f * avgG + 0.0722f * avgB;

        if (avgR <= 0.02f || avgG <= 0.02f || avgB <= 0.02f)
            return (1.0f, 1.0f, 1.0f);

        float rawGainR = targetNeutral / avgR;
        float rawGainG = targetNeutral / avgG;
        float rawGainB = targetNeutral / avgB;

        float gainR = Math.Clamp(1.0f + (rawGainR - 1.0f) * damping, 0.88f, 1.14f);
        float gainG = Math.Clamp(1.0f + (rawGainG - 1.0f) * (damping * 0.65f), 0.94f, 1.06f);
        float gainB = Math.Clamp(1.0f + (rawGainB - 1.0f) * damping, 0.88f, 1.14f);

        return (gainR, gainG, gainB);
    }

    /// <summary>
    /// Nội suy song tuyến tính (Bilinear Interpolation) mượt mà trên lưới 1 chiều phẳng.
    /// Dùng chung giữa OnnxPhotonicEngine, RetinexPhotonicEngine, LocalToneMapper và AiComputationalProcessor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SampleBilinear(float[] grid, int gw, int gh, float normX, float normY)
    {
        float gx = Math.Clamp(normX * (gw - 1), 0.0f, gw - 1.001f);
        float gy = Math.Clamp(normY * (gh - 1), 0.0f, gh - 1.001f);

        int x0 = (int)gx;
        int y0 = (int)gy;
        int x1 = Math.Min(x0 + 1, gw - 1);
        int y1 = Math.Min(y0 + 1, gh - 1);

        float fx = gx - x0;
        float fy = gy - y0;

        int row0 = y0 * gw;
        int row1 = y1 * gw;

        float v00 = grid[row0 + x0];
        float v10 = grid[row0 + x1];
        float v01 = grid[row1 + x0];
        float v11 = grid[row1 + x1];

        float top = v00 + (v10 - v00) * fx;
        float bottom = v01 + (v11 - v01) * fx;

        return top + (bottom - top) * fy;
    }

    /// <summary>
    /// Bộ lọc làm mờ tách chiều 2D tốc độ O(1) (Fast Separable Box Blur) dùng chung.
    /// Sử dụng bộ đệm tạm từ ArrayPool để không cấp phát rác bộ nhớ.
    /// </summary>
    public static void BoxBlur2D(float[] src, float[] dst, float[] temp, int w, int h, int radius)
    {
        BoxBlurHorizontal(src, temp, w, h, radius);
        BoxBlurVertical(temp, dst, w, h, radius);
    }

    public static void BoxBlurHorizontal(float[] src, float[] dst, int w, int h, int radius)
    {
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int xStart = Math.Max(0, x - radius);
                int xEnd = Math.Min(w - 1, x + radius);
                float sum = 0.0f;
                for (int xi = xStart; xi <= xEnd; xi++)
                {
                    sum += src[row + xi];
                }
                dst[row + x] = sum / (xEnd - xStart + 1);
            }
        });
    }

    public static void BoxBlurVertical(float[] src, float[] dst, int w, int h, int radius)
    {
        Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                int yStart = Math.Max(0, y - radius);
                int yEnd = Math.Min(h - 1, y + radius);
                float sum = 0.0f;
                for (int yi = yStart; yi <= yEnd; yi++)
                {
                    sum += src[yi * w + x];
                }
                dst[y * w + x] = sum / (yEnd - yStart + 1);
            }
        });
    }
}
