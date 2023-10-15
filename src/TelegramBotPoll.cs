using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace CraigRec2Telegram
{
    public partial class TelegramBotPoll : IHostedService, IHealthCheck
    {
        private readonly ITelegramBotClient telegramBotClient;
        private readonly ICraigRecordProcessor craigRecordProcessor;
        private readonly IHostApplicationLifetime hostApplicationLifetime;
        private readonly ILogger<TelegramBotPoll> logger;
        private Task recieveTask = Task.CompletedTask;
        private bool currentlyProcessingUpdate;

        public TelegramBotPoll(ITelegramBotClient telegramBotClient,
            ICraigRecordProcessor craigRecordProcessor,
            IHostApplicationLifetime hostApplicationLifetime,
            ILogger<TelegramBotPoll> logger)
        {
            this.telegramBotClient = telegramBotClient;
            this.craigRecordProcessor = craigRecordProcessor;
            this.hostApplicationLifetime = hostApplicationLifetime;
            this.logger = logger;
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
                    try
                    {
                        currentlyProcessingUpdate = true;
                        await craigRecordProcessor.ProcessRecord(
                            client, chat.Id,
                            craigLinkMatch.Groups["prefix"].Success,
                            craigLinkMatch.Groups["id"].Value,
                            craigLinkMatch.Groups["key"].Value,
                            craigLinkMatch.Groups["name"].Value,
                            driveFolderId, cancel);
                        return;
                    }
                    finally
                    {
                        currentlyProcessingUpdate = false;
                    }
                }
            }

            await client.SendTextMessageAsync(chat.Id, "Присылай ссылку на запись в формате [Желаемое название записи] https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
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
        }
    }
}