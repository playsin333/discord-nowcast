using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Nowcast
{
    // The work itself, on its own thread: once a second reads what plays and,
    // when it changed, sends the status to Discord.
    class Engine
    {
        readonly Discord discord = new Discord();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly FileSystemWatcher watcher;
        Thread thread;
        volatile bool stopping;
        volatile bool reload;
        Settings settings;

        // What is in the status now: track key, or "none".
        string sentKey;
        long? sentStart;
        DateTime jumpedAt = DateTime.MinValue;
        // The last timeline the app reported, and the start worked out from it.
        string timelineKey;
        double timelinePosition;
        long timelineUpdated;
        bool timelinePlaying;
        long? timelineStart;
        bool? sentPlaying;
        bool sentCover;
        DateTime sentAt = DateTime.MinValue;
        DateTime nextConnect = DateTime.MinValue;
        DateTime? resendAt;
        string shownKey;
        // The track last seen and when, to see how long its status took.
        string seenKey;
        DateTime seenAt;

        public DateTime HiddenUntil { get; private set; }
        // For the tray: one line about the status and the sessions Windows reports.
        public string Status { get; private set; }
        public List<Track> Sessions { get; private set; }
        public event Action Changed;

        public Engine()
        {
            settings = Settings.Load();
            Status = Text.Starting;
            Sessions = new List<Track>();

            watcher = new FileSystemWatcher(Log.Folder, "config.json");
            watcher.Changed += delegate { reload = true; wake.Set(); };
            watcher.Created += delegate { reload = true; wake.Set(); };
            watcher.EnableRaisingEvents = true;

            // A cover that is done does not wait for the next round.
            Covers.Ready += delegate { wake.Set(); };
        }

        public Settings Settings
        {
            get { return settings; }
        }

        // For the diagnostics: connected, and with which application, or why not.
        public string DiscordState
        {
            get
            {
                if (discord.IsConnected) return "connected, application " + discord.ClientId;
                return "not connected" + (discord.LastMiss != null ? ": " + discord.LastMiss : "");
            }
        }

        public void Start()
        {
            // STA, like the PowerShell this grew from: the Start menu lookup is a shell object.
            thread = new Thread(Loop) { IsBackground = true, Name = "engine" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Stop()
        {
            stopping = true;
            wake.Set();
            if (thread != null) thread.Join(5000);
            watcher.Dispose();
        }

        public void Hide(TimeSpan time)
        {
            HiddenUntil = DateTime.Now + time;
            wake.Set();
        }

        public void Show()
        {
            HiddenUntil = DateTime.MinValue;
            wake.Set();
        }

        void Loop()
        {
            // Tried again every 30 s: Windows may let the program in later.
            string lastError = null;
            while (!stopping)
            {
                try
                {
                    Media.Open();
                    break;
                }
                catch (Exception e)
                {
                    var cause = Unwrap(e);
                    var error = Describe(cause);
                    if (error != lastError) Log.Write("Media sessions unavailable: " + error);
                    lastError = error;
                    // "Class not registered": the media sessions were cut out of
                    // this Windows (custom builds), no player can be read here.
                    // "The service cannot be started, it is disabled": a tweaking
                    // tool turned off a service the media sessions need.
                    Report(cause.HResult == unchecked((int)0x80040154) ? Text.MediaRemoved
                        : cause.HResult == unchecked((int)0x80070422) ? Text.MediaServiceOff
                        : Text.MediaUnavailable);
                    wake.WaitOne(30000);
                }
            }
            string lastTickError = null;
            while (!stopping)
            {
                try
                {
                    Tick();
                    lastTickError = null;
                }
                catch (Exception e)
                {
                    // The same error every second would bury the log: once until it changes.
                    var error = e.ToString();
                    if (error != lastTickError) Log.Write("Error: " + error);
                    lastTickError = error;
                }
                wake.WaitOne(500);
            }
            if (discord.IsConnected)
            {
                try
                {
                    discord.SetActivity(null);
                }
                catch (Exception)
                {
                }
            }
            discord.Dispose();
        }

        void Tick()
        {
            if (reload)
            {
                reload = false;
                settings = Settings.Load();
                sentKey = null;
                Log.Write("Settings reloaded");
            }

            var sessions = Media.Read(settings);
            var isHidden = DateTime.Now < HiddenUntil;
            var track = isHidden ? null : Pick(sessions);
            Sessions = sessions;

            if (discord.IsConnected && !discord.IsAlive)
            {
                Log.Write("Discord closed the connection");
                discord.Dispose();
                nextConnect = DateTime.Now;
            }
            // Each service has its own application, named after it: another
            // service means another connection. Closing one clears its status.
            var clientId = track != null ? settings.ClientIdFor(track.Source) : (discord.ClientId ?? settings.ClientId);
            if (discord.IsConnected && discord.ClientId != clientId)
            {
                Log.Write("Switching to the Discord application of " + track.Source.Name);
                discord.Dispose();
                nextConnect = DateTime.Now;
            }
            if (!discord.IsConnected && DateTime.Now >= nextConnect)
            {
                if (discord.Connect(clientId))
                {
                    sentKey = null;
                    // A Discord that is still starting up can drop the first
                    // status, so it goes once more a little later.
                    resendAt = DateTime.Now.AddSeconds(10);
                }
                else
                {
                    nextConnect = DateTime.Now.AddSeconds(3);
                }
            }

            Report(!discord.IsConnected ? (discord.IsDenied ? Text.DiscordElevated : Text.DiscordOff)
                : isHidden ? string.Format(Text.HiddenUntil, HiddenUntil)
                : track == null ? Text.NothingPlaying
                : Describe(track));
            if (!discord.IsConnected) return;

            var key = track == null ? "none" : track.Key;
            // The cover is looked for as soon as the track is seen, even when its
            // status has to wait for its turn. The status itself never waits for it.
            if (track != null && settings.Covers)
            {
                if (seenKey != key)
                {
                    seenKey = key;
                    seenAt = DateTime.Now;
                }
                Covers.Find(track, settings);
            }
            var start = track == null ? null : StartFor(track);
            bool? playing = track == null ? (bool?)null : track.IsPlaying;
            // A seek: the start of the track moved by more than 3 seconds.
            var jumped = start.HasValue && sentStart.HasValue && Math.Abs(start.Value - sentStart.Value) > 3000;
            // At most one seek in 30 s: a player whose timeline reads oddly must
            // not send the same track over and over.
            if (jumped && key == sentKey && (DateTime.Now - jumpedAt).TotalSeconds < 30) jumped = false;
            var isResend = resendAt.HasValue && DateTime.Now >= resendAt.Value;
            // A cover that was not there when the track started may have come since.
            var coverCame = key == sentKey && track != null && !sentCover && settings.Covers && Covers.IsDue(key);
            var changed = key != sentKey || playing != sentPlaying || jumped || isResend || coverCame;
            // Discord takes at most 5 updates per 20 seconds.
            if (!changed || (DateTime.Now - sentAt).TotalSeconds < 4) return;
            if (isResend) resendAt = null;

            try
            {
                if (track == null)
                {
                    discord.SetActivity(null);
                    Log.Write("Status cleared");
                }
                else
                {
                    var activity = Activity.Build(track, start, settings);
                    var hasCover = activity.ContainsKey("assets") && ((Dictionary<string, object>)activity["assets"]).ContainsKey("large_image");
                    // Looked again and still none: nothing new to send.
                    if (coverCame && !hasCover && key == sentKey && playing == sentPlaying && !jumped && !isResend) return;
                    sentCover = hasCover;
                    if (discord.SetActivity(activity) != null && Activity.UseLinks)
                    {
                        // An older Discord may not know the links on the cover and title.
                        Activity.UseLinks = false;
                        activity = Activity.Build(track, start, settings);
                        discord.SetActivity(activity);
                    }
                    Log.Write("Status: " + Describe(track) + (activity.ContainsKey("assets") ? "" : " (no cover)") +
                        (key != sentKey && seenKey == key ? " [" + (int)(DateTime.Now - seenAt).TotalMilliseconds + " ms after the track was seen]" : ""));
                }
                shownKey = track == null ? null : key;
                sentKey = key;
                if (jumped) jumpedAt = DateTime.Now;
                sentStart = start;
                sentPlaying = playing;
                sentAt = DateTime.Now;
            }
            catch (Exception e)
            {
                Log.Write("Lost Discord: " + e.Message);
                discord.Dispose();
                nextConnect = DateTime.Now.AddSeconds(5);
            }
        }

        // What to show: what plays, a track with a known length first (a video
        // feed in the browser usually has none), then the one already shown,
        // then the one Windows calls current. On pause: only with showPaused.
        Track Pick(List<Track> sessions)
        {
            var visible = sessions.Where(t => !IsHidden(t)).ToList();
            var pool = visible.Where(t => t.IsPlaying).ToList();
            if (pool.Count == 0 && settings.ShowPaused) pool = visible;
            if (pool.Count == 0) return null;
            var timed = pool.Where(t => t.Duration > 0).ToList();
            if (timed.Count > 0) pool = timed;
            return pool.FirstOrDefault(t => t.Key == shownKey)
                ?? pool.FirstOrDefault(t => t.IsCurrent)
                ?? pool[0];
        }

        bool IsHidden(Track track)
        {
            return settings.Hides(track.App, track.Source.Name);
        }

        // When the track started, Unix ms; null when the app tells neither a
        // length nor a position. Without a length but with a position (Telegram
        // may give one) the start is still known: Discord then shows the right
        // time for a track started midway, and a repeat is a seek back to 0.
        // Worked out again only when the app reports something new: the Yandex
        // Music app keeps one position while it plays, the start worked out
        // from the clock crept on a second a second, every 4 s the status went
        // again as a seek, and Discord stopped passing it on to anyone else.
        long? StartFor(Track track)
        {
            if (track.Duration <= 0 && track.Position <= 0 && track.UpdatedAt == 0) return null;
            var same = track.Key == timelineKey && track.IsPlaying == timelinePlaying;
            if (same && timelineStart.HasValue && track.Position == timelinePosition && track.UpdatedAt == timelineUpdated) return timelineStart;
            // A new position under the old time: the time is stale, the position is as of now.
            var updatedAt = same && track.UpdatedAt == timelineUpdated ? 0 : track.UpdatedAt;
            timelineKey = track.Key;
            timelinePlaying = track.IsPlaying;
            timelinePosition = track.Position;
            timelineUpdated = track.UpdatedAt;
            timelineStart = StartedAt(track, updatedAt);
            return timelineStart;
        }

        static long? StartedAt(Track track, long updatedAt)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var position = track.Position * 1000;
            // The position is as of updatedAt; it has played on since.
            if (track.IsPlaying && updatedAt > 0) position += Math.Max(0, now - updatedAt);
            if (track.Duration > 0) position = Math.Min(position, track.Duration * 1000);
            return now - (long)position;
        }

        public static string Describe(Track track)
        {
            var line = track.Artist != "" ? track.Artist + " — " + track.Title : track.Title;
            return line + " · " + track.Source.Name;
        }

        // The error underneath the task and reflection wrappers:
        // "One or more errors occurred" alone says nothing.
        static Exception Unwrap(Exception e)
        {
            while ((e is AggregateException || e is System.Reflection.TargetInvocationException) && e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e;
        }

        static string Describe(Exception e)
        {
            return e.GetType().Name + " 0x" + e.HResult.ToString("X8") + ": " + e.Message;
        }

        void Report(string status)
        {
            if (status == Status) return;
            Status = status;
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
