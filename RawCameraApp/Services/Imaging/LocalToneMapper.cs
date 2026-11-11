using System.Buffers;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Động cơ Local Tone Mapping (Bản đồ ánh sáng cục bộ hai tần số - Bilateral Grid Approximation).
/// Tách bức ảnh thành Lớp ánh sáng nền (Base Layer) và Lớp chi tiết vi mô (Detail Layer),
/// giúp cứu vùng tối và nén vùng sáng cục bộ mà không làm phẳng chi tiết hay gây viền sáng (halo).
/// Đồng bộ hoá với PhotonicSharedCore (Zero Heap Allocation cho bộ đệm trung gian).
/// </summary>
public static class LocalToneMapper
{
    private const int DownsampleShift = 3; // Thu nhỏ 8x (2^3 = 8)

    /// <summary>
    /// Tạo bản đồ ánh sáng vùng (Illumination Map) đã làm mượt từ mảng pixel ARGB gốc.
    /// Kích thước lưới đầu ra là (width / 8) x (height / 8), giá trị Luma chuẩn hóa [0..255].
    /// </summary>
    public static float[] GenerateBaseLayer(int[] pixels, int width, int height)
    {
        int gw = Math.Max(1, width >> DownsampleShift);
        int gh = Math.Max(1, height >> DownsampleShift);
        int gridLen = gw * gh;

        float[] grid = new float[gridLen];

        // 1. Lấy mẫu trung bình theo ô lưới (Block Averaging Downsample)
        Parallel.For(0, gh, gy =>
        {
            int yStart = gy << DownsampleShift;
            int yEnd = Math.Min(yStart + (1 << DownsampleShift), height);
            int gridRowOffset = gy * gw;

            for (int gx = 0; gx < gw; gx++)
            {
                int xStart = gx << DownsampleShift;
                int xEnd = Math.Min(xStart + (1 << DownsampleShift), width);

                long sumLuma = 0;
                int count = 0;

                // Lấy mẫu bước nhảy 2 bên trong ô 8x8 để đạt tốc độ cực đại
                for (int y = yStart; y < yEnd; y += 2)
                {
                    int rowOffset = y * width;
                    for (int x = xStart; x < xEnd; x += 2)
                    {
                        sumLuma += PhotonicSharedCore.ComputeFastLumaInt(pixels[rowOffset + x]);
                        count++;
                    }
                }

                grid[gridRowOffset + gx] = count > 0 ? (float)sumLuma / count : 128.0f;
            }
        });

        // 2. Làm mờ Gaussian xấp xỉ bằng 2 lượt Box Blur dùng chung từ PhotonicSharedCore + ArrayPool
        float[] temp = ArrayPool<float>.Shared.Rent(gridLen);
        float[] temp2 = ArrayPool<float>.Shared.Rent(gridLen);
        try
        {
            PhotonicSharedCore.BoxBlur2D(grid, temp2, temp, gw, gh, radius: 3);
            PhotonicSharedCore.BoxBlur2D(temp2, grid, temp, gw, gh, radius: 2);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
            ArrayPool<float>.Shared.Return(temp2);
        }

        return grid;
    }

    /// <summary>
    /// Nội suy song tuyến tính (Bilinear Interpolation) uỷ quyền sang PhotonicSharedCore dùng chung.
    /// </summary>
    public static float SampleBilinear(float[] grid, int gw, int gh, float normX, float normY)
    {
        return PhotonicSharedCore.SampleBilinear(grid, gw, gh, normX, normY);
    }
}
