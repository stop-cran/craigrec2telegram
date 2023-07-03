namespace CraigRec2Telegram
{
    public interface IGoogleDriveRepository
    {
        Task<string?> UploadAsync(string localFilePath, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken);
    }
}