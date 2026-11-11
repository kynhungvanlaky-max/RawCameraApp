using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Hướng B: Động cơ xử lý ảnh AI On-Device sử dụng ONNX Runtime &amp; Qualcomm Hexagon NPU / CPU.
/// Chạy mô hình mạng nơ-ron Photonic Neural Engine trực tiếp trên điện thoại Poco X3 Pro hoàn toàn offline.
/// Đồng bộ hoá với PhotonicSharedCore (Zero Heap Allocation cho Gain Grids via ArrayPool, Bilinear &amp; Highlight Roll-Off).
/// </summary>
public static class OnnxPhotonicEngine
{
    private static InferenceSession? _session;
    private static readonly object _initLock = new();
    private static bool _isInitialized = false;
    private static bool _isFailed = false;

    public static bool IsAvailable => _isInitialized && _session != null;

    /// <summary>
    /// Khởi tạo và nạp mô hình ONNX vào RAM / NPU
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (_isInitialized || _isFailed) return;

        await Task.Run(async () =>
        {
            lock (_initLock)
            {
                if (_isInitialized || _isFailed) return;
            }

            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync("photonic_neural_engine.onnx");
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                byte[] modelBytes = ms.ToArray();

                var sessionOptions = new SessionOptions();
                try
                {
                    // Tận dụng tăng tốc phần cứng NPU/DSP Qualcomm Snapdragon qua NNAPI
                    sessionOptions.AppendExecutionProvider_Nnapi();
                }
                catch
                {
                    // Dự phòng sang CPU đa luồng tối ưu
                    sessionOptions.IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount);
                }

                var session = new InferenceSession(modelBytes, sessionOptions);

                lock (_initLock)
                {
                    _session = session;
                    _isInitialized = true;
                }
                System.Diagnostics.Debug.WriteLine("[OnnxPhotonicEngine] ONNX Engine initialized successfully with NPU/CPU.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OnnxPhotonicEngine] Initialization failed: {ex.Message}");
                lock (_initLock)
                {
                    _isFailed = true;
                }
            }
        });
    }

    /// <summary>
    /// Thực hiện suy luận mạng nơ-ron AI (Neural Inference) trực tiếp trên ảnh chụp.
    /// Sử dụng kiến trúc Bilateral Neural Grid + PhotonicSharedCore để đạt tốc độ thời gian thực
    /// và giữ trọn vẹn 100% độ phân giải gốc của cảm biến mà không gây tràn RAM.
    /// </summary>
    public static bool Enhance(int[] pixels, int width, int height, float strength = 1.0f)
    {
        if (pixels == null || pixels.Length == 0 || width <= 0 || height <= 0 || strength <= 0.01f)
            return false;

        if (!_isInitialized && !_isFailed)
        {
            try
            {
                InitializeAsync().GetAwaiter().GetResult();
            }
            catch { }
        }

        if (_session == null) return false;

        strength = Math.Clamp(strength, 0.0f, 1.0f);

        const int inferW = 384;
        const int inferH = 384;
        const int gridLen = inferW * inferH;

        var inputTensor = new DenseTensor<float>(new[] { 1, 3, inferH, inferW });

        // 1. Trích xuất và chuẩn hoá dữ liệu đầu vào [0.0, 1.0] đưa vào tensor
        float stepX = (float)width / inferW;
        float stepY = (float)height / inferH;

        for (int y = 0; y < inferH; y++)
        {
            int srcY = Math.Min((int)(y * stepY), height - 1);
            int rowOffset = srcY * width;

            for (int x = 0; x < inferW; x++)
            {
                int srcX = Math.Min((int)(x * stepX), width - 1);
                int color = pixels[rowOffset + srcX];

                inputTensor[0, 0, y, x] = ((color >> 16) & 0xFF) / 255.0f;
                inputTensor[0, 1, y, x] = ((color >> 8) & 0xFF) / 255.0f;
                inputTensor[0, 2, y, x] = (color & 0xFF) / 255.0f;
            }
        }

        // 2. Chạy suy luận nơ-ron (Inference)
        DenseTensor<float>? outputTensor = null;
        try
        {
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var results = _session.Run(inputs);
            foreach (var result in results)
            {
                if (result.Name == "output")
                {
                    outputTensor = result.Value as DenseTensor<float>;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OnnxPhotonicEngine] Inference failed: {ex.Message}");
            return false;
        }

        if (outputTensor == null) return false;

        // 3. Mượn bộ đệm 1 chiều từ ArrayPool (không cấp phát mảng 2 chiều mới trên Heap)
        float[] gainR = ArrayPool<float>.Shared.Rent(gridLen);
        float[] gainG = ArrayPool<float>.Shared.Rent(gridLen);
        float[] gainB = ArrayPool<float>.Shared.Rent(gridLen);

        try
        {
            for (int y = 0; y < inferH; y++)
            {
                int row = y * inferW;
                for (int x = 0; x < inferW; x++)
                {
                    float inR = inputTensor[0, 0, y, x];
                    float inG = inputTensor[0, 1, y, x];
                    float inB = inputTensor[0, 2, y, x];

                    float outR = outputTensor[0, 0, y, x];
                    float outG = outputTensor[0, 1, y, x];
                    float outB = outputTensor[0, 2, y, x];

                    float gR = (outR + 0.02f) / (inR + 0.02f);
                    float gG = (outG + 0.02f) / (inG + 0.02f);
                    float gB = (outB + 0.02f) / (inB + 0.02f);

                    int idx = row + x;
                    gainR[idx] = Math.Clamp(gR, 0.45f, 2.35f);
                    gainG[idx] = Math.Clamp(gG, 0.45f, 2.35f);
                    gainB[idx] = Math.Clamp(gB, 0.45f, 2.35f);
                }
            }

            // 4. Áp dụng song song đa luồng hệ số nơ-ron qua PhotonicSharedCore.SampleBilinear & ApplyHighlightRollOff
            float invW = 1.0f / Math.Max(width - 1, 1);
            float invH = 1.0f / Math.Max(height - 1, 1);

            Parallel.For(0, height, y =>
            {
                float normY = y * invH;
                int rowOffset = y * width;

                for (int x = 0; x < width; x++)
                {
                    float normX = x * invW;

                    float rGain = PhotonicSharedCore.SampleBilinear(gainR, inferW, inferH, normX, normY);
                    float gGain = PhotonicSharedCore.SampleBilinear(gainG, inferW, inferH, normX, normY);
                    float bGain = PhotonicSharedCore.SampleBilinear(gainB, inferW, inferH, normX, normY);

                    rGain = 1.0f + (rGain - 1.0f) * strength;
                    gGain = 1.0f + (gGain - 1.0f) * strength;
                    bGain = 1.0f + (bGain - 1.0f) * strength;

                    int color = pixels[rowOffset + x];
                    int a = (color >> 24) & 0xFF;
                    int r = (color >> 16) & 0xFF;
                    int g = (color >> 8) & 0xFF;
                    int b = color & 0xFF;

                    float newR = PhotonicSharedCore.ApplyHighlightRollOff(r * rGain);
                    float newG = PhotonicSharedCore.ApplyHighlightRollOff(g * gGain);
                    float newB = PhotonicSharedCore.ApplyHighlightRollOff(b * bGain);

                    pixels[rowOffset + x] = (a << 24) |
                                            (Math.Clamp((int)(newR + 0.5f), 0, 255) << 16) |
                                            (Math.Clamp((int)(newG + 0.5f), 0, 255) << 8) |
                                            Math.Clamp((int)(newB + 0.5f), 0, 255);
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(gainR);
            ArrayPool<float>.Shared.Return(gainG);
            ArrayPool<float>.Shared.Return(gainB);
        }

        return true;
    }
}
