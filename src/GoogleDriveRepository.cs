using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Upload;
using System.Text;

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

        public async Task<string?> UploadFileAsync(string localFilePath, string contentType, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken)
        {
            using var contentStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);

            return await UploadAsync(contentStream, contentType, googleDriveFolderId, googleDriveFileName, cancellationToken);
        }

        public async Task<string?> UploadStringAsync(string content, string contentType, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken)
        {
            using var contentStream = new MemoryStream(Encoding.UTF8.GetBytes(content));

            return await UploadAsync(contentStream, contentType, googleDriveFolderId, googleDriveFileName, cancellationToken);
        }

        private async Task<string?> UploadAsync(Stream content, string contentType, string googleDriveFolderId, string googleDriveFileName, CancellationToken cancellationToken)
        {
            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = configurableHttpClientInitializer
            });

            var request = service.Files.Create(new Google.Apis.Drive.v3.Data.File
            {
                Name = googleDriveFileName,
                Parents = new List<string> { googleDriveFolderId }
            }, content, contentType);
            request.SupportsAllDrives = true;
            request.SupportsTeamDrives = true;
            request.Fields = "id";

            logger.LogInformation($"Uploading {contentType}...");

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
