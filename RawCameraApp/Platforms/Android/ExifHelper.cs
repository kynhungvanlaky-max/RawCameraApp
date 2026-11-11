#if ANDROID
using Android.Media;

namespace RawCameraApp.Platforms.Android;

public static class ExifHelper
{
    private static readonly string[] ImportantTags = new[]
    {
        ExifInterface.TagOrientation,
        ExifInterface.TagDatetime,
        ExifInterface.TagDatetimeOriginal,
        ExifInterface.TagDatetimeDigitized,
        ExifInterface.TagMake,
        ExifInterface.TagModel,
        ExifInterface.TagIsoSpeedRatings,
        ExifInterface.TagExposureTime,
        ExifInterface.TagFNumber,
        ExifInterface.TagFocalLength,
        ExifInterface.TagFocalLengthIn35mmFilm,
        ExifInterface.TagWhiteBalance,
        ExifInterface.TagFlash,
        ExifInterface.TagColorSpace,
        ExifInterface.TagSubsecTime,
        ExifInterface.TagSubsecTimeOriginal,
        ExifInterface.TagSubsecTimeDigitized
    };

    public static Dictionary<string, string> ExtractTags(byte[] jpegBytes)
    {
        var tags = new Dictionary<string, string>();
        if (jpegBytes == null || jpegBytes.Length == 0) return tags;

        try
        {
            using var stream = new MemoryStream(jpegBytes);
            var exif = new ExifInterface(stream);

            foreach (var tag in ImportantTags)
            {
                var val = exif.GetAttribute(tag);
                if (!string.IsNullOrEmpty(val))
                {
                    tags[tag] = val;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExifHelper] ExtractTags error: {ex.Message}");
        }

        return tags;
    }

    public static int GetOrientation(byte[] jpegBytes)
    {
        try
        {
            using var stream = new MemoryStream(jpegBytes);
            var exif = new ExifInterface(stream);
            return exif.GetAttributeInt(ExifInterface.TagOrientation, (int)Orientation.Normal);
        }
        catch
        {
            return (int)Orientation.Normal;
        }
    }

    public static int GetIso(byte[] jpegBytes)
    {
        try
        {
            using var stream = new MemoryStream(jpegBytes);
            var exif = new ExifInterface(stream);
            int iso = exif.GetAttributeInt(ExifInterface.TagIsoSpeedRatings, 100);
            return iso > 0 ? iso : 100;
        }
        catch
        {
            return 100;
        }
    }

    public static byte[] InjectExif(byte[] targetJpegBytes, Dictionary<string, string> tagsToInject)
    {
        if (targetJpegBytes == null || targetJpegBytes.Length == 0 || tagsToInject.Count == 0)
            return targetJpegBytes ?? Array.Empty<byte>();

        string tempPath = Path.Combine(FileSystem.CacheDirectory, $"exif_temp_{Guid.NewGuid():N}.jpg");
        try
        {
            File.WriteAllBytes(tempPath, targetJpegBytes);
            var targetExif = new ExifInterface(tempPath);

            foreach (var kvp in tagsToInject)
            {
                targetExif.SetAttribute(kvp.Key, kvp.Value);
            }

            targetExif.SaveAttributes();
            return File.ReadAllBytes(tempPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExifHelper] InjectExif error: {ex.Message}");
            return targetJpegBytes;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch { }
        }
    }
}
#endif
