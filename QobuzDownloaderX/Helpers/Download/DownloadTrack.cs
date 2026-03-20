using QobuzDownloaderX.Helpers;
using QobuzDownloaderX.Properties;
using QopenAPI;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZetaLongPaths;

namespace QobuzDownloaderX
{
    internal sealed class DownloadTrack
    {
        public Service QoService = new Service();
        public User QoUser = new User();
        public Item QoItem = new Item();
        public QopenAPI.Stream QoStream = new QopenAPI.Stream();

        public int paddedTrackLength { get; set; }
        public int paddedDiscLength { get; set; }

        public string downloadPath { get; set; }
        public string filePath { get; set; }

        readonly PaddingNumbers padNumber = new PaddingNumbers();
        readonly DownloadFile downloadFile = new DownloadFile();
        readonly RenameTemplates renameTemplates = new RenameTemplates();
        readonly GetInfo getInfo = new GetInfo();
        // readonly FixMD5 fixMD5 = new FixMD5(); // UNUSED

        public void clearOutputText() => getInfo.outputText = null;

        private sealed class ResolvedStreamSelection
        {
            public QopenAPI.Stream Stream { get; set; }
            public string EffectiveFormatId { get; set; }
            public string EffectiveAudioFormat { get; set; }
            public int? EffectiveBitDepth { get; set; }
            public double? EffectiveSamplingRate { get; set; }
        }

        private static string[] GetFormatFallbackChain(string requestedFormatId)
        {
            switch (requestedFormatId)
            {
                case "27":
                    return new[] { "27", "7", "6" };
                case "7":
                    return new[] { "7", "6" };
                case "6":
                case "5":
                    return new[] { requestedFormatId };
                default:
                    return new[] { requestedFormatId };
            }
        }

        private static string GetAudioFormatFromMimeType(string mimeType, string fallbackAudioFormat)
        {
            if (mimeType != null)
            {
                if (mimeType.IndexOf("flac", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".flac";
                }

                if (mimeType.IndexOf("mpeg", StringComparison.OrdinalIgnoreCase) >= 0
                    || mimeType.IndexOf("mp3", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ".mp3";
                }
            }

            return fallbackAudioFormat;
        }

        private ResolvedStreamSelection ResolveBestAvailableStream(string app_id, string requestedFormatId, string user_auth_token, string app_secret, Item qoItem, string fallbackAudioFormat)
        {
            foreach (string candidateFormatId in GetFormatFallbackChain(requestedFormatId))
            {
                try
                {
                    var qoStream = QoService.TrackGetFileUrl(qoItem.Id.ToString(), candidateFormatId, app_id, user_auth_token, app_secret);
                    if (qoStream == null || string.IsNullOrWhiteSpace(qoStream.StreamURL))
                    {
                        continue;
                    }

                    SecurityHelpers.RequireHttpsUrl(qoStream.StreamURL, "Stream");

                    int? bitDepth = SecurityHelpers.TryParseInt(qoStream.BitDepth, out int parsedBitDepth)
                        ? parsedBitDepth
                        : (int?)null;

                    double? samplingRate = SecurityHelpers.TryParseDouble(qoStream.SampleRate, out double parsedSampleRate)
                        ? parsedSampleRate
                        : (double?)null;

                    string effectiveFormatId = qoStream.FormatID > 0
                        ? qoStream.FormatID.ToString()
                        : candidateFormatId;

                    if (!string.Equals(effectiveFormatId, requestedFormatId, StringComparison.Ordinal))
                    {
                        qbdlxForm._qbdlxForm.logger.Warning(
                            $"Requested format {requestedFormatId} was unavailable for track {qoItem.Id}; using format {effectiveFormatId} instead.");
                    }

                    return new ResolvedStreamSelection
                    {
                        Stream = qoStream,
                        EffectiveFormatId = effectiveFormatId,
                        EffectiveAudioFormat = GetAudioFormatFromMimeType(qoStream.MimeType, fallbackAudioFormat),
                        EffectiveBitDepth = bitDepth,
                        EffectiveSamplingRate = samplingRate
                    };
                }
                catch (Exception ex)
                {
                    qbdlxForm._qbdlxForm.logger.Warning(
                        $"Failed to resolve track stream for track {qoItem.Id} with format {candidateFormatId}: {ex.Message}");
                }
            }

            return null;
        }

        private bool VerifyStreamable(Item QoItem, int paddedTrackLength)
        {
            if (QoItem.Streamable || !Settings.Default.streamableCheck) return true;
            string msg = $"{qbdlxForm._qbdlxForm.downloadOutputTrNotStream.Replace("{TrackNumber}", QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0'))}\r\n";
            getInfo.updateDownloadOutput(msg);
            Miscellaneous.LogNotStreamableTrackEntry(downloadPath, QoItem, msg);
            return false;
        }

        private bool CheckForExistingFile(string filePath, int paddedTrackLength, Item QoItem)
        {
            if (ZlpIOHelper.FileExists(filePath))
            {
                getInfo.updateDownloadOutput($"{qbdlxForm._qbdlxForm.downloadOutputFileExists.Replace("{TrackNumber}", QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0'))}\r\n");
                return true;
            }
            return false;
        }

        private void CleanupArtwork()
        {
            string artworkPath = downloadFile.embeddedArtworkPath;
            if (ZlpIOHelper.FileExists(artworkPath))
            {
                ZlpIOHelper.DeleteFile(artworkPath);
            }
        }

        private void CreatePadding(Album QoAlbum, Playlist QoPlaylist)
        {
            if (QoPlaylist != null)
            {
                paddedTrackLength = padNumber.padPlaylistTracks(QoPlaylist);
                paddedDiscLength = 2;
            }
            else
            {
                paddedTrackLength = padNumber.padTracks(QoAlbum);
                paddedDiscLength = padNumber.padDiscs(QoAlbum);
            }
        }

        private async Task DownloadAndSaveTrack(string downloadType, ResolvedStreamSelection resolvedStream, Album QoAlbum, Item QoItem, Playlist QoPlaylist, string downloadPath, string filePath, int paddedTrackLength, DownloadStats stats, CancellationToken abortToken)
        {
            string streamURL = resolvedStream?.Stream?.StreamURL;
            if (string.IsNullOrWhiteSpace(streamURL))
            {
                string msg = $"{qbdlxForm._qbdlxForm.downloadOutputNoUrl.Replace("{TrackNumber}", QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0'))}\r\n";
                getInfo.updateDownloadOutput(msg);
                Miscellaneous.LogNotDownloadableTrackEntry(downloadPath, QoItem, msg);
                qbdlxForm._qbdlxForm.logger.Error(msg);
                return;
            }

            // Display download status (depending on track number or playlist position number)
            string trackNameFormatted = QoItem.Version == null
                                        ? QoItem.Title
                                        : $"{QoItem.Title.TrimEnd()} ({QoItem.Version})";
            trackNameFormatted = RenameTemplates.repeatedParenthesesRegex.Replace(trackNameFormatted, "($1)");

            if (QoPlaylist == null) { getInfo.updateDownloadOutput($"{qbdlxForm._qbdlxForm.downloadOutputDownloading} - {QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0')} {trackNameFormatted}…"); }
            else { getInfo.updateDownloadOutput($"{qbdlxForm._qbdlxForm.downloadOutputDownloading} - {QoItem.Position.ToString().PadLeft(paddedTrackLength, '0')} {trackNameFormatted}…"); }

            if (abortToken.IsCancellationRequested) { abortToken.ThrowIfCancellationRequested(); }

            // Download stream
            await downloadFile.DownloadStream(downloadType, streamURL, downloadPath, filePath, resolvedStream.EffectiveAudioFormat, QoAlbum, QoItem, getInfo, abortToken, stats);
        }

        public async Task DownloadTrackAsync(string downloadType, string app_id, string album_id, string format_id, string audio_format, string user_auth_token, string app_secret, string downloadLocation, string artistTemplate, string albumTemplate, string trackTemplate, Album QoAlbum, Item QoItem, IProgress<int> progress, DownloadStats stats, CancellationToken abortToken)
        {
            // Empty output on main form if individual track download
            if (downloadType == "track") { getInfo.outputText = null; }

            try
            {
                // Create padding for tracks and possible multi-volume releases
                CreatePadding(QoAlbum, null);

                try
                {
                    // Get track info with auth if not there already
                    if (QoItem == null) { QoItem = QoService.TrackGetWithAuth(app_id, album_id, user_auth_token); }

                    // Check if QoItem is still null. If so, skip.
                    if (QoItem == null)
                    {
                        string msg = $"{qbdlxForm._qbdlxForm.downloadOutputAPIError}\r\n";
                        getInfo.updateDownloadOutput(msg);

                        string downloadPath = null;
                        string directoryPath = null;
                        try
                        {
                            downloadPath = await downloadFile.createPath(downloadLocation, artistTemplate, albumTemplate, trackTemplate, null, null, paddedTrackLength, paddedDiscLength, QoAlbum, QoItem, null);
                            directoryPath = Path.GetDirectoryName(downloadPath);
                        }
                        catch
                        {
                            directoryPath = downloadLocation;
                        }
                        Miscellaneous.LogNotStreamableAlbumEntry(directoryPath, QoAlbum, msg);

                        progress?.Report(100);
                        return;
                    }

                    // Verify Streamable
                    try { if (!VerifyStreamable(QoItem, paddedTrackLength)) { progress?.Report(100); return; } }
                    catch (Exception ex) { qbdlxForm._qbdlxForm.logger.Error($"Unable to verify if track is streamable. Error below:\r\n{ex}"); }

                    ResolvedStreamSelection resolvedStream = ResolveBestAvailableStream(app_id, format_id, user_auth_token, app_secret, QoItem, audio_format);
                    if (resolvedStream == null)
                    {
                        string msg = $"{qbdlxForm._qbdlxForm.downloadOutputNoUrl.Replace("{TrackNumber}", QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0'))}\r\n";
                        getInfo.updateDownloadOutput(msg);
                        Miscellaneous.LogNotDownloadableTrackEntry(downloadLocation, QoItem, msg);
                        qbdlxForm._qbdlxForm.logger.Error(msg);
                        progress?.Report(100);
                        return;
                    }

                    string effectiveAudioFormat = resolvedStream.EffectiveAudioFormat ?? audio_format;

                    // Setting up download and file path
                    downloadPath = await downloadFile.createPath(
                        downloadLocation,
                        artistTemplate,
                        albumTemplate,
                        trackTemplate,
                        null,
                        null,
                        paddedTrackLength,
                        paddedDiscLength,
                        QoAlbum,
                        QoItem,
                        null,
                        downloadType == "track" ? resolvedStream.EffectiveFormatId : null,
                        downloadType == "track" ? resolvedStream.EffectiveBitDepth : null,
                        downloadType == "track" ? resolvedStream.EffectiveSamplingRate : null);

                    string trackTemplateConverted = renameTemplates.renameTemplates(
                        trackTemplate,
                        paddedTrackLength,
                        paddedDiscLength,
                        effectiveAudioFormat,
                        QoAlbum,
                        QoItem,
                        null,
                        resolvedStream.EffectiveFormatId,
                        resolvedStream.EffectiveBitDepth,
                        resolvedStream.EffectiveSamplingRate);

                    if (trackTemplateConverted.Contains(@"\"))
                    {
                        string trackSubDirectory = trackTemplateConverted.Substring(0, trackTemplateConverted.LastIndexOf(@"\"));
                        downloadPath = SecurityHelpers.EnsureTrailingDirectorySeparator(SecurityHelpers.EnsurePathIsWithinRoot(downloadLocation, Path.Combine(downloadPath, trackSubDirectory), nameof(downloadPath)));
                        trackTemplateConverted = trackTemplateConverted.Substring(trackTemplateConverted.LastIndexOf(@"\") + 1);
                        Debug.WriteLine(downloadPath);
                    }

                    // Create subfolders for multi-volume releases
                    if (!(downloadType == "track") && QoAlbum.MediaCount > 1 && (!string.IsNullOrWhiteSpace(Settings.Default.savedCdTemplate)))
                    {
                        string cdDirectoryName = qbdlxForm.discNumberRegex.Replace(Settings.Default.savedCdTemplate, QoItem.MediaNumber.ToString());
                        filePath = Path.Combine(downloadPath, cdDirectoryName, trackTemplateConverted.TrimEnd() + effectiveAudioFormat);
                        // Previous, original logic:
                        // filePath = downloadPath + "CD " + QoItem.MediaNumber.ToString().PadLeft(paddedDiscLength, '0') + ZlpPathHelper.DirectorySeparatorChar + trackTemplateConverted.TrimEnd() + audio_format;
                    }
                    else
                    {
                        filePath = Path.Combine(downloadPath, trackTemplateConverted.TrimEnd() + effectiveAudioFormat);
                    }
                    filePath = SecurityHelpers.EnsurePathIsWithinRoot(downloadLocation, filePath, nameof(filePath));

                    if (qbdlxForm.duplicateFileMode == DuplicateFileMode.SkipDownloads)
                    {
                        // Check for Existing File
                        if (CheckForExistingFile(filePath, paddedTrackLength, QoItem))
                        {
                            progress?.Report(100);
                            return;
                        }
                    }

                    // Download cover art
                    try { await downloadFile.DownloadArtwork(downloadPath, QoAlbum); } catch (Exception ex) { qbdlxForm._qbdlxForm.logger.Error($"Failed to Download Cover Art. Error below:\r\n{ex}"); }

                    // Download and Save Track
                    await DownloadAndSaveTrack(downloadType, resolvedStream, QoAlbum, QoItem, null, downloadPath, filePath, paddedTrackLength, stats, abortToken);

                    if (abortToken.IsCancellationRequested) { abortToken.ThrowIfCancellationRequested(); }
                    progress?.Report(100);
                }
                catch (OperationCanceledException ex)
                {
                    if (!abortToken.IsCancellationRequested)
                    {
                        qbdlxForm._qbdlxForm.logger.Error("Download canceled: " + ex.Message);
                        throw;
                    }
                }
                catch (Exception downloadAlbumEx)
                {
                    getInfo.updateDownloadOutput("\r\n\r\n" + downloadAlbumEx + "\r\n\r\n");
                    Debug.WriteLine(downloadAlbumEx);
                    return;
                }
                finally
                {
                    if (!(downloadType == "album"))
                    {
                        // Delete image used for embedded artwork
                        Miscellaneous.DeleteTempEmbeddedArtwork();
                    }
                }
            }
            catch (Exception downloadAlbumEx)
            {
                Debug.WriteLine(downloadAlbumEx);
                return;
            }
        }

        public async Task DownloadPlaylistTrackAsync(string downloadType, string app_id, string format_id, string audio_format, string user_auth_token, string app_secret, string downloadLocation, string trackTemplate, string playlistTemplate, Album QoAlbum, Item QoItem, Playlist QoPlaylist, IProgress<int> progress, DownloadStats stats, CancellationToken abortToken)
        {
            try
            {
                // Create padding for tracks
                CreatePadding(null, QoPlaylist);

                // Verify Streamable
                if (!VerifyStreamable(QoItem, paddedTrackLength)) return;

                try
                {
                    ResolvedStreamSelection resolvedStream = ResolveBestAvailableStream(app_id, format_id, user_auth_token, app_secret, QoItem, audio_format);
                    if (resolvedStream == null)
                    {
                        string msg = $"{qbdlxForm._qbdlxForm.downloadOutputNoUrl.Replace("{TrackNumber}", QoItem.TrackNumber.ToString().PadLeft(paddedTrackLength, '0'))}\r\n";
                        getInfo.updateDownloadOutput(msg);
                        Miscellaneous.LogNotDownloadableTrackEntry(downloadLocation, QoItem, msg);
                        qbdlxForm._qbdlxForm.logger.Error(msg);
                        progress?.Report(100);
                        return;
                    }

                    string effectiveAudioFormat = resolvedStream.EffectiveAudioFormat ?? audio_format;

                    // Setting up download and file path
                    downloadPath = await downloadFile.createPath(downloadLocation, null, null, trackTemplate, playlistTemplate, null, paddedTrackLength, paddedDiscLength, QoAlbum, QoItem, QoPlaylist);
                    string trackTemplateConverted = renameTemplates.renameTemplates(
                        trackTemplate,
                        paddedTrackLength,
                        paddedDiscLength,
                        effectiveAudioFormat,
                        QoAlbum,
                        QoItem,
                        QoPlaylist,
                        resolvedStream.EffectiveFormatId,
                        resolvedStream.EffectiveBitDepth,
                        resolvedStream.EffectiveSamplingRate);

                    if (trackTemplateConverted.Contains(@"\"))
                    {
                        string trackSubDirectory = trackTemplateConverted.Substring(0, trackTemplateConverted.LastIndexOf(@"\"));
                        downloadPath = SecurityHelpers.EnsureTrailingDirectorySeparator(SecurityHelpers.EnsurePathIsWithinRoot(downloadLocation, Path.Combine(downloadPath, trackSubDirectory), nameof(downloadPath)));
                        trackTemplateConverted = trackTemplateConverted.Substring(trackTemplateConverted.LastIndexOf(@"\") + 1);
                        Debug.WriteLine(downloadPath);
                    }

                    filePath = Path.Combine(downloadPath, trackTemplateConverted.TrimEnd() + effectiveAudioFormat);
                    filePath = SecurityHelpers.EnsurePathIsWithinRoot(downloadLocation, filePath, nameof(filePath));

                    if (qbdlxForm.duplicateFileMode == DuplicateFileMode.SkipDownloads)
                    {
                        // Check for Existing File
                        if (CheckForExistingFile(filePath, paddedTrackLength, QoItem))
                        {
                            progress?.Report(100);
                            return;
                        }
                    }

                    // Download cover art
                    try
                    {
                        await downloadFile.DownloadArtwork(downloadPath, QoAlbum);
                    }
                    catch { }

                    // Download and Save Track
                    await DownloadAndSaveTrack(downloadType, resolvedStream, QoAlbum, QoItem, QoPlaylist, downloadPath, filePath, paddedTrackLength, stats, abortToken);

                    // Delete image used for embedded artwork
                    CleanupArtwork();
                    progress?.Report(100);
                }
                catch (OperationCanceledException ex)
                {
                    if (!abortToken.IsCancellationRequested)
                    {
                        qbdlxForm._qbdlxForm.logger.Error("Download canceled: " + ex.Message);
                        throw;
                    }
                }
                catch (Exception downloadAlbumEx)
                {
                    getInfo.updateDownloadOutput("\r\n\r\n" + downloadAlbumEx + "\r\n\r\n");
                    Debug.WriteLine(downloadAlbumEx);
                    return;
                }

            }
            catch (Exception downloadAlbumEx)
            {
                Debug.WriteLine(downloadAlbumEx);
                return;
            }
        }
    }
}
