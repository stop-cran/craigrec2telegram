namespace CraigRec2Telegram
{
    public interface IAudioToTextConverter
    {
        Task<(string plainText, string subtitles)> ConvertAsync(string mp3FilePath, Action<string> progressCallback, CancellationToken cancellationToken);
    }
}
