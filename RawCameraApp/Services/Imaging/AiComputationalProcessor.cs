using System.Buffers;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Động cơ tính toán xử lý ảnh AI chuyên sâu v4 (Synchronized Deep Computational AI Engine).
/// Đồng bộ hoá 3 tầng xử lý nối tiếp (Pipeline Synergy):
///   Tầng 1: OnnxPhotonicEngine (Mạng nơ-ron sâu trên Qualcomm Hexagon NPU / CPU)
///   Tầng 2: RetinexPhotonicEngine (MSR-GF + Leica Micro-Contrast Acutance)
///   Tầng 3: Phân hệ chuyên biệt (Portrait / NightSight / Landscape / SuperDetail) đọc từ bộ đệm trung gian `stagePixels`
///           để kế thừa 100% kết quả của Tầng 1 + Tầng 2 mà không ghi đè mất dữ liệu!
/// Toàn bộ hàm toán học cơ sở dùng chung qua PhotonicSharedCore.
/// </summary>
public static class AiComputationalProcessor
{
    public static void Process(
        int[] pixels,
        int width,
        int height,
        AiCameraMode mode,
        float strength,
        AiTelemetrySnapshot? telemetry,
        int iso,
        float focusNormX = 0.5f,
        float focusNormY = 0.45f,
        CameraLensProfile? lensProfile = null)
    {
        if (strength <= 0.01f || pixels == null || pixels.Length == 0) return;
        strength = Math.Clamp(strength, 0.0f, 1.0f);

        int totalPixels = width * height;

        // Tự động điều chỉnh ISO hiệu dụng theo kích thước cảm biến của từng ống kính
        int effectiveIso = (int)(iso * (lensProfile?.IsoNoiseMultiplier ?? 1.0f));

        // Ưu tiên tâm chủ thể từ khuôn mặt nhận diện được nếu có
        float actualFocusX = (telemetry != null && telemetry.FaceCount > 0) ? telemetry.FaceNormX : focusNormX;
        float actualFocusY = (telemetry != null && telemetry.FaceCount > 0) ? telemetry.FaceNormY : focusNormY;

        // TẦNG 1: Áp dụng Mạng nơ-ron AI On-Device (Qualcomm Hexagon NPU / ONNX Runtime)
        bool onnxRan = OnnxPhotonicEngine.Enhance(pixels, width, height, strength * 0.85f);

        // TẦNG 2: Áp dụng MSR-GF Photonic Core (NASA Multi-Scale Retinex + Fast Guided Filter)
        bool enableLeica = (mode == AiCameraMode.Landscape) ||
                           (lensProfile != null && (lensProfile.IsSensorCropZoom || lensProfile.LensType == CameraLensType.Macro));

        RetinexPhotonicEngine.Enhance(
            pixels,
            width,
            height,
            onnxRan ? (strength * 0.50f) : (strength * 0.75f),
            effectiveIso,
            enableLeicaAcutance: enableLeica,
            colorRestorationWeight: 0.90f);

        // TẦNG 3: Sao lưu kết quả sau Tầng 1 + Tầng 2 vào `stagePixels` (mượn từ ArrayPool)
        // để các bộ lọc chuyên biệt (Portrait, NightSight, Landscape, SuperDetail) xử lý nối tiếp trên nền ảnh đã nâng cấp!
        int[] stagePixels = ArrayPool<int>.Shared.Rent(totalPixels);
        try
        {
            Array.Copy(pixels, stagePixels, totalPixels);

            // Tự động tối ưu theo ống kính Macro trong chế độ Tự động
            if (mode == AiCameraMode.Auto && lensProfile?.LensType == CameraLensType.Macro)
            {
                ProcessSuperDetail(pixels, stagePixels, width, height, strength * 0.90f, lensProfile?.EdgeAcutanceBoost ?? 0.35f);
            }
            else
            {
                switch (mode)
                {
                    case AiCameraMode.Auto:
                        ProcessAuto(pixels, stagePixels, width, height, strength * 0.65f, telemetry, effectiveIso, actualFocusX, actualFocusY, lensProfile);
                        break;
                    case AiCameraMode.Portrait:
                        ProcessPortrait(pixels, stagePixels, width, height, strength, actualFocusX, actualFocusY, lensProfile?.LensType == CameraLensType.FrontSelfie);
                        break;
                    case AiCameraMode.NightSight:
                        ProcessNightSight(pixels, stagePixels, width, height, strength * 0.60f, effectiveIso);
                        break;
                    case AiCameraMode.Landscape:
                        ProcessLandscape(pixels, stagePixels, width, height, strength * 0.70f);
                        break;
                    case AiCameraMode.SuperDetail:
                        ProcessSuperDetail(pixels, stagePixels, width, height, strength * 0.65f, lensProfile?.EdgeAcutanceBoost ?? 0.0f);
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stagePixels);
        }
    }

    private static void ProcessAuto(
        int[] pixels,
        int[] src,
        int width,
        int height,
        float strength,
        AiTelemetrySnapshot? telemetry,
        int iso,
        float focusNormX,
        float focusNormY,
        CameraLensProfile? lensProfile)
    {
        var scene = telemetry?.DetectedScene ?? AiDetectedScene.Neutral;

        switch (scene)
        {
            case AiDetectedScene.Portrait:
                ProcessPortrait(pixels, src, width, height, strength, focusNormX, focusNormY, lensProfile?.LensType == CameraLensType.FrontSelfie);
                break;
            case AiDetectedScene.NightLowLight:
                ProcessNightSight(pixels, src, width, height, strength, iso);
                break;
            case AiDetectedScene.LandscapeSky:
            case AiDetectedScene.FoliageNature:
                ProcessLandscape(pixels, src, width, height, strength);
                break;
            case AiDetectedScene.BacklitHdr:
            case AiDetectedScene.FoodWarm:
            default:
                ProcessSuperDetail(pixels, src, width, height, strength * 0.80f, lensProfile?.EdgeAcutanceBoost ?? 0.0f);
                break;
        }
    }

    /// <summary>
    /// 1. AI Portrait &amp; Beauty: Làm mịn da tách tần số kép (Pore-Preserving) + Tông da hồng hào + Bokeh quang học
    /// </summary>
    private static void ProcessPortrait(
        int[] pixels,
        int[] src,
        int width,
        int height,
        float strength,
        float focusNormX,
        float focusNormY,
        bool isSelfieCamera = false)
    {
        float cX = width * Math.Clamp(focusNormX, 0.05f, 0.95f);
        float cY = height * Math.Clamp(focusNormY, 0.05f, 0.95f);
        float maxDist = MathF.Sqrt(Math.Max(cX, width - cX) * Math.Max(cX, width - cX) +
                                   Math.Max(cY, height - cY) * Math.Max(cY, height - cY));

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            float dy = y - cY;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int p = src[idx];

                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);
                float skinWeight = PhotonicSharedCore.ComputeSkinWeight(r, g, b);

                float finalR = r;
                float finalG = g;
                float finalB = b;

                // A. Làm mịn da tách tần số kép (Dual-Frequency Pore-Preserving Skin Filter)
                if (skinWeight > 0.05f && x >= 3 && x < width - 3 && y >= 3 && y < height - 3)
                {
                    int sumR = r * 2, sumG = g * 2, sumB = b * 2, wTotal = 2;

                    int[] offsets =
                    {
                        -2 * width, 2 * width, -2, 2,
                        -width - 1, -width + 1, width - 1, width + 1
                    };

                    for (int k = 0; k < offsets.Length; k++)
                    {
                        int np = src[idx + offsets[k]];
                        int nr = (np >> 16) & 0xFF;
                        int ng = (np >> 8) & 0xFF;
                        int nb = np & 0xFF;

                        int diff = Math.Abs(nr - r) + Math.Abs(ng - g) + Math.Abs(nb - b);
                        if (diff < 42)
                        {
                            sumR += nr;
                            sumG += ng;
                            sumB += nb;
                            wTotal++;
                        }
                    }

                    float lowFreqR = (float)sumR / wTotal;
                    float lowFreqG = (float)sumG / wTotal;
                    float lowFreqB = (float)sumB / wTotal;

                    float highFreqR = r - lowFreqR;
                    float highFreqG = g - lowFreqG;
                    float highFreqB = b - lowFreqB;

                    float smoothFactor = skinWeight * 0.58f * strength;
                    float baseR = r * (1.0f - smoothFactor) + lowFreqR * smoothFactor;
                    float baseG = g * (1.0f - smoothFactor) + lowFreqG * smoothFactor;
                    float baseB = b * (1.0f - smoothFactor) + lowFreqB * smoothFactor;

                    finalR = baseR + highFreqR * 0.35f;
                    finalG = baseG + highFreqG * 0.35f;
                    finalB = baseB + highFreqB * 0.35f;

                    if (luma < 205.0f)
                    {
                        float glow = skinWeight * strength * ((205.0f - luma) / 205.0f);
                        finalR += 4.5f * glow;
                        finalG += 2.2f * glow;
                        finalB += 1.8f * glow;
                    }
                }

                // B. Hiệu ứng xóa phông quang học mềm xung quanh chủ thể
                float dx = x - cX;
                float distRatio = MathF.Sqrt(dx * dx + dy * dy) / Math.Max(maxDist, 1.0f);

                if (distRatio > 0.52f && skinWeight < 0.2f && x >= 4 && x < width - 4 && y >= 4 && y < height - 4)
                {
                    float bokehWeight = Math.Clamp((distRatio - 0.52f) / 0.48f, 0.0f, 1.0f) * strength * 0.65f;

                    int p1 = src[idx - 3];
                    int p2 = src[idx + 3];
                    int p3 = src[idx - 3 * width];
                    int p4 = src[idx + 3 * width];
                    int p5 = src[idx - 2 * width - 2];
                    int p6 = src[idx + 2 * width + 2];

                    float blurR = (((p1 >> 16) & 0xFF) + ((p2 >> 16) & 0xFF) + ((p3 >> 16) & 0xFF) +
                                   ((p4 >> 16) & 0xFF) + ((p5 >> 16) & 0xFF) + ((p6 >> 16) & 0xFF)) / 6.0f;
                    float blurG = (((p1 >> 8) & 0xFF) + ((p2 >> 8) & 0xFF) + ((p3 >> 8) & 0xFF) +
                                   ((p4 >> 8) & 0xFF) + ((p5 >> 8) & 0xFF) + ((p6 >> 8) & 0xFF)) / 6.0f;
                    float blurB = ((p1 & 0xFF) + (p2 & 0xFF) + (p3 & 0xFF) +
                                   (p4 & 0xFF) + (p5 & 0xFF) + (p6 & 0xFF)) / 6.0f;

                    finalR = finalR * (1.0f - bokehWeight) + blurR * bokehWeight;
                    finalG = finalG * (1.0f - bokehWeight) + blurG * bokehWeight;
                    finalB = finalB * (1.0f - bokehWeight) + blurB * bokehWeight;
                }

                float knee = isSelfieCamera ? 186.0f : 195.0f;
                finalR = PhotonicSharedCore.ApplyHighlightRollOff(finalR, knee: knee);
                finalG = PhotonicSharedCore.ApplyHighlightRollOff(finalG, knee: knee);
                finalB = PhotonicSharedCore.ApplyHighlightRollOff(finalB, knee: knee);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(finalR + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(finalG + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(finalB + 0.5f), 0, 255);
            }
        });
    }

    /// <summary>
    /// 2. AI Night Sight: Cân bằng tương phản vi mô đêm + Bảo vệ đen sâu nguyên bản + Khử nhiễu Chroma
    /// </summary>
    private static void ProcessNightSight(int[] pixels, int[] src, int width, int height, float strength, int iso)
    {
        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int p = src[idx];

                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);

                // Giữ đen sâu nguyên bản cho vùng tối thật sự (luma < 10) để chống bệt xám ảnh đêm
                float lift = 0f;
                if (luma > 8.0f && luma < 90.0f)
                {
                    float darkFactor = (90.0f - luma) / 82.0f;
                    lift = MathF.Sin(darkFactor * MathF.PI * 0.5f) * 12.0f * strength;
                }

                float newLuma = Math.Clamp(luma + lift, 0.0f, 255.0f);
                float scale = (luma > 1.0f) ? (newLuma / luma) : 1.0f;
                scale = Math.Clamp(scale, 0.90f, 1.25f);

                float finalR = r * scale;
                float finalG = g * scale;
                float finalB = b * scale;

                // Khử nhiễu hạt màu Chroma tập trung ở vùng tối ISO cao
                PhotonicSharedCore.ApplyChromaDenoise(ref finalR, ref finalG, ref finalB, newLuma, Math.Max(iso, 800), strength * 1.2f);

                finalR = PhotonicSharedCore.ApplyHighlightRollOff(finalR);
                finalG = PhotonicSharedCore.ApplyHighlightRollOff(finalG);
                finalB = PhotonicSharedCore.ApplyHighlightRollOff(finalB);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(finalR + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(finalG + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(finalB + 0.5f), 0, 255);
            }
        });
    }

    /// <summary>
    /// 3. AI Semantic Landscape: Bầu trời trong xanh sâu thẳm + Tán lá tươi mát + Khử sương mù nhẹ
    /// </summary>
    private static void ProcessLandscape(int[] pixels, int[] src, int width, int height, float strength)
    {
        int skyHorizonLimit = (int)(height * 0.70f);

        Parallel.For(0, height, y =>
        {
            int rowOffset = y * width;
            bool isSkyRegion = (y < skyHorizonLimit);

            for (int x = 0; x < width; x++)
            {
                int idx = rowOffset + x;
                int p = src[idx];

                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                float finalR = r;
                float finalG = g;
                float finalB = b;

                if (isSkyRegion && b > r + 12 && b >= g)
                {
                    float skyDepth = Math.Clamp((b - r - 12.0f) / 50.0f, 0.0f, 1.0f) * strength;
                    finalB = Math.Min(finalB * (1.0f + 0.18f * skyDepth), 250.0f);
                    finalG = Math.Min(finalG * (1.0f + 0.05f * skyDepth), 248.0f);
                    finalR *= (1.0f - 0.07f * skyDepth);
                }

                if (g > r * 1.06f && g > b * 1.04f && g > 25)
                {
                    float leafDepth = Math.Clamp((g - Math.Max(r, b)) / 45.0f, 0.0f, 1.0f) * strength;
                    finalG = Math.Min(finalG * (1.0f + 0.16f * leafDepth), 250.0f);
                    finalR *= (1.0f - 0.05f * leafDepth);
                    finalB *= (1.0f - 0.05f * leafDepth);
                }

                float minVal = Math.Min(finalR, Math.Min(finalG, finalB));
                float maxVal = Math.Max(finalR, Math.Max(finalG, finalB));
                if (minVal > 40.0f && maxVal < 200.0f && (maxVal - minVal) < 40.0f)
                {
                    float hazeReduction = (40.0f - (maxVal - minVal)) * 0.12f * strength;
                    finalR -= hazeReduction;
                    finalG -= hazeReduction;
                    finalB -= hazeReduction;
                }

                finalR = PhotonicSharedCore.ApplyHighlightRollOff(finalR);
                finalG = PhotonicSharedCore.ApplyHighlightRollOff(finalG);
                finalB = PhotonicSharedCore.ApplyHighlightRollOff(finalB);

                pixels[idx] = (unchecked((int)0xFF000000)) |
                              (Math.Clamp((int)(finalR + 0.5f), 0, 255) << 16) |
                              (Math.Clamp((int)(finalG + 0.5f), 0, 255) << 8) |
                              Math.Clamp((int)(finalB + 0.5f), 0, 255);
            }
        });
    }

    /// <summary>
    /// 4. AI Super Detail &amp; Clarity: Tăng cường độ nét vi mô không viền sáng (Anti-Halo Unsharp)
    /// </summary>
    private static void ProcessSuperDetail(int[] pixels, int[] src, int width, int height, float strength, float acutanceBoost = 0.0f)
    {
        float effectiveMultiplier = (0.35f + acutanceBoost * 0.25f) * strength;
        Parallel.For(1, height - 1, y =>
        {
            int rowOffset = y * width;
            int prevRow = (y - 1) * width;
            int nextRow = (y + 1) * width;

            for (int x = 1; x < width - 1; x++)
            {
                int idx = rowOffset + x;
                int p = src[idx];

                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);

                int lumaTop = PhotonicSharedCore.ComputeFastLumaInt(src[prevRow + x]);
                int lumaBot = PhotonicSharedCore.ComputeFastLumaInt(src[nextRow + x]);
                int lumaLeft = PhotonicSharedCore.ComputeFastLumaInt(src[rowOffset + x - 1]);
                int lumaRight = PhotonicSharedCore.ComputeFastLumaInt(src[rowOffset + x + 1]);

                float avgNeighbor = (lumaTop + lumaBot + lumaLeft + lumaRight) * 0.25f;
                float clampedEdge = Math.Clamp(luma - avgNeighbor, -18.0f, 18.0f);

                if (Math.Abs(clampedEdge) > 2.0f)
                {
                    float detailBoost = clampedEdge * effectiveMultiplier;
                    float newLuma = Math.Clamp(luma + detailBoost, 0.0f, 255.0f);
                    float scale = (luma > 1.0f) ? (newLuma / luma) : 1.0f;
                    scale = Math.Clamp(scale, 0.75f, 1.35f);

                    float finalR = PhotonicSharedCore.ApplyHighlightRollOff(r * scale);
                    float finalG = PhotonicSharedCore.ApplyHighlightRollOff(g * scale);
                    float finalB = PhotonicSharedCore.ApplyHighlightRollOff(b * scale);

                    pixels[idx] = (unchecked((int)0xFF000000)) |
                                  (Math.Clamp((int)(finalR + 0.5f), 0, 255) << 16) |
                                  (Math.Clamp((int)(finalG + 0.5f), 0, 255) << 8) |
                                  Math.Clamp((int)(finalB + 0.5f), 0, 255);
                }
                else
                {
                    pixels[idx] = p;
                }
            }
        });
    }
}
