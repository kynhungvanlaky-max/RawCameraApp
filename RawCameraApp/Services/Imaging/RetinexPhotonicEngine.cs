using System.Buffers;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Động cơ Quang học MSR-GF (Multi-Scale Retinex with Fast Guided Filter &amp; Color Restoration)
/// kết hợp Leica-Style Micro-Contrast Acutance.
/// Đồng bộ hoá toàn diện với PhotonicSharedCore (BoxBlur2D, SampleBilinear, HighlightRollOff, ChromaDenoise).
/// </summary>
public static class RetinexPhotonicEngine
{
    private const int DownsampleShift = 2; // Lưới 1/4 độ phân giải (2^2 = 4)

    public static void Enhance(
        int[] pixels,
        int width,
        int height,
        float strength,
        int iso,
        bool enableLeicaAcutance = true,
        float colorRestorationWeight = 0.85f)
    {
        if (pixels == null || pixels.Length == 0 || strength <= 0.01f) return;
        strength = Math.Clamp(strength, 0.0f, 1.0f);

        int gw = Math.Max(4, width >> DownsampleShift);
        int gh = Math.Max(4, height >> DownsampleShift);
        int gridLen = gw * gh;

        float[] guideLuma = ArrayPool<float>.Shared.Rent(gridLen);
        float[] fineIllum = ArrayPool<float>.Shared.Rent(gridLen);
        float[] medIllum = ArrayPool<float>.Shared.Rent(gridLen);
        float[] coarseIllum = ArrayPool<float>.Shared.Rent(gridLen);
        float[] msrReflectance = ArrayPool<float>.Shared.Rent(gridLen);
        float[] tempBuf = ArrayPool<float>.Shared.Rent(gridLen);

        try
        {
            // BƯỚC 1: Lấy mẫu độ sáng Luma chuẩn hóa [0.001 .. 1.0] trên lưới 1/4
            Parallel.For(0, gh, gy =>
            {
                int yStart = gy << DownsampleShift;
                int yEnd = Math.Min(yStart + (1 << DownsampleShift), height);
                int rowOffset = gy * gw;

                for (int gx = 0; gx < gw; gx++)
                {
                    int xStart = gx << DownsampleShift;
                    int xEnd = Math.Min(xStart + (1 << DownsampleShift), width);

                    float sumLuma = 0;
                    int count = 0;

                    for (int y = yStart; y < yEnd; y += 2)
                    {
                        int srcRow = y * width;
                        for (int x = xStart; x < xEnd; x += 2)
                        {
                            sumLuma += PhotonicSharedCore.ComputeFastLumaInt(pixels[srcRow + x]) * (1.0f / 255.0f);
                            count++;
                        }
                    }

                    guideLuma[rowOffset + gx] = Math.Clamp(count > 0 ? sumLuma / count : 0.5f, 0.002f, 1.0f);
                }
            });

            // BƯỚC 2: Tính 3 tầng ánh sáng (Multi-Scale Illumination) bằng Fast Guided Filter
            FastGuidedFilterSelf(guideLuma, fineIllum, tempBuf, gw, gh, radius: 2, eps: 0.004f);
            FastGuidedFilterSelf(guideLuma, medIllum, tempBuf, gw, gh, radius: 7, eps: 0.015f);
            FastGuidedFilterSelf(guideLuma, coarseIllum, tempBuf, gw, gh, radius: 18, eps: 0.035f);

            // BƯỚC 3: Hợp nhất Multi-Scale Retinex (MSR) trong miền Logarit & tính Bản đồ Gain Quang học
            for (int i = 0; i < gridLen; i++)
            {
                float I = guideLuma[i];
                float L1 = Math.Max(fineIllum[i], 0.002f);
                float L2 = Math.Max(medIllum[i], 0.002f);
                float L3 = Math.Max(coarseIllum[i], 0.002f);

                float logI = MathF.Log(I + 0.01f);
                float rFine = logI - MathF.Log(L1 + 0.01f);
                float rMed = logI - MathF.Log(L2 + 0.01f);
                float rCoarse = logI - MathF.Log(L3 + 0.01f);

                float msrDetail = (0.30f * rFine + 0.45f * rMed + 0.25f * rCoarse) * strength;

                float baseIllum = 0.5f * L2 + 0.5f * L3;
                float shadowGain = 1.0f;
                if (baseIllum < 0.42f)
                {
                    float darkFactor = (0.42f - baseIllum) / 0.42f;
                    shadowGain = 1.0f + darkFactor * darkFactor * 0.68f * strength;
                }
                else if (baseIllum > 0.76f)
                {
                    float brightFactor = (baseIllum - 0.76f) / 0.24f;
                    shadowGain = 1.0f - brightFactor * 0.16f * strength;
                }

                float detailMultiplier = MathF.Exp(Math.Clamp(msrDetail * 0.85f, -0.35f, 0.45f));
                msrReflectance[i] = Math.Clamp(shadowGain * detailMultiplier, 0.72f, 2.05f);
            }

            // Làm mượt nhẹ bản đồ Gain cuối cùng bằng Fast Guided Filter để tuyệt đối không gợn nhiễu
            FastGuidedFilterGuide(guideLuma, msrReflectance, fineIllum, tempBuf, gw, gh, radius: 2, eps: 0.008f);

            // BƯỚC 4: Tái tạo điểm ảnh độ phân giải đầy đủ + Khôi phục màu MSRCR + Leica Micro-Contrast
            Parallel.For(0, height, y =>
            {
                int rowOffset = y * width;
                int prevRow = Math.Max(0, y - 1) * width;
                int nextRow = Math.Min(height - 1, y + 1) * width;
                float normY = (float)y / Math.Max(height - 1, 1);

                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + x;
                    int p = pixels[idx];

                    int r = (p >> 16) & 0xFF;
                    int g = (p >> 8) & 0xFF;
                    int b = p & 0xFF;

                    float luma = PhotonicSharedCore.ComputeLuma(r, g, b);

                    float normX = (float)x / Math.Max(width - 1, 1);
                    float photonicGain = PhotonicSharedCore.SampleBilinear(fineIllum, gw, gh, normX, normY);

                    if (luma > 180.0f && photonicGain > 1.0f)
                    {
                        float hiFade = Math.Clamp((240.0f - luma) / 60.0f, 0.0f, 1.0f);
                        photonicGain = 1.0f + (photonicGain - 1.0f) * hiFade;
                    }

                    // Leica-Style Micro-Contrast Acutance
                    float microGain = 1.0f;
                    if (enableLeicaAcutance && x > 0 && x < width - 1 && y > 0 && y < height - 1)
                    {
                        int lTop = PhotonicSharedCore.ComputeFastLumaInt(pixels[prevRow + x]);
                        int lBot = PhotonicSharedCore.ComputeFastLumaInt(pixels[nextRow + x]);
                        int lLeft = PhotonicSharedCore.ComputeFastLumaInt(pixels[rowOffset + x - 1]);
                        int lRight = PhotonicSharedCore.ComputeFastLumaInt(pixels[rowOffset + x + 1]);

                        float localMean = (lTop + lBot + lLeft + lRight) * 0.25f;
                        float highFreq = Math.Clamp(luma - localMean, -16.0f, 16.0f);

                        if (Math.Abs(highFreq) > 2.2f && luma > 18.0f)
                        {
                            float acutanceStrength = 0.28f * strength;
                            float boostedLuma = Math.Clamp(luma + highFreq * acutanceStrength, 1.0f, 254.0f);
                            microGain = Math.Clamp(boostedLuma / Math.Max(luma, 1.0f), 0.85f, 1.22f);
                        }
                    }

                    float totalGain = photonicGain * microGain;

                    float outR = r * totalGain;
                    float outG = g * totalGain;
                    float outB = b * totalGain;

                    // Khôi phục màu sắc Retinex (MSRCR Color Restoration)
                    if (totalGain > 1.05f && luma > 12.0f && luma < 210.0f)
                    {
                        float outLuma = PhotonicSharedCore.ComputeLuma(outR, outG, outB);
                        float vibranceBoost = 1.0f + Math.Min((totalGain - 1.0f) * 0.22f * colorRestorationWeight, 0.16f);
                        outR = outLuma + (outR - outLuma) * vibranceBoost;
                        outG = outLuma + (outG - outLuma) * vibranceBoost;
                        outB = outLuma + (outB - outLuma) * vibranceBoost;
                    }

                    // Khử nhiễu hạt màu vùng tối (Chroma Noise Suppression) dùng chung
                    PhotonicSharedCore.ApplyChromaDenoise(ref outR, ref outG, ref outB, luma, iso, strength);

                    // Nén vùng sáng cực đại theo đường cong Filmic Knee dùng chung
                    outR = PhotonicSharedCore.ApplyHighlightRollOff(outR);
                    outG = PhotonicSharedCore.ApplyHighlightRollOff(outG);
                    outB = PhotonicSharedCore.ApplyHighlightRollOff(outB);

                    pixels[idx] = (unchecked((int)0xFF000000)) |
                                  (Math.Clamp((int)(outR + 0.5f), 0, 255) << 16) |
                                  (Math.Clamp((int)(outG + 0.5f), 0, 255) << 8) |
                                  Math.Clamp((int)(outB + 0.5f), 0, 255);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(guideLuma);
            ArrayPool<float>.Shared.Return(fineIllum);
            ArrayPool<float>.Shared.Return(medIllum);
            ArrayPool<float>.Shared.Return(coarseIllum);
            ArrayPool<float>.Shared.Return(msrReflectance);
            ArrayPool<float>.Shared.Return(tempBuf);
        }
    }

    private static void FastGuidedFilterSelf(float[] I, float[] q, float[] temp, int w, int h, int radius, float eps)
    {
        int len = w * h;
        float[] meanI = ArrayPool<float>.Shared.Rent(len);
        float[] corrI = ArrayPool<float>.Shared.Rent(len);
        float[] a = ArrayPool<float>.Shared.Rent(len);
        float[] b = ArrayPool<float>.Shared.Rent(len);

        try
        {
            for (int i = 0; i < len; i++)
            {
                corrI[i] = I[i] * I[i];
            }

            PhotonicSharedCore.BoxBlur2D(I, meanI, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(corrI, a, temp, w, h, radius);

            for (int i = 0; i < len; i++)
            {
                float mI = meanI[i];
                float varI = Math.Max(0.0f, a[i] - mI * mI);
                float ai = varI / (varI + eps);
                a[i] = ai;
                b[i] = (1.0f - ai) * mI;
            }

            PhotonicSharedCore.BoxBlur2D(a, corrI, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(b, meanI, temp, w, h, radius);

            for (int i = 0; i < len; i++)
            {
                q[i] = corrI[i] * I[i] + meanI[i];
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(meanI);
            ArrayPool<float>.Shared.Return(corrI);
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
        }
    }

    private static void FastGuidedFilterGuide(float[] I, float[] p, float[] q, float[] temp, int w, int h, int radius, float eps)
    {
        int len = w * h;
        float[] meanI = ArrayPool<float>.Shared.Rent(len);
        float[] meanP = ArrayPool<float>.Shared.Rent(len);
        float[] corrI = ArrayPool<float>.Shared.Rent(len);
        float[] corrIP = ArrayPool<float>.Shared.Rent(len);

        try
        {
            for (int i = 0; i < len; i++)
            {
                corrI[i] = I[i] * I[i];
                corrIP[i] = I[i] * p[i];
            }

            PhotonicSharedCore.BoxBlur2D(I, meanI, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(p, meanP, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(corrI, corrI, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(corrIP, corrIP, temp, w, h, radius);

            for (int i = 0; i < len; i++)
            {
                float mI = meanI[i];
                float mP = meanP[i];
                float varI = Math.Max(0.0f, corrI[i] - mI * mI);
                float covIP = corrIP[i] - mI * mP;

                float ai = covIP / (varI + eps);
                float bi = mP - ai * mI;

                corrI[i] = ai;
                corrIP[i] = bi;
            }

            PhotonicSharedCore.BoxBlur2D(corrI, meanI, temp, w, h, radius);
            PhotonicSharedCore.BoxBlur2D(corrIP, meanP, temp, w, h, radius);

            for (int i = 0; i < len; i++)
            {
                q[i] = meanI[i] * I[i] + meanP[i];
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(meanI);
            ArrayPool<float>.Shared.Return(meanP);
            ArrayPool<float>.Shared.Return(corrI);
            ArrayPool<float>.Shared.Return(corrIP);
        }
    }
}
