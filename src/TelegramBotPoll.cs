using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace CraigRec2Telegram
{

    public partial class TelegramBotPoll : IHostedService, IHealthCheck
    {
        private readonly ITelegramBotClient telegramBotClient;
        private readonly ILogger<TelegramBotPoll> logger;
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private readonly ICraigDownloader craigDownloader;
        private readonly IGoogleDriveRepository googleDriveRepository;
        private readonly IAudioToTextConverter audioToTextConverter;
        private Task recieveTask = Task.CompletedTask;
        private bool currentlyProcessingUpdate;

        public TelegramBotPoll(ITelegramBotClient telegramBotClient,
            IHostApplicationLifetime hostApplicationLifetime,
            ICraigDownloader craigDownloader,
            IGoogleDriveRepository googleDriveRepository,
            IAudioToTextConverter audioToTextConverter,
            ILogger<TelegramBotPoll> logger)
        {
            this.telegramBotClient = telegramBotClient;
            this.logger = logger;
            this.hostApplicationLifetime = hostApplicationLifetime;
            this.craigDownloader = craigDownloader;
            this.googleDriveRepository = googleDriveRepository;
            this.audioToTextConverter = audioToTextConverter;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            recieveTask = telegramBotClient.ReceiveAsync(OnUpdate,
                (_, exception, _) =>
                {
                    logger.LogError(exception, "polling error");
                    return Task.CompletedTask;
                },
                new ReceiverOptions
                {
                    AllowedUpdates = new[] { UpdateType.Message },
                    Limit = 1
                }, hostApplicationLifetime.ApplicationStopping);

            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await recieveTask;
        }


        async Task OnUpdate(ITelegramBotClient client, Update update, CancellationToken cancel)
        {
            if (update.Message?.Text == null)
            {
                return;
            }

            var chat = await client.GetChatAsync(update.Message.Chat.Id);

            if (Links.DriveLinkRegex().IsMatch(update.Message.Text))
            {
                await client.PinChatMessageAsync(chat.Id, update.Message.MessageId, cancellationToken: cancel);
                await client.SendTextMessageAsync(chat.Id, "Теперь присылай ссылку на запись в формате [Желаемое название записи] https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
                return;
            }

            string? driveFolderId = null;

            if (chat.PinnedMessage?.Text != null)
            {
                var driveLinkMatch = Links.DriveLinkRegex().Match(chat.PinnedMessage.Text);

                if (driveLinkMatch.Success)
                {
                    driveFolderId = driveLinkMatch.Groups["id"].Value;
                }
            }

            if (driveFolderId == null)
            {
                await client.SendTextMessageAsync(chat.Id, "Пришли ссылку на папку в Google Drive (должно быть https://drive.google.com/drive/folders/...).", cancellationToken: cancel);
                return;
            }

            if (update.Type == UpdateType.Message && update.Message?.Type == MessageType.Text && update.Message.Text != null)
            {
                var text = update.Message.Text;

                var craigLinkMatch = Links.CraigRecordRegex().Match(text);

                if (craigLinkMatch.Success)
                {
                    await ProcessRecord(client, chat.Id,
                        craigLinkMatch.Groups["prefix"].Success,
                        craigLinkMatch.Groups["id"].Value,
                        craigLinkMatch.Groups["key"].Value,
                        craigLinkMatch.Groups["name"].Value,
                        driveFolderId, cancel);
                    return;
                }
            }

            await client.SendTextMessageAsync(chat.Id, "Присылай ссылку на запись в формате [Желаемое название записи] https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
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

        private async Task ProcessRecord(ITelegramBotClient client, long chatId, bool giarc, string recordId, string recordKey, string recordName, string driveFolderId, CancellationToken cancel)
        {
            using var scope = logger.BeginScope(new { recordId });
            string downloadFolder = $"/tmp/record-{recordId}-{Guid.NewGuid()}";

            if (recordName == null)
            {
                recordName = $"{recordId}-{DateTime.Now:yyyy:MM:ddTHH:mm:ss}";
            }
            else
            {
                recordName = Links.FileNameRegex().Replace(recordName, "_").Trim('_', ' ');
            }

            Directory.CreateDirectory(downloadFolder);

            try
            {
                currentlyProcessingUpdate = true;
                logger.LogInformation($"Processing record {recordId}...");

                var statusMessage = await client.SendTextMessageAsync(chatId, "Открываю браузер...", cancellationToken: cancel);
                var downloadedFileName = await craigDownloader.DownloadRecord(
                    $"https://{(giarc ? "giarc." : "")}craig.horse/rec/{recordId}?key={recordKey}",
                    downloadFolder,
                    text => SetStatusMessageText(chatId, client, statusMessage, text, cancel),
                    cancel);
                var audioFilePath = Path.Combine(downloadFolder, downloadedFileName);

                await SetStatusMessageText(chatId, client, statusMessage, "Загружаю на Google Drive...", cancel);

                var googleDriveAudioFileId = await googleDriveRepository.UploadFileAsync(
                    audioFilePath,
                    "audio/mpeg",
                    driveFolderId,
                    recordName + Path.GetExtension(downloadedFileName),
                    cancel);

                if (googleDriveAudioFileId == null)
                {
                    await client.SendTextMessageAsync(chatId, "Ошибка загрузки аудио!", cancellationToken: cancel);
                    return;
                }

                await SetStatusMessageText(chatId, client, statusMessage, $"Ссылка на запись: https://drive.google.com/file/d/{googleDriveAudioFileId}/view?usp=drive_link", cancel);

                var textConverterStatusMessage = await client.SendTextMessageAsync(chatId, "Распознаю текст...", cancellationToken: cancel);

                (var text, var subtitles) = await audioToTextConverter.ConvertAsync(audioFilePath,
                    text => SetStatusMessageText(chatId, client, textConverterStatusMessage, text, cancel).Wait(), cancel);

                var googleDriveTextFileId = await googleDriveRepository.UploadStringAsync(
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

                var googleDriveSubtitlesFileId = await googleDriveRepository.UploadStringAsync(
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
            catch (RecordNotFoundException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error processing record {recordId}!");
                await client.SendTextMessageAsync(chatId, "Ошибка!", cancellationToken: cancel);
            }
            finally
            {
                Directory.Delete(downloadFolder, true);
                currentlyProcessingUpdate = false;
            }
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(currentlyProcessingUpdate
                ? HealthCheckResult.Unhealthy("Currently processing update")
                : HealthCheckResult.Healthy("Not processing update"));
        }

        static partial class Links
        {
            [GeneratedRegex("^https://drive\\.google\\.com/drive/folders/(?<id>[a-zA-Z0-9_-]+)(\\?.*)?$")]
            public static partial Regex DriveLinkRegex();

            [GeneratedRegex("^(?<name>.+)https://(?<prefix>giarc\\.)?craig\\.horse/rec/(?<id>[a-zA-Z0-9_]+)\\?key=(?<key>[a-zA-Z0-9_]+)")]
            public static partial Regex CraigRecordRegex();

            [GeneratedRegex("^[0-9]+-(?<name>[^:]+)", RegexOptions.Multiline)]
            public static partial Regex SnipUserName();

            [GeneratedRegex("[^\\w\\d\\s]")]
            public static partial Regex FileNameRegex();
        }
    }

    public class RecordNotFoundException : Exception { }
}