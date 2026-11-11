#if ANDROID
using Android.Content;
using Android.OS;
using Android.Provider;
using RawCameraApp.Services;

namespace RawCameraApp.Platforms.Android;

public class AndroidPhotoStorageService : IPhotoStorageService
{
    public async Task<string> SaveToGalleryAsync(byte[] imageBytes, string fileName)
    {
        try
        {
            var context = global::Android.App.Application.Context;
            var values = new ContentValues();
            values.Put(MediaStore.Images.Media.InterfaceConsts.DisplayName, fileName);
            values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");
            
            // Lưu vào thư mục DCIM/RawCamera để ảnh xuất hiện ngay trong Gallery/Google Photos
            values.Put(MediaStore.Images.Media.InterfaceConsts.RelativePath, "DCIM/RawCamera");
            values.Put(MediaStore.Images.Media.InterfaceConsts.IsPending, 1);

            var resolver = context.ContentResolver;
            if (resolver == null || MediaStore.Images.Media.ExternalContentUri == null) return string.Empty;

            var uri = resolver.Insert(MediaStore.Images.Media.ExternalContentUri, values);
            if (uri == null) return string.Empty;

            using (var stream = resolver.OpenOutputStream(uri))
            {
                if (stream != null)
                {
                    await stream.WriteAsync(imageBytes, 0, imageBytes.Length);
                    await stream.FlushAsync();
                }
            }

            values.Clear();
            values.Put(MediaStore.Images.Media.InterfaceConsts.IsPending, 0);
            resolver.Update(uri, values, null, null);

            return uri.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotoStorage] Save error: {ex.Message}");
            return string.Empty;
        }
    }

    public async Task<PhotoFileInfo?> GetLatestPhotoAsync()
    {
        return await Task.Run(async () =>
        {
            try
            {
                var photos = await GetAllPhotosAsync();
                var latest = photos.FirstOrDefault();
                if (latest != null && File.Exists(latest.FilePath))
                {
                    var bytes = await File.ReadAllBytesAsync(latest.FilePath);
                    return new PhotoFileInfo(bytes, latest.FileName, latest.FileSizeString);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoStorage] GetLatest error: {ex.Message}");
            }

            return null;
        });
    }

    public async Task<List<PhotoItem>> GetAllPhotosAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<PhotoItem>();
            try
            {
                var dcimDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(global::Android.OS.Environment.DirectoryDcim)?.AbsolutePath;
                if (!string.IsNullOrEmpty(dcimDir))
                {
                    var rawFolder = Path.Combine(dcimDir, "RawCamera");
                    if (Directory.Exists(rawFolder))
                    {
                        var dir = new DirectoryInfo(rawFolder);
                        var files = dir.GetFiles("*.jpg")
                                       .OrderByDescending(f => f.LastWriteTimeUtc);

                        foreach (var file in files)
                        {
                            try
                            {
                                long len = file.Length;
                                if (len == 0) continue;

                                double sizeKb = Math.Round(len / 1024.0, 1);
                                string sizeStr = sizeKb > 1024 ? $"{Math.Round(sizeKb / 1024.0, 2)} MB" : $"{sizeKb} KB";

                                // Phân tích sơ bộ chế độ chụp
                                string tag = "PHOTO";
                                if (file.Name.Contains("48MP", StringComparison.OrdinalIgnoreCase))
                                    tag = "48 MP";
                                else if (file.Name.Contains("AI", StringComparison.OrdinalIgnoreCase))
                                    tag = "CAM AI";
                                else if (file.Name.Contains("RAW", StringComparison.OrdinalIgnoreCase))
                                    tag = "RAW GỐC";

                                results.Add(new PhotoItem(
                                    FilePath: file.FullName,
                                    FileName: file.Name,
                                    FileSizeString: sizeStr,
                                    FileSizeBytes: len,
                                    CreatedAt: file.LastWriteTime,
                                    ModeTag: tag,
                                    FormattedDate: file.LastWriteTime.ToString("dd/MM/yyyy HH:mm:ss")
                                ));
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoStorage] GetAllPhotos error: {ex.Message}");
            }

            return results;
        });
    }

    public async Task<bool> DeletePhotoAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }

                // Cập nhật MediaStore
                try
                {
                    var context = global::Android.App.Application.Context;
                    var resolver = context.ContentResolver;
                    if (resolver != null && MediaStore.Images.Media.ExternalContentUri != null)
                    {
                        string fileName = Path.GetFileName(filePath);
                        resolver.Delete(MediaStore.Images.Media.ExternalContentUri,
                            $"{MediaStore.Images.Media.InterfaceConsts.DisplayName} = ?",
                            new[] { fileName });
                    }
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoStorage] Delete error: {ex.Message}");
                return false;
            }
        });
    }

    public async Task<byte[]?> GetPhotoBytesAsync(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return await File.ReadAllBytesAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotoStorage] GetBytes error: {ex.Message}");
        }
        return null;
    }

    public async Task<bool> OverwritePhotoAsync(string filePath, byte[] imageBytes)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (File.Exists(filePath))
                {
                    await File.WriteAllBytesAsync(filePath, imageBytes);

                    try
                    {
                        var context = global::Android.App.Application.Context;
                        global::Android.Media.MediaScannerConnection.ScanFile(
                            context,
                            new[] { filePath },
                            new[] { "image/jpeg" },
                            null);
                    }
                    catch { }

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoStorage] Overwrite error: {ex.Message}");
            }
            return false;
        });
    }

    public async Task<string> DuplicatePhotoAsync(string sourceFilePath)
    {
        return await Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(sourceFilePath)) return string.Empty;
                var bytes = await File.ReadAllBytesAsync(sourceFilePath);
                string dir = Path.GetDirectoryName(sourceFilePath) ?? string.Empty;
                string ext = Path.GetExtension(sourceFilePath);
                string baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
                string newFileName = $"{baseName}_COPY_{DateTime.Now:HHmmss}{ext}";
                return await SaveToGalleryAsync(bytes, newFileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoStorage] Duplicate error: {ex.Message}");
                return string.Empty;
            }
        });
    }

    public async Task SharePhotoAsync(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                await Share.Default.RequestAsync(new ShareFileRequest
                {
                    Title = "Chia sẻ ảnh RAW PRO",
                    File = new ShareFile(filePath)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PhotoStorage] Share error: {ex.Message}");
        }
    }
}
#endif
