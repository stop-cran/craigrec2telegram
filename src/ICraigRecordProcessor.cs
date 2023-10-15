using Telegram.Bot;

namespace CraigRec2Telegram
{
    public interface ICraigRecordProcessor
    {
        Task ProcessRecord(ITelegramBotClient client, long chatId, bool giarc, string recordId, string recordKey, string recordName, string driveFolderId, CancellationToken cancel);
    }
}