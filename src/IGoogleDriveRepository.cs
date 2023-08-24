namespace CraigRec2Telegram
{
    public interface IGoogleDriveRepository
    {
        Task<string?> UploadFileAsync(string localFilePath, string contentType, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken);
        Task<string?> UploadBytesAsync(byte[] content, string contentType, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken);
    }
}