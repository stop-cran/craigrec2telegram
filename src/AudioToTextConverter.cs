using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
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

        private event Action<string> OnProgress = _ => { };

        public IObservable<string> Progress =>
            Observable.FromEvent<string>(
                handler => OnProgress += handler,
                handler => OnProgress -= handler);

        public async Task<(byte[] plainText, byte[] subtitles)> ConvertAsync(string m4aFilePath, CancellationToken cancellationToken)
        {
            var lastProgress = DateTime.Now;
            var mp3FilePath = Path.Join(Path.GetDirectoryName(m4aFilePath), Path.GetFileNameWithoutExtension(m4aFilePath)) + ".mp3";

            OnProgress($"Конвертирую в mp3...");

            using (var ffmpegProcess = Process.Start("ffmpeg", $"-i \"{m4aFilePath}\" \"{mp3FilePath}\""))
            {
                await ffmpegProcess.WaitForExitAsync(cancellationToken);

                if (ffmpegProcess.ExitCode != 0)
                {
                    logger.LogError(await ffmpegProcess.StandardError.ReadToEndAsync());
                    throw new ApplicationLogicException("Ошибка конвертации!");
                }
            }

            using var plainText = new MemoryStream();
            using var plainTextWriter = new StreamWriter(plainText);
            int subsCount = 1;
            using var subtitles = new MemoryStream();
            using var subtitlesWriter = new StreamWriter(subtitles);
            var result = new TaskCompletionSource<(byte[] plainText, byte[] subtitles)>();
            using var pushStream = AudioInputStream.CreatePushStream(AudioStreamFormat.GetCompressedFormat(AudioStreamContainerFormat.MP3));

            pushStream.Write(await File.ReadAllBytesAsync(mp3FilePath, cancellationToken));
            pushStream.Close();

            using var audioConfig = AudioConfig.FromStreamInput(pushStream);
            using var recognizer = new SpeechRecognizer(speechConfig, "ru-ru", audioConfig);

            recognizer.Recognized += (_, eventArgs) =>
            {
                try
                {
                    var start = TimeSpan.FromTicks(eventArgs.Result.OffsetInTicks);
                    var end = start + eventArgs.Result.Duration;

                    if (eventArgs.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        plainTextWriter.Write(eventArgs.Result.Text + " ");
                        subtitlesWriter.WriteLine(subsCount.ToString());
                        subsCount++;
                        subtitlesWriter.WriteLine($"{start:hh\\:mm\\:ss\\.fff} --> {end:hh\\:mm\\:ss\\.fff}");
                        subtitlesWriter.WriteLine(eventArgs.Result.Text);
                        subtitlesWriter.WriteLine();
                    }

                    OnProgress($"Распознавание {end:hh\\:mm\\:ss}...");
                }
                catch (Exception ex)
                {
                    OnProgress("Ошибка распознавания!");
                    logger.LogError("OnRecognized error", ex);
                    result.TrySetException(ex);
                }
            };

            recognizer.Canceled += (_, eventArgs) =>
            {
                try
                {
                    if (eventArgs.Reason == CancellationReason.Error)
                    {
                        logger.LogError(eventArgs.ErrorDetails);
                        OnProgress("Ошибка распознавания(2)!");
                    }
                    else if (eventArgs.Reason == CancellationReason.EndOfStream)
                    {
                        OnProgress("Распознавание завершено.");
                    }

                    plainTextWriter.Flush();
                    subtitlesWriter.Flush();
                    result.TrySetResult((plainText.ToArray(), subtitles.ToArray()));
                }
                catch (Exception ex)
                {
                    OnProgress("Ошибка распознавания (3)!");
                    logger.LogError("Canceled error", ex);
                    result.TrySetException(ex);
                }
            };

            recognizer.SessionStarted += (_, _) => OnProgress("Начинаю распознавание...");
            recognizer.SessionStopped += (_, _) => OnProgress("Конец распознавания.");

            await recognizer.StartContinuousRecognitionAsync();

            return await result.Task;
        }
    }
}
