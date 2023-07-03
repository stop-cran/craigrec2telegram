using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Support.UI;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

static partial class Program
{
    static async Task Main()
    {
        var cancel = new CancellationTokenSource();

        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancel?.Cancel();

        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://+:5000");
        builder.Services.AddHealthChecks()
            .AddCheck("CurrentlyProcessing", () => CurrentlyProcessingUpdate
                ? HealthCheckResult.Unhealthy("Currently processing update")
                : HealthCheckResult.Healthy("Not processing update"));

        var app = builder.Build();

        app.MapHealthChecks("/healthz/ready");

        var run = app.RunAsync(cancel.Token);

        var bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_API_TOKEN")!);

        await bot.ReceiveAsync(OnUpdate, OnPollingError, new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message },
            Limit = 1
        }, cancel.Token);

        await run;
    }

    private static bool CurrentlyProcessingUpdate { get; set; }

    static async Task OnUpdate(ITelegramBotClient client, Update update, CancellationToken cancel)
    {
        if (update.Message?.Text == null)
        {
            return;
        }

        var chat = await client.GetChatAsync(update.Message.Chat.Id);
        string? driveFolderId = null;

        if (chat.PinnedMessage?.Text == null)
        {
            if (DriveLinkRegex().IsMatch(update.Message.Text))
            {
                await client.PinChatMessageAsync(chat.Id, update.Message.MessageId, cancellationToken: cancel);
                await client.SendTextMessageAsync(chat.Id, "Теперь присылай ссылку на запись в формате https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
                return;
            }
        }
        else
        {
            var m = DriveLinkRegex().Match(chat.PinnedMessage.Text);

            if (m.Success)
            {
                driveFolderId = m.Groups["id"].Value;
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

            var m = CraigRecordRegex().Match(text);

            if (m.Success)
            {
                await ProcessRecord(client, chat.Id, m.Groups["prefix"].Success, m.Groups["id"].Value, m.Groups["key"].Value, driveFolderId, cancel);
                return;
            }
        }

        await client.SendTextMessageAsync(chat.Id, "Присылай ссылку на запись в формате https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
    }

    static Task OnPollingError(ITelegramBotClient client, Exception exception, CancellationToken cancel)
    {
        Console.WriteLine(exception);

        return Task.CompletedTask;
    }

    private static async Task ProcessRecord(ITelegramBotClient client, long chatId, bool giarc, string recordId, string recordKey, string driveFolderId, CancellationToken cancel)
    {
        string downloadFolder = $"/tmp/record-{recordId}-{Guid.NewGuid()}";

        Directory.CreateDirectory(downloadFolder);

        try
        {
            CurrentlyProcessingUpdate = true;
            Console.WriteLine($"Processing record {recordId}...");

            var statusMessage = await client.SendTextMessageAsync(chatId, "Открываю браузер...", cancellationToken: cancel);

            async Task SetStatusText(string statusText)
            {
                if (statusMessage.Text != statusText)
                    try
                    {
                        await client.EditMessageTextAsync(chatId, statusMessage.MessageId, statusText, cancellationToken: cancel);

                        Console.WriteLine($"Record id: {recordId}, status:{SnipUserName().Replace(statusText, "***").Replace("\n", "\t")}");
                    }
                    catch (ApiRequestException ex) when (ex.Message.ToLowerInvariant().Contains("message is not modified"))
                    { }
            }

            var options = new EdgeOptions();
            options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 1);
            options.AddAdditionalOption("download.default_directory", downloadFolder);
            options.AddArgument("headless");
            options.AddArgument("disable-gpu");
            using var driver = new EdgeDriver(options);
            using var session = driver.GetDevToolsSession();
            var devToolsSession = session.GetVersionSpecificDomains<OpenQA.Selenium.DevTools.V114.DevToolsSessionDomains>();

            var nw = await devToolsSession.Network.Enable(new OpenQA.Selenium.DevTools.V114.Network.EnableCommandSettings(), cancel);
            var pg = await devToolsSession.Page.Enable(new OpenQA.Selenium.DevTools.V114.Page.EnableCommandSettings(), cancel);

            var url = $"https://{(giarc ? "giarc." : "")}craig.horse/rec/{recordId}?key={recordKey}";
            var resp = Observable.FromEventPattern<OpenQA.Selenium.DevTools.V114.Network.ResponseReceivedEventArgs>(
                d => devToolsSession.Network.ResponseReceived += d,
                d => devToolsSession.Network.ResponseReceived -= d)
                .FirstOrDefaultAsync(r => r.EventArgs.Response.Url == url).RunAsync(cancel);

            await devToolsSession.Page.SetDownloadBehavior(new OpenQA.Selenium.DevTools.V114.Page.SetDownloadBehaviorCommandSettings
            {
                Behavior = "allow",
                DownloadPath = downloadFolder
            }, cancel);

            await SetStatusText("Перехожу по ссылке...");
            driver.Navigate().GoToUrl(url);

            await resp;

            var findElementCancelTimeout = new CancellationTokenSource(30_000);
            var findElementCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel, findElementCancelTimeout.Token);

            await SetStatusText("Жду обработки (1)...");
            var notFoundOrMpeg4Button = await driver.FindElementAsync(By.XPath("//*[(name() = 'button' and .//span[text()='AAC'] and .//span[.//span[text()='(MPEG-4)']]and .//parent::div[.//preceding-sibling::div[.//h4[text() = 'Single-track mixed']]]) or (name() = 'span' and text() = 'Recording not found.')]"), findElementCancel.Token);

            if (notFoundOrMpeg4Button.TagName == "button")
            {
                notFoundOrMpeg4Button.Click();
            }
            else
            {
                await SetStatusText("Запись не найдена!");
                return;
            }

            await SetStatusText("Жду обработки (2)...");
            (await driver.FindElementAsync(By.XPath("//button[text()='I understand.']"), findElementCancel.Token)).Click();

            var downloadedFileNames = Observable.FromEventPattern<OpenQA.Selenium.DevTools.V114.Page.DownloadWillBeginEventArgs>(
                d => devToolsSession.Page.DownloadWillBegin += d,
                d => devToolsSession.Page.DownloadWillBegin -= d)
                .Where(r => r.EventArgs.SuggestedFilename.EndsWith(".m4a"))
                .SelectMany(begin =>
                    Observable.FromEventPattern<OpenQA.Selenium.DevTools.V114.Page.DownloadProgressEventArgs>(
                        d => devToolsSession.Page.DownloadProgress += d,
                        d => devToolsSession.Page.DownloadProgress -= d)
                    .Where(r => r.EventArgs.Guid == begin.EventArgs.Guid && r.EventArgs.State == "completed")
                    .Select(_ => begin.EventArgs.SuggestedFilename))
                .FirstAsync()
                .RunAsync(cancel);

            for (int i = 0; i < 2; i++)
            {
                await SetStatusText($"Жду обработки ({3 + i * 2})...");
                (await driver.FindElementAsync(By.XPath("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[contains(text(), 'To handle large projects')]]]"), findElementCancel.Token)).Click();
                await SetStatusText($"Жду обработки ({4 + i * 2})...");
                (await driver.FindElementAsync(By.XPath("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[text() = 'Failed to acquire permission for persistent storage. Large projects will fail.']]]"), findElementCancel.Token)).Click();
            }

            for (bool finished = false; !finished;)
            {
                var elements = driver.FindElements(By.XPath("//div[not(contains(@style,'display:none'))]//*[(name() = 'div' and contains(@class, 'dialog') and (contains(text(),'Loading') or contains(text(),'Mix') or contains(text(),'Export'))) or (name() = 'button' and contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[text() = 'Your audio has now been exported. You may close this tab, or click OK to continue using this tool.']])]"));

                foreach (var buttonOrStatus in elements)
                {
                    if (buttonOrStatus.TagName == "button")
                    {
                        buttonOrStatus.Click();
                        finished = true;
                    }
                    else
                        await SetStatusText(buttonOrStatus.Text);
                }

                if (!finished)
                    await Task.Delay(5000, cancel);
            }

            var downloadedFileName = await downloadedFileNames;

            await SetStatusText("Загружаю на Google Drive...");

            var credential = new ServiceAccountCredential(
                 new ServiceAccountCredential.Initializer(Environment.GetEnvironmentVariable("GOOGLE_DRIVE_EMAIL"))
                 {
                     Scopes = new[] { DriveService.Scope.Drive }
                 }.FromPrivateKey(Environment.GetEnvironmentVariable("GOOGLE_DRIVE_PRIVATE_KEY")));

            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential
            });

            string googleDriveFileId;

            using (var audioStream = new FileStream(Path.Combine(downloadFolder, downloadedFileName), FileMode.Open, FileAccess.Read))
            {
                var request = service.Files.Create(new Google.Apis.Drive.v3.Data.File
                {
                    Name = $"{recordId}-{DateTime.Now:yyyy:MM:ddTHH:mm:ss}{Path.GetExtension(downloadedFileName)}",
                    Parents = new List<string> { driveFolderId }
                }, audioStream, "audio/mpeg");
                request.SupportsAllDrives = true;
                request.SupportsTeamDrives = true;
                request.Fields = "id";
                var uploadProgress = await request.UploadAsync(cancel);

                switch (uploadProgress.Status)
                {
                    case UploadStatus.Failed:
                        Console.WriteLine("Upload failed!");
                        Console.WriteLine(uploadProgress.Exception);
                        await client.SendTextMessageAsync(chatId, "Ошибка загрузки!", cancellationToken: cancel);
                        return;
                    case UploadStatus.Completed:
                        Console.WriteLine("Upload succeeded!");
                        break;
                    default:
                        Console.WriteLine("Upload failed! Unexpected status: " + uploadProgress.Status);
                        await client.SendTextMessageAsync(chatId, "Ошибка загрузки!", cancellationToken: cancel);
                        return;
                }

                googleDriveFileId = request.ResponseBody.Id;
            }

            await client.DeleteMessageAsync(chatId, statusMessage.MessageId, cancellationToken: cancel);
            await client.SendChatActionAsync(chatId, ChatAction.UploadDocument, cancellationToken: cancel);
            await client.SendTextMessageAsync(chatId, $"Ссылка на запись: https://drive.google.com/file/d/{googleDriveFileId}/view?usp=drive_link", cancellationToken: cancel);

            Console.WriteLine($"Successfully processed record {recordId}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing record {recordId}...");
            Console.WriteLine(ex);
            await client.SendTextMessageAsync(chatId, "Ошибка!", cancellationToken: cancel);
        }
        finally
        {
            Directory.Delete(downloadFolder, true);
            CurrentlyProcessingUpdate = false;
        }
    }

    private static async Task<IWebElement> FindElementAsync(this IWebDriver driver, By by, CancellationToken cancellationToken)
    {
        for (; ; )
        {
            var elements = driver.FindElements(by);

            if (elements.Count > 0)
                return elements[0];

            await Task.Delay(1000, cancellationToken);
        }
    }

    [GeneratedRegex("^https://drive\\.google\\.com/drive/folders/(?<id>[a-zA-Z0-9_-]+)(\\?.*)?$")]
    private static partial Regex DriveLinkRegex();

    [GeneratedRegex("https://(?<prefix>giarc\\.)?craig\\.horse/rec/(?<id>[a-zA-Z0-9_]+)\\?key=(?<key>[a-zA-Z0-9_]+)")]
    private static partial Regex CraigRecordRegex();

    [GeneratedRegex("^[0-9]+-(?<name>[^:]+)", RegexOptions.Multiline)]
    private static partial Regex SnipUserName();
}