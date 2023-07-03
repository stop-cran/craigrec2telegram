using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;

namespace CraigRec2Telegram
{
    public class GoogleDriveRepository : IGoogleDriveRepository
    {
        private readonly IConfigurableHttpClientInitializer configurableHttpClientInitializer;
        private readonly ILogger<GoogleDriveRepository> logger;

        public GoogleDriveRepository(IConfigurableHttpClientInitializer configurableHttpClientInitializer, ILogger<GoogleDriveRepository> logger)
        {
            this.configurableHttpClientInitializer = configurableHttpClientInitializer;
            this.logger = logger;
        }

        public async Task<string?> UploadAsync(string localFilePath, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken)
        {
            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = configurableHttpClientInitializer
            });

            using var audioStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);

            var request = service.Files.Create(new Google.Apis.Drive.v3.Data.File
            {
                Name = googleDriveFileName,
                Parents = new List<string> { googleDriveFolderId }
            }, audioStream, "audio/mpeg");
            request.SupportsAllDrives = true;
            request.SupportsTeamDrives = true;
            request.Fields = "id";
            var uploadProgress = await request.UploadAsync(cancellationToken);

            switch (uploadProgress.Status)
            {
                case UploadStatus.Failed:
                    logger.LogError(uploadProgress.Exception, "Upload failed!");
                    return null;
                case UploadStatus.Completed:
                    logger.LogInformation("Upload succeeded!");
                    break;
                default:
                    logger.LogError($"Upload failed! Unexpected status: {uploadProgress.Status}");
                    return null;
            }

            return request.ResponseBody.Id;
        }
    }
}
