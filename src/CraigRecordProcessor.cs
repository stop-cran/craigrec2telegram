using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace CraigRec2Telegram
{
    public partial class CraigRecordProcessor : ICraigRecordProcessor
    {
        private readonly ILogger<CraigRecordProcessor> logger;
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private readonly ICraigDownloader craigDownloader;
        private readonly IAudioFormatConverter audioFormatConverter;
        private readonly IGoogleDriveRepository googleDriveRepository;
        private readonly IAudioToTextConverter audioToTextConverter;

        public CraigRecordProcessor(
            IHostApplicationLifetime hostApplicationLifetime,
            ICraigDownloader craigDownloader,
            IAudioFormatConverter audioFormatConverter,
            IGoogleDriveRepository googleDriveRepository,
            IAudioToTextConverter audioToTextConverter,
            ILogger<CraigRecordProcessor> logger)
        {
            this.hostApplicationLifetime = hostApplicationLifetime;
            this.craigDownloader = craigDownloader;
            this.audioFormatConverter = audioFormatConverter;
            this.googleDriveRepository = googleDriveRepository;
            this.audioToTextConverter = audioToTextConverter;
            this.logger = logger;
        }

        private async Task SetStatusMessageText(ChatId chatId, ITelegramBotClient client, Message statusMessage, string statusText, CancellationToken cancellationToken)
        {
            if (statusMessage.Text != statusText)
                try
                {
                    logger.LogDebug($"Status: {Links.SnipUserName().Replace(statusText, "***").Replace("\n", "\t")}");
                    await client.EditMessageTextAsync(chatId, statusMessage.MessageId, statusText, cancellationToken: cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.Message.ToLowerInvariant().Contains("message is not modified"))
                { }
        }

        public async Task ProcessRecord(ITelegramBotClient client, long chatId, bool giarc, string recordId, string recordKey, string recordName, string driveFolderId, CancellationToken cancel)
        {
            using var scope = logger.BeginScope(new { recordId });
            string downloadFolder = $"/tmp/record-{recordId}-{Guid.NewGuid()}";

            if (recordName == null)
            {
                recordName = $"{recordId}-{DateTime.Now:yyyy:MM:ddTHH:mm:ss}";
            }
            else
            {
                recordName = Links.FileNameRegex().Replace(recordName, "_").Trim("_- .,;".ToCharArray());
            }

            Directory.CreateDirectory(downloadFolder);

            try
            {
                logger.LogInformation($"Processing record {recordId}...");

                var statusMessage = await client.SendTextMessageAsync(chatId, "Открываю браузер...", cancellationToken: cancel);
                var m4aFilePath = await craigDownloader.DownloadRecord(
                    $"https://{(giarc ? "giarc." : "")}craig.horse/rec/{recordId}?key={recordKey}",
                    downloadFolder,
                    text => SetStatusMessageText(chatId, client, statusMessage, text, cancel),
                    cancel);
                var mp3FilePath = await audioFormatConverter.ConvertToMp3Async(m4aFilePath, cancel);

                await SetStatusMessageText(chatId, client, statusMessage, "Загружаю на Google Drive...", cancel);

                var googleDriveAudioFileId = await googleDriveRepository.UploadFileAsync(
                    mp3FilePath,
                    "audio/mpeg",
                    driveFolderId,
                    recordName + ".mp3",
                    cancel);

                if (googleDriveAudioFileId == null)
                {
                    await client.SendTextMessageAsync(chatId, "Ошибка загрузки аудио!", cancellationToken: cancel);
                    return;
                }

                await SetStatusMessageText(chatId, client, statusMessage, $"Ссылка на запись: https://drive.google.com/file/d/{googleDriveAudioFileId}/view?usp=drive_link", cancel);

                var textConverterStatusMessage = await client.SendTextMessageAsync(chatId, "Распознаю текст...", cancellationToken: cancel);
                using var progress = audioToTextConverter.Progress
                    .ObserveOn(TaskPoolScheduler.Default)
                        .Throttle(TimeSpan.FromSeconds(5))
                        .SelectMany(async progress =>
                        {
                            await SetStatusMessageText(chatId, client, textConverterStatusMessage, progress, cancel);
                            return Unit.Default;
                        }).Subscribe();
                (var text, var subtitles) = await audioToTextConverter.ConvertAsync(mp3FilePath, cancel);

                var googleDriveTextFileId = await googleDriveRepository.UploadBytesAsync(
                    text,
                    "text/plain",
                    driveFolderId,
                    recordName + ".txt",
                    cancel);

                if (googleDriveTextFileId == null)
                {
                    await client.SendTextMessageAsync(chatId, "Ошибка загрузки текста!", cancellationToken: cancel);
                    return;
                }

                var googleDriveSubtitlesFileId = await googleDriveRepository.UploadBytesAsync(
                    subtitles,
                    "text/plain",
                    driveFolderId,
                    recordName + ".srt",
                    cancel);

                if (googleDriveSubtitlesFileId == null)
                {
                    await client.SendTextMessageAsync(chatId, "Ошибка загрузки субтитров!", cancellationToken: cancel);
                    return;
                }

                await SetStatusMessageText(chatId, client, textConverterStatusMessage,
                    $"Ссылка на текст: https://drive.google.com/file/d/{googleDriveTextFileId}/view?usp=drive_link\n" +
                    $"Ссылка на субтитры: https://drive.google.com/file/d/{googleDriveSubtitlesFileId}/view?usp=drive_link", cancel);
            }
            catch (ApplicationLogicException ex)
            {
                logger.LogError(ex, $"Error processing record {recordId}!");

                if (!string.IsNullOrEmpty(ex.Message))
                    await client.SendTextMessageAsync(chatId, ex.Message, cancellationToken: cancel);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error processing record {recordId}!");
                await client.SendTextMessageAsync(chatId, "Ошибка!", cancellationToken: cancel);
            }
            finally
            {
                Directory.Delete(downloadFolder, true);
            }
        }

        static partial class Links
        {
            [GeneratedRegex("^[0-9]+-(?<name>[^:]+)", RegexOptions.Multiline)]
            public static partial Regex SnipUserName();

            [GeneratedRegex("[^\\w\\d\\s\\.,;]")]
            public static partial Regex FileNameRegex();
        }
    }


    public class ApplicationLogicException : Exception
    {
        public ApplicationLogicException
        () : base("") { }
        public ApplicationLogicException
        (string message) : base(message) { }
    }
}