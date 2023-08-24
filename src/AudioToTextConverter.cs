using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.Text;

namespace CraigRec2Telegram
{
    public class AudioToTextConverter : IAudioToTextConverter
    {
        private readonly SpeechConfig speechConfig;
        private readonly ILogger<AudioToTextConverter> logger;

        public AudioToTextConverter(SpeechConfig speechConfig, ILogger<AudioToTextConverter> logger)
        {
            this.speechConfig = speechConfig;
            this.logger = logger;
        }

        public async Task<(string plainText, string subtitles)> ConvertAsync(string mp3FilePath, Action<string> progressCallback, CancellationToken cancellationToken)
        {
            var plainText = new StringBuilder();
            int subsCount = 1;
            var subtitles = new StringBuilder();
            var result = new TaskCompletionSource<(string plainText, string subtitles)>();
            using var pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetCompressedFormat(AudioStreamContainerFormat.MP3));

            pushStream.Write(await File.ReadAllBytesAsync(mp3FilePath, cancellationToken));

            using var audioConfig = AudioConfig.FromStreamInput(pushStream);
            using var recognizer = new SpeechRecognizer(speechConfig, "ru-ru", audioConfig);

            recognizer.Recognized += (_, eventArgs) =>
            {
                var start = TimeSpan.FromSeconds(1e-7 * eventArgs.Result.OffsetInTicks);
                var end = start + eventArgs.Result.Duration;

                if (eventArgs.Result.Reason == ResultReason.RecognizedSpeech)
                {
                    plainText.Append(eventArgs.Result.Text);
                    subtitles.AppendLine(subsCount.ToString());
                    subsCount++;
                    subtitles.AppendLine($"{start:HH:mm:ss,fff} --> {end:HH:mm:ss,fff}");
                    subtitles.AppendLine(eventArgs.Result.Text);
                    subtitles.AppendLine();
                }

                progressCallback($"Распознавание {end:HH:mm}...");
            };

            recognizer.Canceled += (_, eventArgs) =>
            {
                if (eventArgs.Reason == CancellationReason.Error)
                {
                    logger.LogError(eventArgs.ErrorDetails);
                    progressCallback("Ошибка распознавания!");
                }
                else if (eventArgs.Reason == CancellationReason.EndOfStream)
                {
                    progressCallback("Распознавание завершено.");
                }

                result.TrySetResult((plainText.ToString(), subtitles.ToString()));
            };

            recognizer.SessionStarted += (_, _) => progressCallback($"Начинаю распознавание...");
            recognizer.SessionStopped += (_, _) => result.TrySetResult((plainText.ToString(), subtitles.ToString()));

            await recognizer.StartContinuousRecognitionAsync();

            return await result.Task;
        }
    }
}
