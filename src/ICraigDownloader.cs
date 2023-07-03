namespace CraigRec2Telegram
{
    public interface ICraigDownloader
    {
        Task<string> DownloadRecord(string url, string downloadFolder, Func<string, Task> setStatusText, CancellationToken cancel);
    }
}