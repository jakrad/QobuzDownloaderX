using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace QobuzDownloaderX.Helpers.Download
{
    internal static class OpusTranscoder
    {
        public static bool IsFfmpegAvailable()
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    p.Start();
                    p.WaitForExit(3000);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Transcodes flacPath to .opus at the given bitrate kbps.
        // Returns path of the new .opus file. Caller is responsible for deleting flacPath.
        public static async Task<string> TranscodeAsync(string flacPath, int bitrateKbps, CancellationToken ct)
        {
            string opusPath = Path.ChangeExtension(flacPath, ".opus");

            string args = $"-y -i \"{flacPath}\" -c:a libopus -b:a {bitrateKbps}k -vbr on"
                        + $" -map_metadata 0 -map 0:a -map 0:v? \"{opusPath}\"";

            qbdlxForm._qbdlxForm.logger.Debug($"ffmpeg transcode: {args}");

            using (var p = new Process())
            {
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };

                p.Start();

                string stderr = await p.StandardError.ReadToEndAsync();

                bool exited = await Task.Run(() => p.WaitForExit(300_000), ct);

                if (ct.IsCancellationRequested)
                {
                    try { p.Kill(); } catch { }
                    if (File.Exists(opusPath)) File.Delete(opusPath);
                    ct.ThrowIfCancellationRequested();
                }

                if (!exited || p.ExitCode != 0)
                {
                    if (File.Exists(opusPath)) File.Delete(opusPath);
                    qbdlxForm._qbdlxForm.logger.Error($"ffmpeg exited {p.ExitCode}:\n{stderr}");
                    throw new Exception($"ffmpeg failed (exit {p.ExitCode}). Is ffmpeg in PATH?");
                }

                qbdlxForm._qbdlxForm.logger.Debug("ffmpeg transcode complete.");
                return opusPath;
            }
        }
    }
}
