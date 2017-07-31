﻿using iTunesLib;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.IO;

namespace mySync
{
    // ReSharper disable once InconsistentNaming
    internal class iTunesLinker
    {
        private readonly IiTunes _iTunes = new iTunesApp();
        private Action<string> _funcCountdown;

        public iTunesLinker()
        {
            ThreadPool.SetMaxThreads(4, 4);
        }

        public string Version => _iTunes.Version;

        private void AsyncConvert(IITTrack track, string outDirectory, SyncConfiguration conf)
        {
            if (!(track is IITFileOrCDTrack)) return;
            var inPath = ((IITFileOrCDTrack)track).Location;
            var outPath = (outDirectory + GetPersistentId(track) + ".m4a");

            if (inPath == null) return;

            if (conf.Force || !File.Exists(outPath))
            {
                ThreadPool.QueueUserWorkItem(ConvertInternal, new ConvertInfo
                {
                    InPath = inPath,
                    OutPath = outPath,
                    BitrateKbps = conf.BitrateKbps,
                    Start = track.Start,
                    End = track.Finish,
                    TagInfoProvide = new TrackTagInfo { Track = track },
                    StatusBroadcaster = conf.StatusBroadcaster
                });
            }
            else
            {
                conf.StatusBroadcaster.IncProgress();
                _funcCountdown(outPath);
            }
        }

        private void ConvertInternal(object param)
        {
            var convertInfo = (ConvertInfo)param;
            FFmpegConverter.Convert(convertInfo);
            RebuildTag(convertInfo);

            _funcCountdown(convertInfo.OutPath);
            convertInfo.StatusBroadcaster.IncProgress();
        }

        private void RebuildTag(ConvertInfo convertInfo)
        {
            var mediaFile = TagLib.File.Create(convertInfo.OutPath);
            var track = (IITFileOrCDTrack) ((TrackTagInfo) convertInfo.TagInfoProvide).Track;

            var tag = mediaFile.Tag;
            tag.Title = track.Name;
            if (track.SortName != track.Name)
                tag.TitleSort = track.SortName;
            tag.Performers = new[] {track.Artist};
            if (track.SortArtist != track.Artist)
                tag.PerformersSort = new[] {track.SortArtist};
            tag.Album = track.Album;
            if (track.SortAlbum != track.Album)
                tag.AlbumSort = track.SortAlbum;
            tag.AlbumArtists = new[] {track.AlbumArtist};
            if (track.SortAlbumArtist != track.AlbumArtist)
                tag.AlbumArtistsSort = new[] {track.SortAlbumArtist};
            tag.Composers = new[] {track.Composer};
            if (track.SortComposer != track.Composer)
                tag.ComposersSort = new[] {track.SortComposer};

            tag.Genres = new[] {track.Genre};
            if (track.Year > 0) tag.Year = (uint) track.Year;
            if (track.TrackNumber > 0) tag.Track = (uint) track.TrackNumber;
            if (track.TrackCount > 0) tag.TrackCount = (uint) track.TrackCount;
            if (track.DiscNumber > 0) tag.Disc = (uint) track.DiscNumber;
            if (track.DiscCount > 0) tag.DiscCount = (uint) track.DiscCount;
            if (track.BPM > 0) tag.BeatsPerMinute = (uint) track.BPM;

            tag.Lyrics = track.Lyrics;
            tag.Comment = track.Comment;

            var arts = track.Artwork;
            tag.Pictures = (
                from IITArtwork artwork
                in arts
                select WriteArtwork(track, artwork)
            ).ToArray();

            mediaFile.RemoveTags(mediaFile.TagTypes & ~ mediaFile.TagTypesOnDisk);
            mediaFile.Save();
            mediaFile.Dispose();
        }

        private TagLib.IPicture WriteArtwork(IITTrack track, IITArtwork artwork)
        {
            string suffix;
            switch (artwork.Format)
            {
                case ITArtworkFormat.ITArtworkFormatBMP: suffix = ".bmp"; break;
                case ITArtworkFormat.ITArtworkFormatJPEG: suffix = ".jpg"; break;
                case ITArtworkFormat.ITArtworkFormatPNG: suffix = ".png"; break;
                default: suffix = ".jpg"; break;
            }
            
            string temp = Path.GetTempPath() + GetPersistentId(track) + suffix;
            if (File.Exists(temp)) File.Delete(temp);
            artwork.SaveArtworkToFile(temp);

            TagLib.IPicture picture = new TagLib.Picture(temp);

            File.Delete(temp);
            return picture;
        }

        private class TrackTagInfo : ConvertInfo.ITagInfo
        {
            public IITTrack Track { get; set; }
        }
        
        public void CheckSync(Action<string> funcCountDown, SyncConfiguration configuration, string tempDirectory, out CountDownLatch waiter)
        {
            _funcCountdown = funcCountDown;
            EnsureAllTrackAvailable();

            var availableTracks = 
                (from IITTrack track
                in _iTunes.LibraryPlaylist.Tracks
                where (track as IITFileOrCDTrack)?.Location != null
                select track).ToList();

            configuration.StatusBroadcaster.ProgressValue = 0;
            configuration.StatusBroadcaster.ProgressMax = availableTracks.Count;

            foreach (var track in availableTracks)
            {
                AsyncConvert(track, tempDirectory, configuration);
            }
            waiter = new CountDownLatch(availableTracks.Count);
        }

        private void EnsureAllTrackAvailable()
        {
            foreach (IITTrack track in _iTunes.LibraryPlaylist.Tracks)
            {
                var nTrack = track as IITFileOrCDTrack;
                if (nTrack != null)
                {
                    // ReSharper disable once UnusedVariable
                    var temp = nTrack.Location;
                }
            }
        }

        public void MakePlaylist(string tempDirectory, Action<string> funcFileWrote)
        {
            if (!Directory.Exists(tempDirectory))
                Directory.CreateDirectory(tempDirectory);
            var files = from existsFile in Directory.EnumerateFiles(tempDirectory)
                        where existsFile.ToLower().EndsWith(".m3u")
                        select existsFile;
            foreach (var file in files)
                File.Delete(file);
            foreach (IITPlaylist playlist in _iTunes.LibrarySource.Playlists)
            {
                var filename = GetNewPlaylistFileName(tempDirectory, playlist.Name);

                var lines = 
                    from IITTrack track 
                    in playlist.Tracks
                    where (track as IITFileOrCDTrack)?.Location != null
                    select "./" + GetPersistentId(track) + ".m4a";

                // ReSharper disable once LocalizableElement
                File.WriteAllText(filename, string.Join("\n", lines) + "\n", Encoding.UTF8);
                funcFileWrote(filename);
            }
        }

        private static string GetNewPlaylistFileName(string tempDirectory, string playlistName)
        {
            var filename = tempDirectory + playlistName + ".m3u";
            if (!File.Exists(filename)) return filename;
            for (var i = 1; File.Exists(filename); i++)
                filename = tempDirectory + playlistName + "-" + i + ".m3u";
            return filename;
        }

        private ulong GetPersistentId(IITObject obj)
        {
            int high, low;
            object nobj = obj;
            _iTunes.GetITObjectPersistentIDs(ref nobj, out high, out low);
            return ((ulong) high << 32) + (ulong) low;
        }
    }
}
