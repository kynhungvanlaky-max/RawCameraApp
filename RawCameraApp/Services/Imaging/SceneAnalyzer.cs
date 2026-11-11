namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Kết quả phân tích bối cảnh quang học cho chế độ PHOTO.
/// </summary>
public readonly record struct SceneAnalysisResult(
    float MeanLuminance,
    float DynamicRangeRatio,
    float WbGainR,
    float WbGainB,
    bool IsLowLight,
    bool IsHighDynamicRange);

/// <summary>
/// Bộ phân tích bối cảnh thông minh (Scene Analyzer) cho chế độ PHOTO.
/// Đồng bộ thuật toán tính Luma và Cân bằng trắng tự động (AWB) với PhotonicSharedCore.
/// </summary>
public static class SceneAnalyzer
{
    public static SceneAnalysisResult Analyze(int[] pixels, int width, int height, int iso)
    {
        const int step = 16;

        double sumLuma = 0;
        int darkCount = 0;
        int brightCount = 0;
        int sampleCount = 0;

        double sumR = 0, sumG = 0, sumB = 0;
        int graySamples = 0;

        for (int y = 0; y < height; y += step)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x += step)
            {
                int p = pixels[rowOffset + x];
                int r = (p >> 16) & 0xFF;
                int g = (p >> 8) & 0xFF;
                int b = p & 0xFF;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);
                sumLuma += luma;
                sampleCount++;

                if (luma < 35.0f) darkCount++;
                else if (luma > 220.0f) brightCount++;

                // Lọc các điểm trung tính cho thuật toán Cân bằng trắng dùng chung
                int maxC = Math.Max(r, Math.Max(g, b));
                int minC = Math.Min(r, Math.Min(g, b));
                int chroma = maxC - minC;

                if (luma > 35.0f && luma < 215.0f && chroma < 45)
                {
                    sumR += r / 255.0f;
                    sumG += g / 255.0f;
                    sumB += b / 255.0f;
                    graySamples++;
                }
            }
        }

        if (sampleCount == 0)
        {
            return new SceneAnalysisResult(128f, 0.2f, 1.0f, 1.0f, false, false);
        }

        float meanLuma = (float)(sumLuma / sampleCount);
        float darkRatio = (float)darkCount / sampleCount;
        float brightRatio = (float)brightCount / sampleCount;
        float drRatio = darkRatio + brightRatio;

        // Tính hệ số Cân bằng trắng tự động (Smart AWB) qua PhotonicSharedCore dùng chung
        var (wbGainR, _, wbGainB) = PhotonicSharedCore.ComputeAwbGains(
            (float)sumR, (float)sumG, (float)sumB, graySamples, damping: 0.45f);

        bool isLowLight = meanLuma < 65.0f || iso >= 800;
        bool isHdr = (darkRatio > 0.15f && brightRatio > 0.05f);

        return new SceneAnalysisResult(meanLuma, drRatio, wbGainR, wbGainB, isLowLight, isHdr);
    }
}
