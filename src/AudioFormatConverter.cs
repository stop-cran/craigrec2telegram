using System.Diagnostics;

namespace CraigRec2Telegram
{
    public class AudioFormatConverter : IAudioFormatConverter
    {
        private readonly ILogger<AudioFormatConverter> logger;

        public AudioFormatConverter(ILogger<AudioFormatConverter> logger)
        {
            this.logger = logger;
        }

        public async Task<string> ConvertToMp3Async(string m4aFilePath, CancellationToken cancellationToken)
        {
            var mp3FilePath = Path.Join(Path.GetDirectoryName(m4aFilePath), Path.GetFileNameWithoutExtension(m4aFilePath)) + ".mp3";

            using (var ffmpegProcess = Process.Start(new ProcessStartInfo("ffmpeg", $"-i \"{m4aFilePath}\" \"{mp3FilePath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                await ffmpegProcess!.WaitForExitAsync(cancellationToken);

                if (ffmpegProcess.ExitCode != 0)
                {
                    string stderr = await ffmpegProcess.StandardError.ReadToEndAsync(cancellationToken);

                    logger.LogError(stderr);
                    throw new ApplicationLogicException("Ошибка конвертации!");
                }
            }

            return mp3FilePath;
        }
    }
}
