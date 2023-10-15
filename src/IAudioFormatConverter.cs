namespace CraigRec2Telegram
{
    public interface IAudioFormatConverter
    {
        Task<string> ConvertToMp3Async(string m4aFilePath, CancellationToken cancellationToken);
    }
}