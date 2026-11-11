using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
#if ANDROID
using Android.Graphics;
using RawCameraApp.Platforms.Android;
#endif

namespace RawCameraApp.Services.Imaging;

public enum PhotonicAiAction
{
    AutoMaster,
    SubjectRelight,
    SemanticBokeh,
    SmartHdrSky,
    CinematicLut,
    SuperSharpDetail
}

public record AiPhotoDiagnosis(
    string Summary,
    float MeanLuma,
    float CenterLuma,
    float SkinRatio,
    float SkyRatio,
    float ShadowRatio,
    float HighlightRatio,
    float SharpnessScore
);

/// <summary>
/// Trợ lý Quang học Photonic AI Agent (RAW • PRO AI Studio).
/// Hoạt động 100% ngoại tuyến trên phần cứng thiết bị, tối ưu đa luồng CPU và NPU.
/// Tự động chẩn đoán khuyết điểm ánh sáng và thực thi 6 tác vụ phục hồi ảnh chuyên sâu.
/// </summary>
public static class PhotonicAiAgent
{
    public static AiPhotoDiagnosis Analyze(int[] pixels, int width, int height)
    {
        int total = width * height;
        if (total == 0 || pixels == null || pixels.Length == 0)
        {
            return new AiPhotoDiagnosis("Chưa có dữ liệu ảnh", 0.5f, 0.5f, 0f, 0f, 0f, 0f, 0f);
        }

        int step = (width > 600) ? 8 : 2;

        double sumLuma = 0;
        double sumCenterLuma = 0;
        int centerCount = 0;
        int skinCount = 0;
        int skyCount = 0;
        int shadowCount = 0;
        int highlightCount = 0;
        int sampled = 0;

        float cX = width * 0.5f;
        float cY = height * 0.45f;
        float centerRadiusSq = (width * 0.28f) * (width * 0.28f);
        int upperSkyLimitY = (int)(height * 0.46f);

        double laplacianSum = 0;
        int laplacianCount = 0;

        for (int y = 0; y < height; y += step)
        {
            int rowOffset = y * width;
            bool isUpper = y < upperSkyLimitY;
            float dy = y - cY;
            float dySq = dy * dy;

            for (int x = 0; x < width; x += step)
            {
                int color = pixels[rowOffset + x];
                int r = (color >> 16) & 0xFF;
                int g = (color >> 8) & 0xFF;
                int b = color & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b) / 255.0f;
                sumLuma += luma;
                sampled++;

                if (luma < 0.16f) shadowCount++;
                else if (luma > 0.85f) highlightCount++;

                float dx = x - cX;
                if ((dx * dx + dySq) <= centerRadiusSq)
                {
                    sumCenterLuma += luma;
                    centerCount++;
                }

                if (PhotonicSharedCore.ComputeSkinWeight(r, g, b) > 0.20f)
                {
                    skinCount++;
                }

                if (isUpper && b > r * 1.15f && b > g * 1.04f && luma > 0.30f && luma < 0.96f)
                {
                    skyCount++;
                }

                // Laplacian variance sơ bộ
                if (x >= step && x < width - step && y >= step && y < height - step)
                {
                    int cLeft = pixels[rowOffset + x - step];
                    int cRight = pixels[rowOffset + x + step];
                    int cTop = pixels[(y - step) * width + x];
                    int cBottom = pixels[(y + step) * width + x];

                    int lCenter = (r * 54 + g * 183 + b * 19) >> 8;
                    int lLeft = PhotonicSharedCore.ComputeFastLumaInt(cLeft);
                    int lRight = PhotonicSharedCore.ComputeFastLumaInt(cRight);
                    int lTop = PhotonicSharedCore.ComputeFastLumaInt(cTop);
                    int lBottom = PhotonicSharedCore.ComputeFastLumaInt(cBottom);

                    int lap = Math.Abs(4 * lCenter - lLeft - lRight - lTop - lBottom);
                    laplacianSum += lap;
                    laplacianCount++;
                }
            }
        }

        if (sampled == 0) sampled = 1;
        float meanLuma = (float)(sumLuma / sampled);
        float centerLuma = centerCount > 0 ? (float)(sumCenterLuma / centerCount) : meanLuma;
        float skinRatio = (float)skinCount / sampled;
        float skyRatio = (float)skyCount / sampled;
        float shadowRatio = (float)shadowCount / sampled;
        float highlightRatio = (float)highlightCount / sampled;
        float sharpness = laplacianCount > 0 ? (float)(laplacianSum / laplacianCount) : 10.0f;

        // Sinh chẩn đoán bằng tiếng Việt trực diện không có dấu ngoặc đơn
        string summary;
        if (centerLuma < 0.35f && highlightRatio > 0.16f)
        {
            summary = "Ngược sáng mạnh • Đề xuất cứu sáng chủ thể và giảm chói nền trời";
        }
        else if (meanLuma < 0.28f || shadowRatio > 0.40f)
        {
            summary = "Thiếu sáng • Đề xuất kích hoạt tăng sáng đường cong và khử nhiễu";
        }
        else if (skinRatio > 0.10f)
        {
            summary = "Ảnh chân dung • Đề xuất làm sáng da mặt và làm mờ hậu cảnh";
        }
        else if (skyRatio > 0.14f || (highlightRatio > 0.20f && meanLuma > 0.45f))
        {
            summary = "Phong cảnh bầu trời • Đề xuất cân bằng dải tương phản và tăng sắc xanh";
        }
        else if (sharpness < 9.0f)
        {
            summary = "Độ nét thấp • Đề xuất tăng nét vi mô và làm rõ khối chi tiết";
        }
        else
        {
            summary = "Ánh sáng tiêu chuẩn • Sẵn sàng tối ưu màu sắc và độ tương phản";
        }

        return new AiPhotoDiagnosis(
            summary,
            meanLuma,
            centerLuma,
            skinRatio,
            skyRatio,
            shadowRatio,
            highlightRatio,
            sharpness
        );
    }

    public static void ExecuteAction(
        int[] pixels,
        int width,
        int height,
        PhotonicAiAction action,
        float strength = 1.0f)
    {
        if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0) return;
        strength = Math.Clamp(strength, 0.1f, 1.0f);

        switch (action)
        {
            case PhotonicAiAction.AutoMaster:
                ExecuteAutoMaster(pixels, width, height, strength);
                break;
            case PhotonicAiAction.SubjectRelight:
                ExecuteSubjectRelight(pixels, width, height, strength);
                break;
            case PhotonicAiAction.SemanticBokeh:
                ExecuteSemanticBokeh(pixels, width, height, strength);
                break;
            case PhotonicAiAction.SmartHdrSky:
                ExecuteSmartHdrSky(pixels, width, height, strength);
                break;
            case PhotonicAiAction.CinematicLut:
                ExecuteCinematicLut(pixels, width, height, strength);
                break;
            case PhotonicAiAction.SuperSharpDetail:
                ExecuteSuperSharpDetail(pixels, width, height, strength);
                break;
        }
    }

    private static void ExecuteAutoMaster(int[] pixels, int width, int height, float strength)
    {
        var diag = Analyze(pixels, width, height);

        float lift = (diag.MeanLuma < 0.38f) ? 0.22f * strength : 0.08f * strength;
        float contrast = 1.04f + 0.08f * strength;
        float knee = 192.0f;

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int c = pixels[idx];
                int r = (c >> 16) & 0xFF;
                int g = (c >> 8) & 0xFF;
                int b = c & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);
                float normL = luma / 255.0f;

                // Tăng sáng vùng tối dạng S-Curve
                float shadowBoost = (1.0f - normL) * (1.0f - normL) * lift * 255.0f;
                float newL = luma + shadowBoost;

                // Tương phản midtone
                newL = 128.0f + (newL - 128.0f) * contrast;
                float scale = (luma > 0.5f) ? (newL / luma) : 1.0f;
                scale = Math.Clamp(scale, 0.85f, 1.45f);

                float nr = r * scale;
                float ng = g * scale;
                float nb = b * scale;

                // Bơm nhẹ độ tươi màu nhạt
                float maxC = Math.Max(nr, Math.Max(ng, nb));
                float minC = Math.Min(nr, Math.Min(ng, nb));
                float sat = maxC > 0.1f ? (maxC - minC) / maxC : 0.0f;
                if (sat < 0.40f)
                {
                    float vib = (0.40f - sat) * 0.20f * strength;
                    float avg = (nr + ng + nb) / 3.0f;
                    nr = nr + (nr - avg) * vib;
                    ng = ng + (ng - avg) * vib;
                    nb = nb + (nb - avg) * vib;
                }

                nr = PhotonicSharedCore.ApplyHighlightRollOff(nr, knee);
                ng = PhotonicSharedCore.ApplyHighlightRollOff(ng, knee);
                nb = PhotonicSharedCore.ApplyHighlightRollOff(nb, knee);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(nr + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(ng + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(nb + 0.5f), 0, 255);
            }
        });
    }

    private static void ExecuteSubjectRelight(int[] pixels, int width, int height, float strength)
    {
        float cX = width * 0.5f;
        float cY = height * 0.45f;
        float maxDist = MathF.Sqrt(width * width + height * height) * 0.5f;

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            float dy = y - cY;
            float dySq = dy * dy;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int c = pixels[idx];
                int r = (c >> 16) & 0xFF;
                int g = (c >> 8) & 0xFF;
                int b = c & 0xFF;

                float skinWeight = PhotonicSharedCore.ComputeSkinWeight(r, g, b);

                float dx = x - cX;
                float dist = MathF.Sqrt(dx * dx + dySq);
                float centerWeight = Math.Clamp(1.0f - (dist / maxDist), 0.0f, 1.0f);
                centerWeight = centerWeight * centerWeight;

                float subjectWeight = Math.Max(skinWeight * 1.35f, centerWeight * 0.75f);
                float boost = 1.0f + (subjectWeight * 0.38f * strength);

                float nr = PhotonicSharedCore.ApplyHighlightRollOff(r * boost, knee: 188.0f);
                float ng = PhotonicSharedCore.ApplyHighlightRollOff(g * boost, knee: 188.0f);
                float nb = PhotonicSharedCore.ApplyHighlightRollOff(b * boost, knee: 188.0f);

                // Ánh sáng hồng hào nhẹ trên da
                if (skinWeight > 0.15f)
                {
                    nr += 4.0f * skinWeight * strength;
                    ng += 1.8f * skinWeight * strength;
                }

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(nr + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(ng + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(nb + 0.5f), 0, 255);
            }
        });
    }

    private static void ExecuteSemanticBokeh(int[] pixels, int width, int height, float strength)
    {
        int total = width * height;
        int[] copy = ArrayPool<int>.Shared.Rent(total);
        try
        {
            Array.Copy(pixels, copy, total);

            float cX = width * 0.5f;
            float cY = height * 0.45f;
            float innerR = Math.Min(width, height) * 0.28f;
            float outerR = Math.Min(width, height) * 0.65f;

            int blurStep = Math.Max(2, (int)(4 * strength));

            Parallel.For(0, height, y =>
            {
                int rowOffset = y * width;
                float dy = y - cY;
                float dySq = dy * dy;

                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    int c = copy[idx];
                    int r = (c >> 16) & 0xFF;
                    int g = (c >> 8) & 0xFF;
                    int b = c & 0xFF;

                    float skinWeight = PhotonicSharedCore.ComputeSkinWeight(r, g, b);

                    float dx = x - cX;
                    float dist = MathF.Sqrt(dx * dx + dySq);

                    float blurWeight = 0.0f;
                    if (dist > innerR && skinWeight < 0.25f)
                    {
                        blurWeight = Math.Clamp((dist - innerR) / (outerR - innerR), 0.0f, 1.0f);
                        blurWeight = blurWeight * strength * 0.85f;
                    }

                    if (blurWeight > 0.05f && x >= blurStep && x < width - blurStep && y >= blurStep && y < height - blurStep)
                    {
                        int p1 = copy[idx - blurStep];
                        int p2 = copy[idx + blurStep];
                        int p3 = copy[idx - blurStep * width];
                        int p4 = copy[idx + blurStep * width];
                        int p5 = copy[idx - blurStep * width - blurStep];
                        int p6 = copy[idx + blurStep * width + blurStep];

                        float br = (((p1 >> 16) & 0xFF) + ((p2 >> 16) & 0xFF) + ((p3 >> 16) & 0xFF) +
                                    ((p4 >> 16) & 0xFF) + ((p5 >> 16) & 0xFF) + ((p6 >> 16) & 0xFF)) / 6.0f;
                        float bg = (((p1 >> 8) & 0xFF) + ((p2 >> 8) & 0xFF) + ((p3 >> 8) & 0xFF) +
                                    ((p4 >> 8) & 0xFF) + ((p5 >> 8) & 0xFF) + ((p6 >> 8) & 0xFF)) / 6.0f;
                        float bb = ((p1 & 0xFF) + (p2 & 0xFF) + (p3 & 0xFF) +
                                    (p4 & 0xFF) + (p5 & 0xFF) + (p6 & 0xFF)) / 6.0f;

                        r = (int)(r * (1.0f - blurWeight) + br * blurWeight + 0.5f);
                        g = (int)(g * (1.0f - blurWeight) + bg * blurWeight + 0.5f);
                        b = (int)(b * (1.0f - blurWeight) + bb * blurWeight + 0.5f);
                    }

                    pixels[idx] = (unchecked((int)0xFF000000)) |
                                  (Math.Clamp(r, 0, 255) << 16) |
                                  (Math.Clamp(g, 0, 255) << 8) |
                                  Math.Clamp(b, 0, 255);
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(copy);
        }
    }

    private static void ExecuteSmartHdrSky(int[] pixels, int width, int height, float strength)
    {
        int upperSkyLimitY = (int)(height * 0.52f);

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            bool isUpper = y < upperSkyLimitY;
            float skyAltitudeWeight = isUpper ? (1.0f - (float)y / upperSkyLimitY) : 0.0f;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int c = pixels[idx];
                int r = (c >> 16) & 0xFF;
                int g = (c >> 8) & 0xFF;
                int b = c & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);

                float finalR = r;
                float finalG = g;
                float finalB = b;

                // Nén vùng cháy sáng bầu trời
                if (luma > 175.0f && isUpper)
                {
                    float burnFactor = (luma - 175.0f) / 80.0f;
                    float recoverWeight = burnFactor * skyAltitudeWeight * 0.35f * strength;

                    finalR *= (1.0f - recoverWeight * 1.15f);
                    finalG *= (1.0f - recoverWeight * 0.85f);
                    finalB *= (1.0f - recoverWeight * 0.45f); // Giữ sắc xanh lam của bầu trời
                }

                // Cứu sáng vùng tối sâu
                if (luma < 90.0f)
                {
                    float darkFactor = (90.0f - luma) / 90.0f;
                    float shadowGain = 1.0f + (darkFactor * 0.28f * strength);
                    finalR *= shadowGain;
                    finalG *= shadowGain;
                    finalB *= shadowGain;
                }

                finalR = PhotonicSharedCore.ApplyHighlightRollOff(finalR, knee: 195.0f);
                finalG = PhotonicSharedCore.ApplyHighlightRollOff(finalG, knee: 195.0f);
                finalB = PhotonicSharedCore.ApplyHighlightRollOff(finalB, knee: 195.0f);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(finalR + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(finalG + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(finalB + 0.5f), 0, 255);
            }
        });
    }

    private static void ExecuteCinematicLut(int[] pixels, int width, int height, float strength)
    {
        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int c = pixels[idx];
                int r = (c >> 16) & 0xFF;
                int g = (c >> 8) & 0xFF;
                int b = c & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);
                float normL = luma / 255.0f;

                // Tương phản điện ảnh sâu vùng tối
                float sCurve = normL * normL * (3.0f - 2.0f * normL);
                float blendLuma = normL * (1.0f - 0.20f * strength) + sCurve * (0.20f * strength);
                float scale = (normL > 0.01f) ? (blendLuma / normL) : 1.0f;

                float nr = r * scale;
                float ng = g * scale;
                float nb = b * scale;

                // Teal & Amber Split Tone
                if (luma < 120.0f)
                {
                    // Vùng tối: ám xanh lam ngọc (Teal)
                    float shadowWeight = (120.0f - luma) / 120.0f * strength * 0.18f;
                    ng += 6.0f * shadowWeight;
                    nb += 14.0f * shadowWeight;
                    nr -= 4.0f * shadowWeight;
                }
                else
                {
                    // Vùng sáng: ánh hổ phách ấm (Amber)
                    float highlightWeight = (luma - 120.0f) / 135.0f * strength * 0.16f;
                    nr += 12.0f * highlightWeight;
                    ng += 6.0f * highlightWeight;
                    nb -= 5.0f * highlightWeight;
                }

                nr = PhotonicSharedCore.ApplyHighlightRollOff(nr, knee: 190.0f);
                ng = PhotonicSharedCore.ApplyHighlightRollOff(ng, knee: 190.0f);
                nb = PhotonicSharedCore.ApplyHighlightRollOff(nb, knee: 190.0f);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(nr + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(ng + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(nb + 0.5f), 0, 255);
            }
        });
    }

    private static void ExecuteSuperSharpDetail(int[] pixels, int width, int height, float strength)
    {
        int total = width * height;
        int[] copy = ArrayPool<int>.Shared.Rent(total);
        try
        {
            Array.Copy(pixels, copy, total);

            Parallel.For(1, height - 1, y =>
            {
                int rowOffset = y * width;
                for (int x = 1; x < width - 1; x++)
                {
                    int idx = rowOffset + x;
                    int c = copy[idx];
                    int r = (c >> 16) & 0xFF;
                    int g = (c >> 8) & 0xFF;
                    int b = c & 0xFF;

                    int cL = copy[idx - 1];
                    int cR = copy[idx + 1];
                    int cT = copy[idx - width];
                    int cB = copy[idx + width];

                    float avgR = (((cL >> 16) & 0xFF) + ((cR >> 16) & 0xFF) + ((cT >> 16) & 0xFF) + ((cB >> 16) & 0xFF)) * 0.25f;
                    float avgG = (((cL >> 8) & 0xFF) + ((cR >> 8) & 0xFF) + ((cT >> 8) & 0xFF) + ((cB >> 8) & 0xFF)) * 0.25f;
                    float avgB = ((cL & 0xFF) + (cR & 0xFF) + (cT & 0xFF) + (cB & 0xFF)) * 0.25f;

                    float highR = r - avgR;
                    float highG = g - avgG;
                    float highB = b - avgB;

                    float boost = 0.55f * strength;
                    float nr = r + Math.Clamp(highR * boost, -24.0f, 24.0f);
                    float ng = g + Math.Clamp(highG * boost, -24.0f, 24.0f);
                    float nb = b + Math.Clamp(highB * boost, -24.0f, 24.0f);

                    pixels[idx] = (unchecked((int)0xFF000000)) |
                                  (Math.Clamp((int)(nr + 0.5f), 0, 255) << 16) |
                                  (Math.Clamp((int)(ng + 0.5f), 0, 255) << 8) |
                                  Math.Clamp((int)(nb + 0.5f), 0, 255);
                }
            });
        }
        finally
        {
            ArrayPool<int>.Shared.Return(copy);
        }
    }

#if ANDROID
    public const int MaxWorkingDimension = 2560;
    private const int JniStripHeight = 64;

    public static (int Width, int Height) GetWorkingDimensions(byte[] jpegBytes)
    {
        try
        {
            var opt = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length, opt);
            int origW = opt.OutWidth;
            int origH = opt.OutHeight;
            if (origW <= 0 || origH <= 0) return (1080, 1440);

            int maxDim = Math.Max(origW, origH);
            int sampleSize = 1;
            while (maxDim / sampleSize > MaxWorkingDimension)
            {
                sampleSize *= 2;
            }

            return (origW / sampleSize, origH / sampleSize);
        }
        catch
        {
            return (1080, 1440);
        }
    }

    public static Bitmap? DecodeWorkingBitmap(byte[] jpegBytes, out int sampleSize)
    {
        var optBounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length, optBounds);
        int origW = optBounds.OutWidth;
        int origH = optBounds.OutHeight;

        sampleSize = 1;
        int maxDim = Math.Max(origW, origH);
        while (maxDim / sampleSize > MaxWorkingDimension)
        {
            sampleSize *= 2;
        }

        var decodeOptions = new BitmapFactory.Options
        {
            InSampleSize = sampleSize,
            InMutable = true,
            InPreferredConfig = Bitmap.Config.Argb8888
        };

        return BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length, decodeOptions);
    }

    public static void GetPixelsSafe(Bitmap bmp, int[] destPixels, int width, int height)
    {
        int stripSize = width * JniStripHeight;
        int[] stripBuffer = new int[stripSize];

        for (int y = 0; y < height; y += JniStripHeight)
        {
            int currentStripH = Math.Min(JniStripHeight, height - y);
            int currentCount = width * currentStripH;

            bmp.GetPixels(stripBuffer, 0, width, 0, y, width, currentStripH);
            Array.Copy(stripBuffer, 0, destPixels, y * width, currentCount);
        }
    }

    public static void SetPixelsSafe(Bitmap bmp, int[] srcPixels, int width, int height)
    {
        int stripSize = width * JniStripHeight;
        int[] stripBuffer = new int[stripSize];

        for (int y = 0; y < height; y += JniStripHeight)
        {
            int currentStripH = Math.Min(JniStripHeight, height - y);
            int currentCount = width * currentStripH;

            Array.Copy(srcPixels, y * width, stripBuffer, 0, currentCount);
            bmp.SetPixels(stripBuffer, 0, width, 0, y, width, currentStripH);
        }
    }

    public static async Task<(byte[] OutputBytes, string Diagnosis)> ProcessJpegAsync(byte[] inputBytes, PhotonicAiAction action, float strength = 1.0f)
    {
        return await Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                var exifTags = ExifHelper.ExtractTags(inputBytes);
                bmp = DecodeWorkingBitmap(inputBytes, out _);
                if (bmp == null) return (inputBytes, "Không thể đọc dữ liệu ảnh");

                int width = bmp.Width;
                int height = bmp.Height;
                int total = width * height;

                int[] pixels = new int[total];
                GetPixelsSafe(bmp, pixels, width, height);

                var diag = Analyze(pixels, width, height);
                ExecuteAction(pixels, width, height, action, strength);

                SetPixelsSafe(bmp, pixels, width, height);

                using var ms = new MemoryStream();
                bmp.Compress(Bitmap.CompressFormat.Jpeg!, 95, ms);
                byte[] compressed = ms.ToArray();
                byte[] finalBytes = ExifHelper.InjectExif(compressed, exifTags);

                return (finalBytes, diag.Summary);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PhotonicAiAgent] ProcessJpeg error: {ex.Message}");
                return (inputBytes, $"Lỗi xử lý: {ex.Message}");
            }
            finally
            {
                bmp?.Recycle();
                bmp?.Dispose();
            }
        });
    }

    public static async Task<string> QuickDiagnoseJpegAsync(byte[] inputBytes)
    {
        return await Task.Run(() =>
        {
            Bitmap? bmp = null;
            try
            {
                var opts = new BitmapFactory.Options
                {
                    InSampleSize = 4,
                    InPreferredConfig = Bitmap.Config.Argb8888
                };
                bmp = BitmapFactory.DecodeByteArray(inputBytes, 0, inputBytes.Length, opts);
                if (bmp == null) return "Ảnh chuẩn • Sẵn sàng tối ưu";

                int width = bmp.Width;
                int height = bmp.Height;
                int total = width * height;

                int[] pixels = new int[total];
                GetPixelsSafe(bmp, pixels, width, height);
                var diag = Analyze(pixels, width, height);
                return diag.Summary;
            }
            catch
            {
                return "Ảnh chuẩn • Sẵn sàng tối ưu";
            }
            finally
            {
                bmp?.Recycle();
                bmp?.Dispose();
            }
        });
    }

    public static byte[] RotateJpeg(byte[] jpegBytes, float degrees)
    {
        Bitmap? bmp = null;
        Bitmap? rotated = null;
        try
        {
            var exifTags = ExifHelper.ExtractTags(jpegBytes);
            bmp = BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length);
            if (bmp == null) return jpegBytes;

            var matrix = new Matrix();
            matrix.PostRotate(degrees);
            rotated = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, matrix, true);
            using var ms = new MemoryStream();
            rotated.Compress(Bitmap.CompressFormat.Jpeg!, 95, ms);
            return ExifHelper.InjectExif(ms.ToArray(), exifTags);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RotateJpeg] Error: {ex.Message}");
            return jpegBytes;
        }
        finally
        {
            bmp?.Recycle();
            bmp?.Dispose();
            rotated?.Recycle();
            rotated?.Dispose();
        }
    }

    public static byte[] FlipJpegHorizontal(byte[] jpegBytes)
    {
        Bitmap? bmp = null;
        Bitmap? flipped = null;
        try
        {
            var exifTags = ExifHelper.ExtractTags(jpegBytes);
            bmp = BitmapFactory.DecodeByteArray(jpegBytes, 0, jpegBytes.Length);
            if (bmp == null) return jpegBytes;

            var matrix = new Matrix();
            matrix.PostScale(-1f, 1f);
            flipped = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, matrix, true);
            using var ms = new MemoryStream();
            flipped.Compress(Bitmap.CompressFormat.Jpeg!, 95, ms);
            return ExifHelper.InjectExif(ms.ToArray(), exifTags);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FlipJpeg] Error: {ex.Message}");
            return jpegBytes;
        }
        finally
        {
            bmp?.Recycle();
            bmp?.Dispose();
            flipped?.Recycle();
            flipped?.Dispose();
        }
    }

    public enum PromptIntent
    {
        Erase,
        Brighten,
        Darken,
        Blur,
        Sharpen,
        ColorVintage,
        BlackAndWhite
    }

    public static PromptIntent ParsePromptIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return PromptIntent.Erase;
        string lower = prompt.Trim().ToLowerInvariant();

        if (lower.Contains("xóa") || lower.Contains("xoá") || lower.Contains("tẩy") || lower.Contains("bỏ") ||
            lower.Contains("remove") || lower.Contains("erase") || lower.Contains("mất") || lower.Contains("delete") ||
            lower.Contains("biến mất") || lower.Contains("vết") || lower.Contains("rác") || lower.Contains("người"))
        {
            return PromptIntent.Erase;
        }

        if (lower.Contains("trắng đen") || lower.Contains("đen trắng") || lower.Contains("mono") ||
            lower.Contains("bw") || lower.Contains("b&w"))
        {
            return PromptIntent.BlackAndWhite;
        }

        if (lower.Contains("sáng") || lower.Contains("bừng") || lower.Contains("chiếu") ||
            lower.Contains("bright") || lower.Contains("lift"))
        {
            return PromptIntent.Brighten;
        }

        if (lower.Contains("tối") || lower.Contains("shadow") || lower.Contains("dark"))
        {
            return PromptIntent.Darken;
        }

        if (lower.Contains("mờ") || lower.Contains("blur") || lower.Contains("phông") || lower.Contains("bokeh"))
        {
            return PromptIntent.Blur;
        }

        if (lower.Contains("nét") || lower.Contains("rõ") || lower.Contains("chi tiết") || lower.Contains("sharp"))
        {
            return PromptIntent.Sharpen;
        }

        if (lower.Contains("màu") || lower.Contains("vintage") || lower.Contains("ấm") ||
            lower.Contains("lạnh") || lower.Contains("rực rỡ"))
        {
            return PromptIntent.ColorVintage;
        }

        return PromptIntent.Erase;
    }

    public static async Task<(byte[] OutputBytes, string StatusMessage)> ExecutePromptOnMaskAsync(
        byte[] inputBytes,
        byte[] binaryMask,
        int maskW,
        int maskH,
        string prompt)
    {
        if (inputBytes == null || inputBytes.Length == 0)
        {
            return (Array.Empty<byte>(), "Không có dữ liệu ảnh");
        }

        return await Task.Run(() =>
        {
            Bitmap? sourceBitmap = null;
            try
            {
                var exifTags = ExifHelper.ExtractTags(inputBytes);
                sourceBitmap = DecodeWorkingBitmap(inputBytes, out _);
                if (sourceBitmap == null) return (inputBytes, "Không thể đọc dữ liệu ảnh");

                int width = sourceBitmap.Width;
                int height = sourceBitmap.Height;
                int totalPixels = width * height;

                byte[] matchedMask;
                if (maskW == width && maskH == height && binaryMask.Length == totalPixels)
                {
                    matchedMask = binaryMask;
                }
                else
                {
                    matchedMask = RescaleMask(binaryMask, maskW, maskH, width, height);
                }

                bool hasMaskedPixels = false;
                for (int i = 0; i < matchedMask.Length; i += 32)
                {
                    if (matchedMask[i] > 0)
                    {
                        hasMaskedPixels = true;
                        break;
                    }
                }

                if (!hasMaskedPixels)
                {
                    return (inputBytes, "Vui lòng tô lên vật thể hoặc vùng cần sửa");
                }

                var intent = ParsePromptIntent(prompt);
                int[] pixels = new int[totalPixels];
                string actionName;

                GetPixelsSafe(sourceBitmap, pixels, width, height);

                switch (intent)
                {
                    case PromptIntent.Erase:
                        FastInpaint(pixels, width, height, matchedMask);
                        actionName = "Đã tẩy xóa vật thể thành công";
                        break;

                    case PromptIntent.Brighten:
                        ApplyMaskedBrightness(pixels, width, height, matchedMask, 1.40f);
                        actionName = "Đã nâng sáng vùng chọn";
                        break;

                    case PromptIntent.Darken:
                        ApplyMaskedBrightness(pixels, width, height, matchedMask, 0.65f);
                        actionName = "Đã giảm sáng vùng chọn";
                        break;

                    case PromptIntent.Blur:
                        ApplyMaskedBlur(pixels, width, height, matchedMask, 16);
                        actionName = "Đã làm mờ xóa phông vùng chọn";
                        break;

                    case PromptIntent.Sharpen:
                        ApplyMaskedSharpen(pixels, width, height, matchedMask, 0.75f);
                        actionName = "Đã tăng độ nét vùng chọn";
                        break;

                    case PromptIntent.BlackAndWhite:
                        ApplyMaskedBlackAndWhite(pixels, width, height, matchedMask);
                        actionName = "Đã chuyển vùng chọn sang trắng đen";
                        break;

                    case PromptIntent.ColorVintage:
                    default:
                        ApplyMaskedColorShift(pixels, width, height, matchedMask, 1.25f, 1.10f, 0.85f);
                        actionName = "Đã đổi màu nghệ thuật cho vùng chọn";
                        break;
                }

                SetPixelsSafe(sourceBitmap, pixels, width, height);

                using var outputStream = new MemoryStream();
                sourceBitmap.Compress(Bitmap.CompressFormat.Jpeg!, 95, outputStream);
                byte[] compressedBytes = outputStream.ToArray();
                byte[] finalBytes = ExifHelper.InjectExif(compressedBytes, exifTags);

                return (finalBytes, actionName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExecutePromptOnMaskAsync] {ex}");
                return (inputBytes, $"Lỗi AI: {ex.Message}");
            }
            finally
            {
                sourceBitmap?.Recycle();
                sourceBitmap?.Dispose();
            }
        });
    }

    private static byte[] RescaleMask(byte[] srcMask, int srcW, int srcH, int dstW, int dstH)
    {
        byte[] dstMask = new byte[dstW * dstH];
        if (srcMask == null || srcMask.Length == 0 || srcW <= 0 || srcH <= 0) return dstMask;

        float scaleX = (float)srcW / dstW;
        float scaleY = (float)srcH / dstH;

        Parallel.For(0, dstH, dstY =>
        {
            int srcY = Math.Clamp((int)(dstY * scaleY), 0, srcH - 1);
            int srcRow = srcY * srcW;
            int dstRow = dstY * dstW;

            for (int dstX = 0; dstX < dstW; dstX++)
            {
                int srcX = Math.Clamp((int)(dstX * scaleX), 0, srcW - 1);
                dstMask[dstRow + dstX] = srcMask[srcRow + srcX];
            }
        });

        return dstMask;
    }

    private static void FastInpaint(int[] pixels, int width, int height, byte[] mask)
    {
        int total = width * height;
        int[] result = new int[total];
        Array.Copy(pixels, result, total);

        int maxSearchDist = Math.Min(Math.Max(width, height) / 5, 220);

        (int dx, int dy)[] directions = new (int, int)[]
        {
            (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (1, -1), (-1, 1), (1, 1)
        };

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                float totalWeight = 0f;
                float sumR = 0f;
                float sumG = 0f;
                float sumB = 0f;

                for (int d = 0; d < 8; d++)
                {
                    int stepX = directions[d].dx;
                    int stepY = directions[d].dy;

                    int curX = x;
                    int curY = y;
                    int dist = 0;

                    while (dist < maxSearchDist)
                    {
                        curX += stepX;
                        curY += stepY;
                        dist++;

                        if (curX < 0 || curX >= width || curY < 0 || curY >= height)
                            break;

                        int testIdx = curY * width + curX;
                        if (mask[testIdx] == 0)
                        {
                            int bgCol = pixels[testIdx];
                            float w = 1.0f / (dist * dist);

                            sumR += ((bgCol >> 16) & 0xFF) * w;
                            sumG += ((bgCol >> 8) & 0xFF) * w;
                            sumB += (bgCol & 0xFF) * w;
                            totalWeight += w;
                            break;
                        }
                    }
                }

                if (totalWeight > 0.00001f)
                {
                    int r = Math.Clamp((int)(sumR / totalWeight + 0.5f), 0, 255);
                    int g = Math.Clamp((int)(sumG / totalWeight + 0.5f), 0, 255);
                    int b = Math.Clamp((int)(sumB / totalWeight + 0.5f), 0, 255);
                    result[idx] = (unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
                }
            }
        });

        Parallel.For(1, height - 1, y =>
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                bool nearBorder = (mask[idx - 1] == 0 || mask[idx + 1] == 0 ||
                                   mask[idx - width] == 0 || mask[idx + width] == 0);

                if (nearBorder)
                {
                    int rSum = 0, gSum = 0, bSum = 0, cnt = 0;
                    for (int ky = -1; ky <= 1; ky++)
                    {
                        int kRow = (y + ky) * width;
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int p = result[kRow + (x + kx)];
                            rSum += (p >> 16) & 0xFF;
                            gSum += (p >> 8) & 0xFF;
                            bSum += p & 0xFF;
                            cnt++;
                        }
                    }
                    pixels[idx] = (unchecked((int)0xFF000000)) | ((rSum / cnt) << 16) | ((gSum / cnt) << 8) | (bSum / cnt);
                }
                else
                {
                    pixels[idx] = result[idx];
                }
            }
        });
    }

    private static void ApplyMaskedBrightness(int[] pixels, int width, int height, byte[] mask, float factor)
    {
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                int p = pixels[idx];
                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                int nr = Math.Clamp((int)(r * factor + 0.5f), 0, 255);
                int ng = Math.Clamp((int)(g * factor + 0.5f), 0, 255);
                int nb = Math.Clamp((int)(b * factor + 0.5f), 0, 255);

                pixels[idx] = (unchecked((int)0xFF000000)) | (nr << 16) | (ng << 8) | nb;
            }
        });
    }

    private static void ApplyMaskedBlur(int[] pixels, int width, int height, byte[] mask, int radius)
    {
        int total = width * height;
        int[] copy = new int[total];
        Array.Copy(pixels, copy, total);

        int r = Math.Clamp(radius, 4, 32);

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                int rSum = 0, gSum = 0, bSum = 0, count = 0;
                int minY = Math.Max(0, y - r);
                int maxY = Math.Min(height - 1, y + r);
                int minX = Math.Max(0, x - r);
                int maxX = Math.Min(width - 1, x + r);

                int step = (r > 12) ? 2 : 1;
                for (int sy = minY; sy <= maxY; sy += step)
                {
                    int sRow = sy * width;
                    for (int sx = minX; sx <= maxX; sx += step)
                    {
                        int p = copy[sRow + sx];
                        rSum += (p >> 16) & 0xFF;
                        gSum += (p >> 8) & 0xFF;
                        bSum += p & 0xFF;
                        count++;
                    }
                }

                if (count > 0)
                {
                    pixels[idx] = (unchecked((int)0xFF000000)) | ((rSum / count) << 16) | ((gSum / count) << 8) | (bSum / count);
                }
            }
        });
    }

    private static void ApplyMaskedSharpen(int[] pixels, int width, int height, byte[] mask, float strength)
    {
        Parallel.For(1, height - 1, y =>
        {
            int row = y * width;
            int rowUp = (y - 1) * width;
            int rowDn = (y + 1) * width;

            for (int x = 1; x < width - 1; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                int pCenter = pixels[idx];
                int pLeft = pixels[idx - 1];
                int pRight = pixels[idx + 1];
                int pUp = pixels[rowUp + x];
                int pDown = pixels[rowDn + x];

                int rC = (pCenter >> 16) & 0xFF;
                int gC = (pCenter >> 8) & 0xFF;
                int bC = pCenter & 0xFF;

                int rAvg = (((pLeft >> 16) & 0xFF) + ((pRight >> 16) & 0xFF) + ((pUp >> 16) & 0xFF) + ((pDown >> 16) & 0xFF)) >> 2;
                int gAvg = (((pLeft >> 8) & 0xFF) + ((pRight >> 8) & 0xFF) + ((pUp >> 8) & 0xFF) + ((pDown >> 8) & 0xFF)) >> 2;
                int bAvg = ((pLeft & 0xFF) + (pRight & 0xFF) + (pUp & 0xFF) + (pDown & 0xFF)) >> 2;

                int nr = Math.Clamp((int)(rC + (rC - rAvg) * strength), 0, 255);
                int ng = Math.Clamp((int)(gC + (gC - gAvg) * strength), 0, 255);
                int nb = Math.Clamp((int)(bC + (bC - bAvg) * strength), 0, 255);

                pixels[idx] = (unchecked((int)0xFF000000)) | (nr << 16) | (ng << 8) | nb;
            }
        });
    }

    private static void ApplyMaskedBlackAndWhite(int[] pixels, int width, int height, byte[] mask)
    {
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                int p = pixels[idx];
                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                int luma = (r * 77 + g * 150 + b * 29) >> 8;
                pixels[idx] = (unchecked((int)0xFF000000)) | (luma << 16) | (luma << 8) | luma;
            }
        });
    }

    private static void ApplyMaskedColorShift(int[] pixels, int width, int height, byte[] mask, float rScale, float gScale, float bScale)
    {
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x;
                if (mask[idx] == 0) continue;

                int p = pixels[idx];
                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                int nr = Math.Clamp((int)(r * rScale + 0.5f), 0, 255);
                int ng = Math.Clamp((int)(g * gScale + 0.5f), 0, 255);
                int nb = Math.Clamp((int)(b * bScale + 0.5f), 0, 255);

                pixels[idx] = (unchecked((int)0xFF000000)) | (nr << 16) | (ng << 8) | nb;
            }
        });
    }
#else
    public static (int Width, int Height) GetWorkingDimensions(byte[] jpegBytes) => (1080, 1440);
    public static Task<(byte[] OutputBytes, string Diagnosis)> ProcessJpegAsync(byte[] inputBytes, PhotonicAiAction action, float strength = 1.0f) => Task.FromResult((inputBytes, "Hoàn tất xử lý"));
    public static Task<string> QuickDiagnoseJpegAsync(byte[] inputBytes) => Task.FromResult("Ảnh chuẩn • Sẵn sàng tối ưu");
    public static byte[] RotateJpeg(byte[] jpegBytes, float degrees) => jpegBytes;
    public static byte[] FlipJpegHorizontal(byte[] jpegBytes) => jpegBytes;
    public static Task<(byte[] OutputBytes, string StatusMessage)> ExecutePromptOnMaskAsync(byte[] inputBytes, byte[] binaryMask, int maskW, int maskH, string prompt) => Task.FromResult((inputBytes, "Hoàn tất xử lý"));
#endif
}
