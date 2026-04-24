using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace QobuzDownloaderX.Helpers.Download
{
    internal static class OpusTranscoder
    {
        // Read PATH from registry so we pick up winget installs even when the app
        // was launched before PATH was updated in the current process environment.
        private static string ResolveFfmpegExe()
        {
            string userPath = Registry.GetValue(
                @"HKEY_CURRENT_USER\Environment", "PATH", "") as string ?? "";
            string sysPath = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment",
                "Path", "") as string ?? "";

            foreach (string dir in (userPath + ";" + sysPath).Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return "ffmpeg"; // last resort: let OS try
        }

        public static bool IsFfmpegAvailable()
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = ResolveFfmpegExe(),
                        Arguments = "-version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    p.Start();
                    p.StandardInput.Close();
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

            string ffmpegExe = ResolveFfmpegExe();
            string args = $"-y -i \"{flacPath}\" -c:a libopus -b:a {bitrateKbps}k -vbr on"
                        + $" -map_metadata 0 -map 0:a \"{opusPath}\"";

            qbdlxForm._qbdlxForm.logger.Debug($"ffmpeg path: {ffmpegExe}");
            qbdlxForm._qbdlxForm.logger.Debug($"ffmpeg transcode: {args}");

            using (var p = new Process())
            {
                // Route through cmd.exe so ffmpeg gets a proper console environment.
                // Direct UseShellExecute=false spawn from a WinForms process gives
                // ffmpeg null stdio handles which causes an immediate crash.
                p.StartInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    // cmd /c strips the first and last " from the argument when it
                    // starts with ". Wrapping in an extra pair of outer quotes means
                    // cmd strips those and leaves the inner quoted path intact.
                    Arguments = $"/c \"\"{ffmpegExe}\" {args}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                p.Start();
                p.StandardInput.Close();

                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                string stderr = await p.StandardError.ReadToEndAsync();
                await stdoutTask;

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
                    throw new Exception($"ffmpeg failed (exit {p.ExitCode}) at \"{ffmpegExe}\". Is ffmpeg installed?");
                }

                qbdlxForm._qbdlxForm.logger.Debug("ffmpeg transcode complete.");
                return opusPath;
            }
        }
    }
}
