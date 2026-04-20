using System;
using System.Diagnostics;
using System.IO;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class FixMD5
    {
        readonly GetInfo getInfo = new GetInfo();

        public string outputResult { get; set; }

        public void fixMD5(string filePath, string flacEXEPath)
        {
            qbdlxForm._qbdlxForm.logger.Debug("Attempting to fix unset MD5…");

            try
            {
                string flacExecutablePath = SecurityHelpers.ResolveExecutablePath(flacEXEPath);
                qbdlxForm._qbdlxForm.logger.Debug("Running FLAC command directly to fix MD5…");

                using (Process cmd = new Process())
                {
                    cmd.StartInfo = new ProcessStartInfo
                    {
                        FileName = flacExecutablePath,
                        Arguments = $"-f8 \"{filePath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = Path.GetDirectoryName(flacExecutablePath)
                    };

                    cmd.Start();
                    cmd.WaitForExit();

                    if (cmd.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"flac exited with code {cmd.ExitCode}.");
                    }
                }

                outputResult = "COMPLETE";
                qbdlxForm._qbdlxForm.logger.Debug("MD5 has been fixed for file!");
            }
            catch (Exception fixMD5ex)
            {
                outputResult = "FAILED";
                qbdlxForm._qbdlxForm.logger.Error("Failed to fix MD5s, error below:\r\n" + fixMD5ex);
            }
        }
    }
}
