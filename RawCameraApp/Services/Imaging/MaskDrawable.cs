using System;
using System.Collections.Generic;
using Microsoft.Maui.Graphics;

namespace RawCameraApp.Services.Imaging;

/// <summary>
/// Nét vẽ mặt nạ cảm ứng
/// </summary>
public class MaskStroke
{
    public List<PointF> Points { get; } = new();
    public float BrushRadius { get; set; } = 24f;

    public MaskStroke(float brushRadius)
    {
        BrushRadius = brushRadius;
    }
}

/// <summary>
/// Quản lý lớp vẽ mặt nạ cảm ứng (Interactive Mask Canvas) phủ lên ảnh xem trước trong AI Studio.
/// Hỗ trợ vẽ mượt mà, hoàn tác, đổi cỡ bút và xuất ma trận nhị phân (Binary Mask) khớp chuẩn xác kích thước ảnh gốc.
/// </summary>
public class MaskDrawable : IDrawable
{
    private readonly List<MaskStroke> _strokes = new();
    private MaskStroke? _currentStroke;
    private readonly object _lock = new();

    public float CurrentBrushRadius { get; set; } = 28f;
    public bool HasStrokes
    {
        get
        {
            lock (_lock)
            {
                return _strokes.Count > 0 || _currentStroke != null;
            }
        }
    }

    public void StartStroke(float x, float y)
    {
        lock (_lock)
        {
            _currentStroke = new MaskStroke(CurrentBrushRadius);
            _currentStroke.Points.Add(new PointF(x, y));
        }
    }

    public void AddPoint(float x, float y)
    {
        lock (_lock)
        {
            if (_currentStroke == null)
            {
                StartStroke(x, y);
                return;
            }

            // Lọc bớt các điểm quá sát nhau (< 2px) để tăng tốc độ vẽ
            if (_currentStroke.Points.Count > 0)
            {
                var last = _currentStroke.Points[^1];
                float dx = x - last.X;
                float dy = y - last.Y;
                if ((dx * dx + dy * dy) < 4f)
                {
                    return;
                }
            }

            _currentStroke.Points.Add(new PointF(x, y));
        }
    }

    public void EndStroke()
    {
        lock (_lock)
        {
            if (_currentStroke != null && _currentStroke.Points.Count > 0)
            {
                _strokes.Add(_currentStroke);
            }
            _currentStroke = null;
        }
    }

    public bool Undo()
    {
        lock (_lock)
        {
            if (_strokes.Count > 0)
            {
                _strokes.RemoveAt(_strokes.Count - 1);
                return true;
            }
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _strokes.Clear();
            _currentStroke = null;
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        lock (_lock)
        {
            if (_strokes.Count == 0 && _currentStroke == null) return;

            canvas.SaveState();
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeLineJoin = LineJoin.Round;

            // Màu đỏ huỳnh quang neon bán trong suốt
            canvas.StrokeColor = Color.FromArgb("#88EF4444");

            // Vẽ các nét đã hoàn thành
            foreach (var stroke in _strokes)
            {
                DrawStroke(canvas, stroke);
            }

            // Vẽ nét đang kéo dở
            if (_currentStroke != null)
            {
                DrawStroke(canvas, _currentStroke);
            }

            canvas.RestoreState();
        }
    }

    private static void DrawStroke(ICanvas canvas, MaskStroke stroke)
    {
        if (stroke.Points.Count == 0) return;

        canvas.StrokeSize = stroke.BrushRadius * 2f;

        if (stroke.Points.Count == 1)
        {
            var p = stroke.Points[0];
            canvas.FillColor = Color.FromArgb("#88EF4444");
            canvas.FillCircle(p.X, p.Y, stroke.BrushRadius);
            return;
        }

        var path = new PathF();
        path.MoveTo(stroke.Points[0]);

        for (int i = 1; i < stroke.Points.Count; i++)
        {
            path.LineTo(stroke.Points[i]);
        }

        canvas.DrawPath(path);
    }

    /// <summary>
    /// Xuất ma trận nhị phân (Binary Mask) tỷ lệ 1:1 với kích thước ảnh thực tế (255 = vùng tô, 0 = nền).
    /// </summary>
    public byte[] GenerateBinaryMask(int targetWidth, int targetHeight, RectF displayImageRect)
    {
        byte[] mask = new byte[targetWidth * targetHeight];

        lock (_lock)
        {
            if (_strokes.Count == 0) return mask;

            float dispW = displayImageRect.Width;
            float dispH = displayImageRect.Height;
            if (dispW <= 1f || dispH <= 1f) return mask;

            float scaleX = targetWidth / dispW;
            float scaleY = targetHeight / dispH;

            foreach (var stroke in _strokes)
            {
                if (stroke.Points.Count == 0) continue;

                float scaledRadius = stroke.BrushRadius * Math.Max(scaleX, scaleY);
                int rInt = (int)Math.Ceiling(scaledRadius);
                int rSq = rInt * rInt;

                for (int i = 0; i < stroke.Points.Count; i++)
                {
                    float imgX = (stroke.Points[i].X - displayImageRect.X) * scaleX;
                    float imgY = (stroke.Points[i].Y - displayImageRect.Y) * scaleY;

                    int cX = (int)Math.Round(imgX);
                    int cY = (int)Math.Round(imgY);

                    // Điền hình tròn quanh từng điểm
                    int minY = Math.Max(0, cY - rInt);
                    int maxY = Math.Min(targetHeight - 1, cY + rInt);
                    int minX = Math.Max(0, cX - rInt);
                    int maxX = Math.Min(targetWidth - 1, cX + rInt);

                    for (int y = minY; y <= maxY; y++)
                    {
                        int dy = y - cY;
                        int dySq = dy * dy;
                        int row = y * targetWidth;

                        for (int x = minX; x <= maxX; x++)
                        {
                            int dx = x - cX;
                            if (dx * dx + dySq <= rSq)
                            {
                                mask[row + x] = 255;
                            }
                        }
                    }

                    // Nối đường thẳng giữa 2 điểm liên tiếp nếu cách xa nhau
                    if (i > 0)
                    {
                        float prevX = (stroke.Points[i - 1].X - displayImageRect.X) * scaleX;
                        float prevY = (stroke.Points[i - 1].Y - displayImageRect.Y) * scaleY;

                        float dist = (float)Math.Sqrt((imgX - prevX) * (imgX - prevX) + (imgY - prevY) * (imgY - prevY));
                        int steps = (int)Math.Ceiling(dist / (scaledRadius * 0.4f));

                        for (int s = 1; s < steps; s++)
                        {
                            float t = (float)s / steps;
                            int interpX = (int)Math.Round(prevX + t * (imgX - prevX));
                            int interpY = (int)Math.Round(prevY + t * (imgY - prevY));

                            int sMinY = Math.Max(0, interpY - rInt);
                            int sMaxY = Math.Min(targetHeight - 1, interpY + rInt);
                            int sMinX = Math.Max(0, interpX - rInt);
                            int sMaxX = Math.Min(targetWidth - 1, interpX + rInt);

                            for (int y = sMinY; y <= sMaxY; y++)
                            {
                                int dy = y - interpY;
                                int dySq = dy * dy;
                                int row = y * targetWidth;

                                for (int x = sMinX; x <= sMaxX; x++)
                                {
                                    int dx = x - interpX;
                                    if (dx * dx + dySq <= rSq)
                                    {
                                        mask[row + x] = 255;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        return mask;
    }
}
