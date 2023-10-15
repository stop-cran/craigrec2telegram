namespace CraigRec2Telegram
{
    public interface IAudioToTextConverter
    {
        Task<(byte[] plainText, byte[] subtitles)> ConvertAsync(string mp3FilePath, CancellationToken cancellationToken);
        IObservable<string> Progress { get; }
    }
}
