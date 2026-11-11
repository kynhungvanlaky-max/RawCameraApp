using System.Buffers;
using System.Diagnostics;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Bộ phân tích thị giác máy tính thời gian thực (Real-time AI Scene &amp; Semantic Analyzer).
/// Đồng bộ hoá hoàn toàn với PhotonicSharedCore (Skin Mask, AWB, Rec.709 Luma, ArrayPool).
/// </summary>
public static class AiSceneAnalyzer
{
#if ANDROID
    public static AiTelemetrySnapshot AnalyzeFrame(Android.Graphics.Bitmap previewBitmap)
    {
        int width = previewBitmap.Width;
        int height = previewBitmap.Height;
        int total = width * height;
        if (total == 0) return GetDefaultSnapshot();

        int[] pixels = ArrayPool<int>.Shared.Rent(total);
        try
        {
            previewBitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

            int faceCount = 0;
            float faceNormX = 0.5f;
            float faceNormY = 0.45f;

            try
            {
                using var rgb565 = previewBitmap.Copy(Android.Graphics.Bitmap.Config.Rgb565!, false);
                if (rgb565 != null)
                {
                    using var detector = new Android.Media.FaceDetector(width, height, 3);
                    var faces = new Android.Media.FaceDetector.Face[3];
                    faceCount = detector.FindFaces(rgb565, faces);
                    if (faceCount > 0 && faces[0] != null)
                    {
                        var midPoint = new Android.Graphics.PointF();
                        faces[0].GetMidPoint(midPoint);
                        faceNormX = Math.Clamp(midPoint.X / width, 0.1f, 0.9f);
                        faceNormY = Math.Clamp(midPoint.Y / height, 0.1f, 0.9f);
                    }
                }
            }
            catch
            {
                // Bỏ qua nếu FaceDetector không khả dụng trên thiết bị
            }

            return AnalyzePixels(pixels, width, height, faceCount, faceNormX, faceNormY);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pixels);
        }
    }
#endif

    public static AiTelemetrySnapshot AnalyzePixels(
        int[] pixels,
        int width,
        int height,
        int faceCount = 0,
        float faceNormX = 0.5f,
        float faceNormY = 0.45f)
    {
        var sw = Stopwatch.StartNew();
        int total = width * height;
        if (total == 0) return GetDefaultSnapshot();

        // Tự động chọn bước nhảy (stride):
        // - Nếu là ảnh preview 160x120: step = 1
        // - Nếu là ảnh chụp độ phân giải đầy đủ 12MP (4000x3000): step = 16 để chạy dưới 2ms!
        int step = (width > 320) ? 16 : 1;

        double sumLuma = 0;
        int shadowCount = 0;
        int highlightCount = 0;
        int skinCount = 0;
        int skyCount = 0;
        int foliageCount = 0;
        int warmFoodCount = 0;
        int sampledCount = 0;

        double awbR = 0, awbG = 0, awbB = 0;
        int awbCount = 0;

        int upperSkyLimitY = (int)(height * 0.48f);

        for (int y = 0; y < height; y += step)
        {
            int rowOffset = y * width;
            bool isUpperRegion = y < upperSkyLimitY;

            for (int x = 0; x < width; x += step)
            {
                int c = pixels[rowOffset + x];
                int rInt = (c >> 16) & 0xFF;
                int gInt = (c >> 8) & 0xFF;
                int bInt = c & 0xFF;

                float r = rInt / 255.0f;
                float g = gInt / 255.0f;
                float b = bInt / 255.0f;

                float luma = PhotonicSharedCore.ComputeLuma(r, g, b);
                sumLuma += luma;
                sampledCount++;

                if (luma < 0.16f) shadowCount++;
                else if (luma > 0.84f) highlightCount++;

                // 1. Nhận diện màu da dùng chung từ PhotonicSharedCore
                if (luma > 0.15f && luma < 0.90f && PhotonicSharedCore.ComputeSkinWeight(rInt, gInt, bInt) > 0.25f)
                {
                    skinCount++;
                }

                // 2. Nhận diện bầu trời (Upper region, Blue dominant, bright)
                if (isUpperRegion && b > r * 1.18f && b > g * 1.04f && luma > 0.30f && luma < 0.96f)
                {
                    skyCount++;
                }

                // 3. Nhận diện cây cối / thảm thực vật (Green dominant)
                if (g > r * 1.12f && g > b * 1.15f && luma > 0.10f && luma < 0.78f)
                {
                    foliageCount++;
                }

                // 4. Nhận diện màu ấm / ẩm thực
                if (r > b * 1.45f && g > b * 1.15f && r > g * 1.12f && luma > 0.22f && luma < 0.85f)
                {
                    warmFoodCount++;
                }

                // 5. Cân bằng trắng thông minh
                float maxC = Math.Max(r, Math.Max(g, b));
                float minC = Math.Min(r, Math.Min(g, b));
                float sat = maxC > 0.001f ? (maxC - minC) / maxC : 0.0f;
                if (luma > 0.15f && luma < 0.85f && sat < 0.40f)
                {
                    awbR += r;
                    awbG += g;
                    awbB += b;
                    awbCount++;
                }
            }
        }

        if (sampledCount == 0) return GetDefaultSnapshot();

        float meanLuma = (float)(sumLuma / sampledCount);
        float shadowRatio = (float)shadowCount / sampledCount;
        float highlightRatio = (float)highlightCount / sampledCount;
        float skinCoverage = (float)skinCount / sampledCount;
        float skyCoverage = (float)skyCount / sampledCount;
        float foliageCoverage = (float)foliageCount / sampledCount;
        float foodCoverage = (float)warmFoodCount / sampledCount;

        // Phân loại bối cảnh ưu tiên
        AiDetectedScene scene = AiDetectedScene.Neutral;
        string sceneName = "AI Tự động";
        string sceneIcon = "IconAiSparkleData";
        float confidence = 0.82f;

        if (meanLuma < 0.22f || (meanLuma < 0.30f && shadowRatio > 0.45f))
        {
            scene = AiDetectedScene.NightLowLight;
            sceneName = "AI Đêm & Thiếu sáng";
            sceneIcon = "IconNightData";
            confidence = 0.94f;
        }
        else if (faceCount > 0 || skinCoverage > 0.14f)
        {
            scene = AiDetectedScene.Portrait;
            sceneName = faceCount > 0 ? $"AI Chân dung • {faceCount} mặt" : "AI Chân dung";
            sceneIcon = "IconPortraitData";
            confidence = faceCount > 0 ? 0.98f : 0.88f;
        }
        else if (shadowRatio > 0.28f && highlightRatio > 0.12f)
        {
            scene = AiDetectedScene.BacklitHdr;
            sceneName = "AI Ngược sáng HDR";
            sceneIcon = "IconHdrSunData";
            confidence = 0.90f;
        }
        else if (skyCoverage > 0.16f)
        {
            scene = AiDetectedScene.LandscapeSky;
            sceneName = "AI Phong cảnh & Bầu trời";
            sceneIcon = "IconLandscapeData";
            confidence = 0.91f;
        }
        else if (foliageCoverage > 0.18f)
        {
            scene = AiDetectedScene.FoliageNature;
            sceneName = "AI Thiên nhiên & Cây lá";
            sceneIcon = "IconFoliageData";
            confidence = 0.89f;
        }
        else if (foodCoverage > 0.22f)
        {
            scene = AiDetectedScene.FoodWarm;
            sceneName = "AI Ẩm thực & Sắc ấm";
            sceneIcon = "IconWarmFoodData";
            confidence = 0.86f;
        }

        float evShift = 0.0f;
        if (scene == AiDetectedScene.BacklitHdr && highlightRatio > 0.16f)
        {
            evShift = -0.5f;
        }
        else if (highlightRatio > 0.24f)
        {
            evShift = -0.7f;
        }
        else if (scene == AiDetectedScene.NightLowLight && highlightRatio < 0.04f)
        {
            evShift = +0.5f;
        }
        else if (scene == AiDetectedScene.Portrait && meanLuma < 0.38f && highlightRatio < 0.08f)
        {
            evShift = +0.3f;
        }

        // Hệ số AWB dùng chung từ PhotonicSharedCore
        var (gainR, _, gainB) = PhotonicSharedCore.ComputeAwbGains(
            (float)awbR, (float)awbG, (float)awbB, awbCount, damping: 0.55f);

        sw.Stop();
        return new AiTelemetrySnapshot(
            scene,
            confidence,
            sceneName,
            sceneIcon,
            meanLuma,
            shadowRatio,
            highlightRatio,
            skinCoverage,
            skyCoverage,
            foliageCoverage,
            evShift,
            gainR,
            gainB,
            faceCount,
            sw.ElapsedMilliseconds,
            faceNormX,
            faceNormY);
    }

    public static AiTelemetrySnapshot GetDefaultSnapshot() => new(
        AiDetectedScene.Neutral,
        0.80f,
        "AI Tự động",
        "IconAiSparkleData",
        0.48f,
        0.10f,
        0.05f,
        0.0f,
        0.0f,
        0.0f,
        0.0f,
        1.0f,
        1.0f,
        0,
        1);
}
