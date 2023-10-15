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
        builder.Services.AddTransient<ICraigRecordProcessor, CraigRecordProcessor>();
        builder.Services.AddTransient<IGoogleDriveRepository, GoogleDriveRepository>();
        builder.Services.AddTransient<IAudioFormatConverter, AudioFormatConverter>();
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
            new ServiceAccountCredential.Initializer("craigrec2telegram@k8s-course-300712.iam.gserviceaccount.com")
            {
                Scopes = new[] { DriveService.Scope.Drive }
            }.FromPrivateKey("-----BEGIN PRIVATE KEY-----MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDBkbGxuXI7HgsWKzVSDi/FevU6Qd6c4fDeknP+/ytXKFugJUmb6C/0FGKn1y08dyYAHLUxI6CDxeLPDFspV3RDivySDjKoXhfT59X6lrHCILh5VI9wVj0DT469Pivr337bKPXCpsYxOPCupjJf8WHGC59Yzv1LRbVkzyYU4uOTNfosaqbxNjy9TFVmJEiclghIxsU8zNDKwfcB6j1Qur6ybok8WkjCSXbUxY5W7TfG2pnvOlOR2hFUC53yyhIch3EETJVI5O29CE+0qhduZrSq/qbSZrzVbc5tkGHMtktIhhhVpTZau35EVccrG9HL3yZKh8NSlYqX689UHsHPYtjxAgMBAAECggEAGuXGqiJRif70/e3foSkZAMlC+ccs6qJn0NnLdg/ZoykXwFNmHNzBCxrpEZcQMWKCHIpgsDUZ6S2uhGRcZ4yAOqF90sLXzbcTew3wWTum9EmNMMjsDKljHYpo1Imm20Ypb8VLjzKTAESAB1jPT68wTa+QQywyHMtRNzKkwUq84lfkK+DmIw/JblImqIJJMhlwqKS9NGPMJ1+CeGN/9IkwR+dbxjyMjAd/OFaIekuWegIjL+h28ZeEZ9oi080AwrCOV604Zk1GUL7VszjSJtOgvmsd2QZ8s8AZpzSwYi9rS0asRCTeB/m9Mh4TGlT7RwATcGhgVPHsiZT8QAmTUs5ZNQKBgQDjhx60XQCDSmyWcX2WND4lRmgMu9sjmohlIoG4Tb+6pIIJ2sxtcLF3SxPtvMpXIoYwEHudnmKeEj6tUoahMOiHecG0CDxPZCpW0pKXDM7j/R9bXNai10vxnBHs0qfQjmIRZ/uoE3bN/6nV7+wmLd8TpEq/Ps9NyhHneAuzkhuTlQKBgQDZyrQTzqlt97XXCtUTB0PfGXdQqj0z2z9qDmkmQWPVOdKBAkNDYGD/vw41igF1vXNkt6xkwDNCjvFqfTMgwaRhA3QH0DBJOfY5/JfGjmTlp94Qf9lhbFNz/3BXuxYegOEoI8BOhR53gIiSjIQbE+/pJNdsSjCqk9RUlBDhhKBY7QKBgCu7zB4VBkU2J/se42ncrtlRWCyiazPDv1XZNR/s6d6BQnGMeeDLWYE5kCIROL9Y5nwUnv4j9Ia3sQ51n2UaVoe70oy9TIJiGXVI0l5wWtAd1kokZSk1wuY0/okL0K/YvmbkGs4qt5pO+yEhFb4c8RUAblGmTAiCv8BDJdSlT+GlAoGBAJciopqING1ak/a5zqlYxlHA01rH+JbO1A/eFjv07rmeWaUrE9Bixo1TYSHoNiElqjk/eMOl2SycE44JefyGRHMKOW/emlOGhIcy8YRZdk0kk+axQgHocXUy0xweeTgLybPYM3CJ0l2tdZj1KAu+ZyNMbK36QhFtatCSu7A0IC31AoGAUPbtr3fwljI7fiX+hGUAUKe5hJEn7ViwIuYp6NhYSz53bWC5H3rUDwUQRLp21tzS9671r4f6ItuVfQCYcKMazaQJ9lir29YLb+9kUQk2tEAuj7EkuyrMfLPoRvv+0QDOV5HZcmcwkQu/+/LhJL7yIFytw3gcCd2YGawash3oN80=-----END PRIVATE KEY-----")));
        builder.Services.AddSingleton(SpeechConfig.FromSubscription("0ae108f994374ed8a5dea62eb7de0901", "westus"));
        builder.Services.AddTransient<IAudioToTextConverter, AudioToTextConverter>();

        var app = builder.Build();

        app.MapHealthChecks("/healthz/ready");

        await app.RunAsync();
    }
}