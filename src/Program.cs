using CraigRec2Telegram;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Microsoft.CognitiveServices.Speech;
using OpenQA.Selenium;
using OpenQA.Selenium.Edge;
using System.Reactive.Linq;
using Telegram.Bot;

[assembly: Fody.ConfigureAwait(false)]

static partial class Program
{
    static async Task Main()
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseUrls("http://+:5000");
        builder.Services.AddHealthChecks().AddCheck<TelegramBotPoll>("CurrentlyProcessing");
        builder.Services.AddSingleton<TelegramBotPoll>();
        builder.Services.AddHostedService(c => c.GetRequiredService<TelegramBotPoll>());
        builder.Services.AddTransient<ITelegramBotClient>(c => new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_API_TOKEN")!));
        builder.Services.AddTransient<IGoogleDriveRepository, GoogleDriveRepository>();
        builder.Services.AddTransient<ICraigDownloader, CraigDownloader>();
        builder.Services.AddLogging(loggingBuilder => loggingBuilder.AddConsole());
        builder.Services.AddTransient<Func<string, IWebDriver>>(_ => downloadFolder =>
        {
            var options = new EdgeOptions();
            options.AddUserProfilePreference("profile.default_content_setting_values.notifications", 1);
            options.AddAdditionalOption("download.default_directory", downloadFolder);
            options.AddArgument("headless");
            options.AddArgument("disable-gpu");
            return new EdgeDriver(options);
        });
        builder.Services.AddTransient<IConfigurableHttpClientInitializer>(_ => new ServiceAccountCredential(
            new ServiceAccountCredential.Initializer(Environment.GetEnvironmentVariable("GOOGLE_DRIVE_EMAIL"))
            {
                Scopes = new[] { DriveService.Scope.Drive }
            }.FromPrivateKey(Environment.GetEnvironmentVariable("GOOGLE_DRIVE_PRIVATE_KEY"))));
        builder.Services.AddSingleton(SpeechConfig.FromSubscription(Environment.GetEnvironmentVariable("SPEECH_SERVICES_KEY"), Environment.GetEnvironmentVariable("SPEECH_SERVICES_REGION")));
        builder.Services.AddTransient<IAudioToTextConverter, AudioToTextConverter>();

        var app = builder.Build();

        app.MapHealthChecks("/healthz/ready");

        await app.RunAsync();
    }
}