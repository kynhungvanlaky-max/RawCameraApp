namespace RawCameraApp.Services;

public record PhotoFileInfo(byte[] Bytes, string FileName, string FileSizeString);

public record PhotoItem(
    string FilePath,
    string FileName,
    string FileSizeString,
    long FileSizeBytes,
    DateTime CreatedAt,
    string ModeTag,
    string FormattedDate
);

public interface IPhotoStorageService
{
    Task<string> SaveToGalleryAsync(byte[] imageBytes, string fileName);
    Task<PhotoFileInfo?> GetLatestPhotoAsync();
    Task<List<PhotoItem>> GetAllPhotosAsync();
    Task<bool> DeletePhotoAsync(string filePath);
    Task<byte[]?> GetPhotoBytesAsync(string filePath);
    Task<bool> OverwritePhotoAsync(string filePath, byte[] imageBytes);
    Task<string> DuplicatePhotoAsync(string sourceFilePath);
    Task SharePhotoAsync(string filePath);
}
