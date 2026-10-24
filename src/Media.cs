using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Nowcast
{
    class Track
    {
        public string App;
        public SourceInfo Source;
        public string Artist;
        public string Title;
        public string Album;
        public bool IsPlaying;
        public bool IsCurrent;
        // Seconds; Position is as of UpdatedAt (Unix ms, 0 when unknown).
        public double Position;
        public double Duration;
        public long UpdatedAt;
        public object Thumbnail;

        public string Key
        {
            get { return App + "|" + Artist + "|" + Title; }
        }
    }

    // The system media sessions, the same source as the volume flyout: any
    // app that publishes there is covered.
    static class Media
    {
        const string Control = "Windows.Media.Control";
        static Type managerType, sessionType, propertiesType, timelineType, playbackType, referenceType, streamType;
        static object manager;

        public static void Open()
        {
            managerType = WinRT.Type(Control + ".GlobalSystemMediaTransportControlsSessionManager", Control);
            sessionType = WinRT.Type(Control + ".GlobalSystemMediaTransportControlsSession", Control);
            propertiesType = WinRT.Type(Control + ".GlobalSystemMediaTransportControlsSessionMediaProperties", Control);
            timelineType = WinRT.Type(Control + ".GlobalSystemMediaTransportControlsSessionTimelineProperties", Control);
            playbackType = WinRT.Type(Control + ".GlobalSystemMediaTransportControlsSessionPlaybackInfo", Control);
            referenceType = WinRT.Type("Windows.Storage.Streams.IRandomAccessStreamReference", "Windows.Storage.Streams");
            streamType = WinRT.Type("Windows.Storage.Streams.IRandomAccessStreamWithContentType", "Windows.Storage.Streams");
            manager = WinRT.Await(managerType.GetMethod("RequestAsync").Invoke(null, null), managerType, 15000);
        }

        // Every session with something to show.
        public static List<Track> Read(Settings settings)
        {
            var list = new List<Track>();
            if (manager == null) Open();
            object current;
            IEnumerable sessions;
            try
            {
                current = WinRT.Call(manager, managerType, "GetCurrentSession");
                sessions = (IEnumerable)WinRT.Call(manager, managerType, "GetSessions");
            }
            catch (Exception)
            {
                // The manager can go dead under the program (Explorer restarted,
                // the PC woke from sleep): it is asked for anew next time,
                // instead of failing every second until a restart.
                manager = null;
                throw;
            }
            var currentApp = current == null ? null : (string)WinRT.Get(current, sessionType, "SourceAppUserModelId");
            foreach (var session in sessions)
            {
                try
                {
                    var properties = WinRT.Await(WinRT.Call(session, sessionType, "TryGetMediaPropertiesAsync"), propertiesType, 3000);
                    if (properties == null) continue;
                    var artist = (string)WinRT.Get(properties, propertiesType, "Artist") ?? "";
                    var title = (string)WinRT.Get(properties, propertiesType, "Title") ?? "";
                    if (artist == "" && title == "") continue;
                    var app = (string)WinRT.Get(session, sessionType, "SourceAppUserModelId") ?? "";
                    var playback = WinRT.Call(session, sessionType, "GetPlaybackInfo");
                    var track = new Track
                    {
                        App = app,
                        Source = Sources.Resolve(app, settings),
                        Artist = artist,
                        Title = title,
                        Album = (string)WinRT.Get(properties, propertiesType, "AlbumTitle") ?? "",
                        IsPlaying = WinRT.Get(playback, playbackType, "PlaybackStatus").ToString() == "Playing",
                        IsCurrent = app == currentApp,
                        Thumbnail = WinRT.Get(properties, propertiesType, "Thumbnail"),
                    };
                    if (track.Source.IsBrowser) track.Source = Site(app, artist, title) ?? track.Source;
                    ReadTimeline(session, track);
                    Repair(track);
                    list.Add(track);
                }
                catch (Exception)
                {
                    // A session can close between listing and reading; it is left out.
                }
            }
            return list;
        }

        // The site found for a track in a browser, kept for as long as it plays:
        // its tab need not stay in front, and the status does not flip back.
        static readonly Dictionary<string, SourceInfo> sites = new Dictionary<string, SourceInfo>();
        // Not found yet: the windows are looked through again at most every 5 s.
        static readonly Dictionary<string, DateTime> lookAgainAt = new Dictionary<string, DateTime>();

        static SourceInfo Site(string app, string artist, string title)
        {
            var key = app + "|" + artist + "|" + title;
            SourceInfo site;
            if (sites.TryGetValue(key, out site)) return site;
            DateTime at;
            if (lookAgainAt.TryGetValue(key, out at) && DateTime.Now < at) return null;
            site = Sources.FromWindows(title, artist);
            if (sites.Count > 100) sites.Clear();
            if (lookAgainAt.Count > 100) lookAgainAt.Clear();
            if (site != null) sites[key] = site;
            else lookAgainAt[key] = DateTime.Now.AddSeconds(5);
            return site;
        }

        static void ReadTimeline(object session, Track track)
        {
            try
            {
                var timeline = WinRT.Call(session, sessionType, "GetTimelineProperties");
                var start = (TimeSpan)WinRT.Get(timeline, timelineType, "StartTime");
                var end = (TimeSpan)WinRT.Get(timeline, timelineType, "EndTime");
                var position = (TimeSpan)WinRT.Get(timeline, timelineType, "Position");
                var updated = (DateTimeOffset)WinRT.Get(timeline, timelineType, "LastUpdatedTime");
                track.Position = (position - start).TotalSeconds;
                track.Duration = (end - start).TotalSeconds;
                track.UpdatedAt = updated.Year > 2000 ? updated.ToUnixTimeMilliseconds() : 0;
            }
            catch (Exception)
            {
                track.Position = track.Duration = 0;
                track.UpdatedAt = 0;
            }
        }

        // Tags sites add to titles: (Official Video), [HD] and the like.
        static readonly Regex junk = new Regex(
            @"\s*[(\[](?:official\s+)?(?:music\s+|lyric\s+)?(?:video|audio|visuali[sz]er|lyrics?)(?:\s+video)?[)\]]|\s*[(\[](?:hd|hq|4k|official|клип|премьера клипа|премьера)[)\]]",
            RegexOptions.IgnoreCase);
        static readonly Regex dashed = new Regex(@"^(.+?)\s+[-–—]\s+(.+)$");

        static void Repair(Track track)
        {
            var artist = track.Artist.Trim();
            var title = junk.Replace(track.Title, "").Trim();
            // Browser tabs often give no artist and "Artist - Title" as the title.
            if (artist == "")
            {
                var match = dashed.Match(title);
                if (match.Success)
                {
                    artist = match.Groups[1].Value;
                    title = match.Groups[2].Value;
                }
            }
            // "Artist - Topic" and "ArtistVEVO" channels post music, not videos:
            // "Listening to YouTube" for them, and the cover from iTunes.
            var isMusic = Regex.IsMatch(artist, @"\s*-\s*Topic$", RegexOptions.IgnoreCase) || Regex.IsMatch(artist, "VEVO$");
            if (isMusic && track.Source.IsVideo && track.Source.Name == "YouTube")
            {
                track.Source = new SourceInfo
                {
                    Name = track.Source.Name,
                    Search = track.Source.Search,
                    ClientId = track.Source.ClientId,
                    IsBrowser = track.Source.IsBrowser,
                };
            }
            artist = Regex.Replace(artist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
            artist = Regex.Replace(artist, "VEVO$", "");
            track.Artist = artist.Trim();
            track.Title = title;
        }

        // The cover the player handed to Windows; null when there is none.
        public static byte[] ReadThumbnail(Track track)
        {
            if (track.Thumbnail == null) return null;
            var stream = WinRT.Await(WinRT.Call(track.Thumbnail, referenceType, "OpenReadAsync"), streamType, 5000);
            using (var reader = WinRT.AsStream(stream))
            using (var memory = new MemoryStream())
            {
                reader.CopyTo(memory);
                return memory.Length > 1 ? memory.ToArray() : null;
            }
        }
    }
}
