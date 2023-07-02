using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using OpenQA.Selenium.Interactions;
using OpenQA.Selenium.Support.UI;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

static class Program
{
    class CurrentlyProcessingUpdateCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (CurrentlyProcessingUpdate)
                return Task.FromResult(HealthCheckResult.Unhealthy("Currently processing update"));

            return Task.FromResult(HealthCheckResult.Healthy("Not processing update"));
        }
    }

    static async Task Main()
    {
        var cancel = new CancellationTokenSource();

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            cancel?.Cancel();
        };

        var builder = WebApplication.CreateBuilder();

        var listenUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");

        if (listenUrls != null)
            builder.WebHost.UseUrls(listenUrls);

        builder.Services.AddHealthChecks()
            .AddCheck<CurrentlyProcessingUpdateCheck>("Startup", tags: new[] { "ready" });

        var app = builder.Build();

        app.MapHealthChecks("/healthz/ready", new HealthCheckOptions
        {
            Predicate = healthCheck => healthCheck.Tags.Contains("ready")
        });

        var run = app.RunAsync(cancel.Token);

        var bot = new TelegramBotClient(Environment.GetEnvironmentVariable("bot-api-token")!);

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
        if (update.Type == UpdateType.Message && update.Message?.Type == MessageType.Text && update.Message.Text != null)
        {
            var text = update.Message.Text;
            var m = Regex.Match(text, @"https://(?<prefix>giarc\.)?craig\.horse/rec/(?<id>[a-zA-Z0-9_]+)\?key=(?<key>[a-zA-Z0-9_]+)");

            if (m.Success)
            {
                await ProcessRecord(client, update.Message.Chat.Id, m.Groups["prefix"].Success, m.Groups["id"].Value, m.Groups["key"].Value, cancel);
                return;
            }
        }

        if (update.Message != null)
        {
            await client.SendTextMessageAsync(update.Message.Chat.Id, "Присылай ссылку на запись в формате https://[giarc.]craig.horse/rec/<id>?key=<key>", cancellationToken: cancel);
        }
    }

    static Task OnPollingError(ITelegramBotClient client, Exception exception, CancellationToken cancel)
    {
        Console.WriteLine(exception);

        return Task.CompletedTask;
    }

    private static async Task ProcessRecord(ITelegramBotClient client, long chatId, bool giarc, string recordId, string recordKey, CancellationToken cancel)
    {
        try
        {
            CurrentlyProcessingUpdate = true;

            var statusMessage = await client.SendTextMessageAsync(chatId, "Открываю браузер...", cancellationToken: cancel);

            async Task SetStatusText(string statusText)
            {
                if (statusMessage.Text != statusText)
                    try
                    {
                        await client.EditMessageTextAsync(chatId, statusMessage.MessageId, statusText, cancellationToken: cancel);
                    }
                    catch (ApiRequestException ex) when (ex.Message.ToLowerInvariant().Contains("message is not modified"))
                    { }
            }

            string downloadFolder = "/tmp/download-" + Guid.NewGuid();

            Directory.CreateDirectory(downloadFolder);

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

            await SetStatusText("Жду обработки (1)...");
            var notFoundOrMpeg4Button = driver.FindElementAsync(By.XPath("//*[(name() = 'button' and .//span[text()='AAC'] and .//span[.//span[text()='(MPEG-4)']]and .//parent::div[.//preceding-sibling::div[.//h4[text() = 'Single-track mixed']]]) or (name() = 'span' and text() = 'Recording not found.')]"), cancel);

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
            driver.FindElementAsync(By.XPath("//button[text()='I understand.']"), cancel).Click();

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
                driver.FindElementAsync(By.XPath("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[contains(text(), 'To handle large projects')]]]"), cancel).Click();
                await SetStatusText($"Жду обработки ({4 + i * 2})...");
                driver.FindElementAsync(By.XPath("//button[contains(@class, 'row') and text()='OK' and .//parent::div[@class = 'dialog' and .//div[text() = 'Failed to acquire permission for persistent storage. Large projects will fail.']]]"), cancel).Click();
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

            await client.DeleteMessageAsync(chatId, statusMessage.MessageId, cancellationToken: cancel);
            await client.SendChatActionAsync(chatId, ChatAction.UploadVoice, cancellationToken: cancel);

            using (var file = new FileStream(Path.Combine(downloadFolder, downloadedFileName), FileMode.Open, FileAccess.Read))
            {
                await client.SendAudioAsync(chatId, InputFile.FromStream(file, recordId), cancellationToken: cancel);
            }

            foreach (var file in Directory.GetFiles(downloadFolder))
                System.IO.File.Delete(file);
            Directory.Delete(downloadFolder);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            await client.SendTextMessageAsync(chatId, "Ошибка!", cancellationToken: cancel);
        }
        finally
        {
            CurrentlyProcessingUpdate = false;
        }
    }

    private static IWebElement FindElementAsync(this IWebDriver driver, By by, CancellationToken cancellationToken = default)
    {
        var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));

        return wait.Until(d => d.FindElement(by));
    }
}