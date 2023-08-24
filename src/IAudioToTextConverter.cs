namespace CraigRec2Telegram
{
    public interface IAudioToTextConverter
    {
        Task<(byte[] plainText, byte[] subtitles)> ConvertAsync(string m4aFilePath, CancellationToken cancellationToken);
        IObservable<string> Progress { get; }
    }
}
