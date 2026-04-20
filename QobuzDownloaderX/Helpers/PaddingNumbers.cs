using System;
using System.Globalization;

using QopenAPI;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class PaddingNumbers
    {
        public Album QoAlbum = new Album();

        private static int GetPaddingLength(int count)
        {
            if (count <= 0)
            {
                return 2;
            }

            return Math.Max(2, count.ToString(CultureInfo.InvariantCulture).Length);
        }

        public int padTracks(Album QoAlbum)
        {
            return GetPaddingLength(QoAlbum?.TracksCount ?? 0);
        }

        public int padPlaylistTracks(Playlist QoPlaylist)
        {
            return GetPaddingLength(QoPlaylist?.TracksCount ?? 0);
        }

        public int padDiscs(Album QoAlbum)
        {
            return GetPaddingLength(QoAlbum?.MediaCount ?? 0);
        }
    }
}
