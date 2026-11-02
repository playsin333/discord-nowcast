using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace Nowcast
{
    // Checks of the logic that broke before, without Discord or a player:
    // tests\run.ps1 builds them with the program and runs them. A check that
    // fails prints FAIL and the exit code is 1.
    static class Tests
    {
        static int failed;

        static int Main()
        {
            // Tests must never overwrite the user's real fallback picture or log.
            var testFolder = Path.Combine(Path.GetTempPath(), "nowcast-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testFolder);
            typeof(Log).GetField("Folder").SetValue(null, testFolder);
            typeof(Log).GetField("FilePath").SetValue(null, Path.Combine(testFolder, "log.txt"));
            Run("defaults", Defaults);
            Run("cover setting from before", CoverSetting);
            Run("sources", SourcesByApp);
            Run("lines of a service", ServiceLines);
            Run("lines of other players", OtherLines);
            Run("album line", AlbumLine);
            Run("button", Button);
            Run("long titles", LongTitles);
            Run("titles from sites", Repair);
            Run("frozen timeline", FrozenTimeline);
            Run("no length, a position", NoLength);
            Run("fresh timestamps do not reset a frozen position", TimelineFreshStamps);
            Run("repeated seeks, pause, resume and repeat", TimelineTransitions);
            Run("late duration updates the Discord timer", TimelineLateDuration);
            Run("temporary missing timeline keeps its anchor", TimelineMissingReport);
            Run("cover without Spotify's logo", CoverCrop);
            Run("cover after the network is back", CoverAfterFailures);
            Run("cover link renewed before the host deletes it", CoverRenewed);
            Run("one-hour cover renewed at 48 minutes", HourCoverRenewed);
            Run("player's picture after iTunes", LatePicture);
            Run("iTunes error is not an answer", ITunesError);
            Run("one track's cover is not another's", CoverPerTrack);
            Run("one upload for an album", AlbumOnce);
            Run("cover settings changed", CoverSettingsChanged);
            Run("cover in place of the settings' picture", CoverOverImage);
            Run("picked picture when there is no cover", FallbackPicture);
            Run("lyrics", LyricsLines);
            Run("lyrics looked up again after an error", LyricsRetry);
            Run("VPS picture lifetimes and moderation rejection", HostFallback);
            Run("custom status reports, renews and retries", StatusReports);
            Run("tracked picture carries installation and slot", TrackedPicture);
            Run("own status", OwnStatus);
            Run("own status settings", OwnSettings);
            Run("own picture uploaded and renewed", OwnPicture);
            Run("own picture from a file", OwnPictureFile);
            Console.WriteLine(failed == 0 ? "All passed" : failed + " failed");
            Directory.Delete(testFolder, true);
            return failed == 0 ? 0 : 1;
        }

        // A patch once turned update checks off for every new install.
        static void Defaults()
        {
            var settings = new Settings();
            Check(settings.CheckUpdates, "update checks on");
            Check(!settings.HideAlbum, "album shown");
            Check(settings.CoverSource == "player" && settings.Buttons, "covers from the player and button on");
            Check(settings.InList == "source", "inList is source");
        }

        // "covers": false from before the choice is "none"; the new key wins.
        static void CoverSetting()
        {
            var apply = typeof(Settings).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance);
            var old = new Settings();
            apply.Invoke(old, new object[] { new Dictionary<string, object> { { "covers", false } } });
            Check(old.CoverSource == "none" && !old.Covers, "covers off becomes none");
            var kept = new Settings();
            apply.Invoke(kept, new object[] { new Dictionary<string, object> { { "covers", true } } });
            Check(kept.CoverSource == "player", "covers on becomes player");
            var chosen = new Settings();
            apply.Invoke(chosen, new object[] { new Dictionary<string, object> { { "coverSource", "itunes" }, { "covers", false } } });
            Check(chosen.CoverSource == "itunes", "the choice wins");
            var odd = new Settings();
            apply.Invoke(odd, new object[] { new Dictionary<string, object> { { "coverSource", "something" } } });
            Check(odd.CoverSource == "player", "an unknown value is the default");
        }

        static void SourcesByApp()
        {
            var settings = new Settings();
            var spotify = Sources.Resolve("Spotify.exe", settings);
            Check(spotify.Name == "Spotify" && spotify.ClientId != null, "Spotify");
            var firefox = Sources.Resolve("308046B0AF4A39CB", settings);
            Check(firefox.Name == "Firefox" && firefox.IsBrowser, "Firefox by its folder hash");
            Check(Sources.Resolve("F0DC299D809B9700", settings).Name == "Firefox", "Firefox elsewhere");
            Check(Sources.Resolve("Chrome", settings).IsBrowser, "Chrome is a browser");
            Check(Sources.Resolve("AIMP.exe", settings).ClientId == "1563516800287907893", "AIMP has its application");
            Check(Sources.Resolve("vlc.exe", settings).ClientId == null, "VLC uses the general one");
        }

        // A service named in the header: the lines are the title and the artist.
        static void ServiceLines()
        {
            var activity = Build(Track("SoundCloud.exe", "LiL PEEP", "driveway"), new Settings());
            Check((string)activity["details"] == "driveway", "title first");
            Check((string)activity["state"] == "LiL PEEP", "artist second");
            Check((int)activity["status_display_type"] == 0, "the application's name in the list");
            Check((int)activity["type"] == 2, "Listening to");
        }

        // A player without an application of its own keeps the source in a line.
        static void OtherLines()
        {
            var activity = Build(Track("vlc.exe", "Artist", "Song"), new Settings());
            Check((string)activity["details"] == "Artist — Song", "Artist — Title");
            Check((string)activity["state"] == "VLC", "the source second");
            Check((int)activity["status_display_type"] == 1, "the source in the list");
        }

        static void AlbumLine()
        {
            var settings = new Settings { Image = "https://example.com/a.png" };
            var track = Track("SoundCloud.exe", "A", "B");
            Check(!Assets(Build(track, settings)).ContainsKey("large_text"), "no album, no line: not the source again");
            track.Album = "Album";
            Check((string)Assets(Build(track, settings))["large_text"] == "Album", "the album");
            settings.HideAlbum = true;
            Check(!Assets(Build(track, settings)).ContainsKey("large_text"), "hidden album");
        }

        // A button to music.yandex.ru kept the status from everyone else.
        static void Button()
        {
            var yandex = Build(Track("Yandex.Music", "A", "B"), new Settings());
            var url = (string)ButtonOf(yandex)["url"];
            Check(url.StartsWith("https://www.youtube.com/"), "Yandex Music's button goes to YouTube");
            Check(url.IndexOf("yandex", StringComparison.OrdinalIgnoreCase) < 0, "no Yandex link in the button");
            var soundCloud = Build(Track("SoundCloud.exe", "A", "B"), new Settings());
            Check(((string)ButtonOf(soundCloud)["url"]).StartsWith("https://soundcloud.com/"), "SoundCloud's own search");
        }

        static void LongTitles()
        {
            // A Cyrillic letter is six characters escaped: over 512 the status is turned down.
            var russian = new string('ж', 300);
            var activity = Build(Track("SoundCloud.exe", russian, russian), new Settings());
            var url = (string)ButtonOf(activity)["url"];
            Check(url.Length <= 512, "button link within 512 (" + url.Length + ")");
            Check(((string)activity["details"]).Length <= 128, "title within 128");
            // An emoji cut in half makes the JSON invalid.
            var emoji = new string('a', 126) + "\U0001F525\U0001F525";
            var details = (string)Build(Track("SoundCloud.exe", "A", emoji), new Settings())["details"];
            Check(!char.IsHighSurrogate(details[details.Length - 2]), "no half of an emoji before the dots");
            Check(details.Length <= 128, "emoji title within 128");
        }

        // Browser tabs: "Artist - Title" and tags in the title.
        static void Repair()
        {
            var repair = typeof(Media).GetMethod("Repair", BindingFlags.NonPublic | BindingFlags.Static);
            var track = Track("Chrome", "", "Artist - Song (Official Video)");
            repair.Invoke(null, new object[] { track });
            Check(track.Artist == "Artist" && track.Title == "Song", "split and cleaned: " + track.Artist + " / " + track.Title);
            var topic = Track("Chrome", "Artist - Topic", "Song");
            topic.Source = new SourceInfo { Name = "YouTube", IsVideo = true };
            repair.Invoke(null, new object[] { topic });
            Check(topic.Artist == "Artist" && !topic.Source.IsVideo, "a Topic channel is music");
        }

        // The Yandex Music app keeps one position while it plays: the start of
        // the track must not creep on with the clock (it sent the status every 4 s).
        static void FrozenTimeline()
        {
            var engine = (Engine)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Engine));
            var startFor = typeof(Engine).GetMethod("StartFor", BindingFlags.NonPublic | BindingFlags.Instance);
            var track = Track("Yandex.Music", "A", "B");
            track.IsPlaying = true;
            track.Duration = 200;
            track.Position = 10;
            var first = (long?)startFor.Invoke(engine, new object[] { track });
            Thread.Sleep(1200);
            var second = (long?)startFor.Invoke(engine, new object[] { track });
            Check(first == second, "the same start a second later");
            track.Position = 60;
            var seek = (long?)startFor.Invoke(engine, new object[] { track });
            Check(Math.Abs((first - seek).Value) > 3000, "a new position is a seek");
        }

        // Telegram gives no length; when it gives a position, the time is right
        // from midway, a repeat starts it again, and Discord gets no end.
        static void NoLength()
        {
            var engine = (Engine)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Engine));
            var startFor = typeof(Engine).GetMethod("StartFor", BindingFlags.NonPublic | BindingFlags.Instance);
            var track = Track("Telegram.exe", "A", "B");
            Check(startFor.Invoke(engine, new object[] { track }) == null, "no length, no position: no time");
            track.Position = 95;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var start = (long?)startFor.Invoke(engine, new object[] { track });
            Check(start.HasValue && Math.Abs(now - start.Value - 95000) < 2000, "started 95 s ago");
            track.Position = 1;
            var again = (long?)startFor.Invoke(engine, new object[] { track });
            Check(again.Value - start.Value > 90000, "a repeat starts the time again");
            var timestamps = (Dictionary<string, object>)Activity.Build(track, again, new Settings { CoverSource = "none" })["timestamps"];
            Check(timestamps.ContainsKey("start") && !timestamps.ContainsKey("end"), "start only, no end");
        }

        static void WithTimeline(Action<Engine, Func<Track, long?>, Action<long>> test)
        {
            var saved = Engine.TimelineNow;
            var clock = 1000000000000L;
            Engine.TimelineNow = delegate { return clock; };
            var engine = (Engine)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Engine));
            var startFor = typeof(Engine).GetMethod("StartFor", BindingFlags.NonPublic | BindingFlags.Instance);
            try { test(engine, t => (long?)startFor.Invoke(engine, new object[] { t }), t => clock = t); }
            finally { Engine.TimelineNow = saved; }
        }

        static void RememberTiming(Engine engine, long? start, long duration)
        {
            typeof(Engine).GetField("sentStart", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(engine,start);
            typeof(Engine).GetField("sentDuration", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(engine,duration);
        }

        static void TimelineFreshStamps()
        {
            WithTimeline(delegate(Engine engine, Func<Track,long?> start, Action<long> at)
            {
                const long now=1000000000000L;
                var track=Track("SoundCloud", "Dean", "Song");
                track.IsPlaying=true; track.Duration=200; track.Position=10; track.UpdatedAt=now;
                var first=start(track);
                at(now+5000); track.UpdatedAt=now+5000;
                Check(start(track)==first,"a newer timestamp with the same position does not shift the clock");
                track.Position=15;
                Check(start(track)==first,"a coarse position catches up without a false seek");
                at(now+6000); track.Position=15.5; track.UpdatedAt=now+6000;
                Check(start(track)==first,"sub-second rounding does not jitter the clock");
            });
        }

        static void TimelineTransitions()
        {
            WithTimeline(delegate(Engine engine, Func<Track,long?> start, Action<long> at)
            {
                const long now=1000000000000L;
                var track=Track("SoundCloud", "Dean", "Song");
                track.IsPlaying=true; track.Duration=200; track.Position=10; track.UpdatedAt=now;
                RememberTiming(engine,start(track),200000);
                at(now+7000); track.Position=70; track.UpdatedAt=now+7000;
                var firstSeek=start(track);
                Check(engine.TimingChanged(track,firstSeek),"first seek is pending");
                RememberTiming(engine,firstSeek,200000);
                at(now+8000); track.Position=20; track.UpdatedAt=now+8000;
                var secondSeek=start(track);
                Check(engine.TimingChanged(track,secondSeek),"second seek within a second is also pending");
                at(now+10000); track.IsPlaying=false;
                var paused=start(track);
                Check(now+10000-paused.Value==22000,"pause freezes the predicted position when the raw sample is stale");
                Check(!Activity.Build(track,paused,new Settings()).ContainsKey("timestamps"),"paused status has no running timestamps");
                at(now+15000); track.UpdatedAt=now+15000;
                Check(start(track)==paused,"a pause does not count time forward");
                at(now+20000); track.IsPlaying=true;
                var resumed=start(track);
                Check(now+20000-resumed.Value==22000,"resume continues from the frozen position");
                at(now+21000); track.Position=0; track.UpdatedAt=now+21000;
                Check(start(track)==now+21000,"repeating the same track restarts at zero");
            });
        }

        static void TimelineLateDuration()
        {
            WithTimeline(delegate(Engine engine, Func<Track,long?> start, Action<long> at)
            {
                const long now=1000000000000L;
                var track=Track("SoundCloud", "Dean", "Song");
                track.IsPlaying=true; track.Position=30; track.UpdatedAt=now;
                var first=start(track);
                RememberTiming(engine,first,0);
                at(now+2000); track.Duration=200; track.UpdatedAt=now+2000;
                var current=start(track);
                Check(current==first && engine.TimingChanged(track,current),"duration arriving later queues a resend without moving the start");
                var times=(Dictionary<string,object>)Activity.Build(track,current,new Settings())["timestamps"];
                Check((long)times["end"]==first.Value+200000,"Discord receives the newly known end time");
                RememberTiming(engine,current,200000);
                Check(!engine.TimingChanged(track,current),"unchanged timing does not spam Discord");
                track.Duration=200.25;
                Check(!engine.TimingChanged(track,current),"small duration rounding does not trigger another resend");
            });
        }

        static void TimelineMissingReport()
        {
            WithTimeline(delegate(Engine engine, Func<Track,long?> start, Action<long> at)
            {
                var track=Track("SoundCloud", "Dean", "Song");
                track.IsPlaying=true;track.Duration=200;track.Position=10;
                var known=start(track);
                track.Duration=0;track.Position=0;track.UpdatedAt=0;
                Check(start(track)==known,"temporary missing data does not reset a known track");
                track.Title="Other song";
                Check(start(track)==null,"a different track never inherits the old clock");
            });
        }

        // Spotify's picture: the artwork with its logo below on a transparent strip.
        static void CoverCrop()
        {
            byte[] png;
            using (var picture = new Bitmap(640, 760, PixelFormat.Format32bppArgb))
            {
                using (var canvas = Graphics.FromImage(picture))
                {
                    canvas.Clear(Color.Transparent);
                    canvas.FillRectangle(Brushes.OrangeRed, 0, 0, 640, 640);
                    canvas.FillRectangle(Brushes.White, 200, 690, 240, 40);
                }
                using (var stream = new MemoryStream())
                {
                    picture.Save(stream, ImageFormat.Png);
                    png = stream.ToArray();
                }
            }
            var prepare = typeof(CoverImage).GetMethod("Prepare");
            var result = (byte[])prepare.Invoke(null, new object[] { png });
            using (var cover = new Bitmap(new MemoryStream(result)))
            {
                Check(cover.Width == cover.Height, "square, the strip cut off: " + cover.Width + "x" + cover.Height);
                Check(cover.Width <= 300, "scaled down");
            }
            Check(result[0] == 0xFF && result[1] == 0xD8, "a JPEG");
        }

        // Stand-ins for the covers: a clock that moves when told, a player's
        // picture, uploads that fail while the network is down, and iTunes.
        static DateTime clock;
        static byte[] picture;
        static bool networkDown;
        static int uploads;
        static int lookups;
        static Covers.Result iTunesAnswer;

        static void CoversWith(Action test)
        {
            var now = Covers.Now;
            var read = Covers.ReadPicture;
            var upload = Covers.Upload;
            var lookUp = Covers.LookUp;
            clock = new DateTime(2026, 10, 28, 12, 0, 0);
            picture = new byte[] { 1, 2, 3 };
            networkDown = false;
            uploads = 0;
            lookups = 0;
            iTunesAnswer = new Covers.Result { Answered = true };
            Covers.Reset();
            Covers.Now = delegate { return clock; };
            Covers.ReadPicture = delegate { return picture; };
            Covers.Upload = delegate(byte[] image)
            {
                uploads++;
                if (networkDown) throw new InvalidOperationException("no network");
                return new Hosts.Uploaded { Url = "https://host/" + image[0] + "-" + uploads, Keep = TimeSpan.FromHours(3) };
            };
            Covers.LookUp = delegate
            {
                lookups++;
                return iTunesAnswer;
            };
            var uploadOwn = Covers.UploadOwn;
            Covers.UploadOwn = delegate(byte[] image, string slot) { return Covers.Upload(image); };
            try
            {
                test();
            }
            finally
            {
                Covers.Now = now;
                Covers.ReadPicture = read;
                Covers.Upload = upload;
                Covers.LookUp = lookUp;
                Covers.UploadOwn = uploadOwn;
                Covers.Reset();
            }
        }

        static Covers.Result Found(string url)
        {
            var entry = new Covers.Entry { Url = url, FromITunes = true, Renew = DateTime.MaxValue, Gone = DateTime.MaxValue };
            return new Covers.Result { Entry = entry, Answered = true };
        }

        // Looks for the cover, lets the lookups it started finish, and looks again.
        static string Cover(Track track, Settings settings, int seconds)
        {
            clock = clock.AddSeconds(seconds);
            Covers.Find(track, settings);
            var pending = (System.Collections.IDictionary)typeof(Covers).GetField("pending", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (System.Threading.Tasks.Task job in new List<object>(pending.Values.Cast<object>()).Cast<System.Threading.Tasks.Task>()) job.Wait();
            return Covers.Find(track, settings);
        }

        // A track whose upload failed for longer than a minute had no cover until the program restarted.
        static void CoverAfterFailures()
        {
            CoversWith(delegate
            {
                var track = Track("SoundCloud.exe", "A", "B");
                var settings = new Settings();
                networkDown = true;
                Check(Cover(track, settings, 0) == null, "no cover while the network is down");
                var tried = uploads;
                Check(Cover(track, settings, 1) == null && uploads == tried, "not tried again at once");
                for (var i = 0; i < 20; i++) Cover(track, settings, 61);
                Check(uploads > 15 && uploads < 30, "tried about once a minute: " + uploads);
                networkDown = false;
                Check(Cover(track, settings, 61) != null, "the cover comes when the network is back");
            });
        }

        // uguu.se deletes a picture after three hours; the link kept being sent.
        static void CoverRenewed()
        {
            CoversWith(delegate
            {
                var track = Track("SoundCloud.exe", "A", "B");
                var settings = new Settings();
                var first = Cover(track, settings, 0);
                Check(first != null, "a cover");
                Check(Cover(track, settings, 3600) == first, "the same link an hour later");
                var renewed = Cover(track, settings, 5400);
                Check(renewed != null && renewed != first, "a new link before the host deletes the old one");
                picture = null;
                Check(Cover(track, settings, 3 * 3600) == null, "a deleted link is not sent");
            });
        }

        static void HourCoverRenewed()
        {
            CoversWith(delegate
            {
                Covers.Upload = delegate(byte[] image)
                {
                    uploads++;
                    return new Hosts.Uploaded { Url = "https://host/hour-" + uploads, Keep = TimeSpan.FromHours(1) };
                };
                var track = Track("SoundCloud.exe", "A", "Hour");
                var settings = new Settings();
                var first = Cover(track, settings, 0);
                Check(Cover(track, settings, 47 * 60) == first, "usable before renewal");
                Check(Cover(track, settings, 60) != first && uploads == 2, "renewed after 48 minutes");
            });
        }

        // A browser hands its picture over late: iTunes stands in, then the player's picture takes its place.
        static void LatePicture()
        {
            CoversWith(delegate
            {
                var track = Track("Chrome", "A", "B");
                var settings = new Settings();
                picture = null;
                iTunesAnswer = Found("https://itunes/1");
                string cover = null;
                for (var i = 0; i < 6 && cover == null; i++) cover = Cover(track, settings, 1);
                Check(cover == "https://itunes/1", "iTunes stands in: " + cover);
                picture = new byte[] { 7 };
                Check(Cover(track, settings, 5).StartsWith("https://host/7"), "the player's picture in its place");

                Covers.Reset();
                var fromITunes = new Settings { CoverSource = "itunes" };
                picture = new byte[] { 7 };
                Check(Cover(track, fromITunes, 0) == "https://itunes/1", "from iTunes first");
                Check(Cover(track, fromITunes, 60) == "https://itunes/1", "and it stays");
            });
        }

        // iTunes not reached was taken for "not found" and never asked again.
        static void ITunesError()
        {
            CoversWith(delegate
            {
                var track = Track("Chrome", "A", "B");
                var settings = new Settings();
                picture = null;
                iTunesAnswer = new Covers.Result { Failed = true };
                for (var i = 0; i < 6; i++) Cover(track, settings, 1);
                Check(lookups == 1, "asked once: " + lookups);
                iTunesAnswer = Found("https://itunes/2");
                Check(Cover(track, settings, 10) == "https://itunes/2", "asked again after the error");
                Check(lookups == 2, "twice in all");

                Covers.Reset();
                lookups = 0;
                iTunesAnswer = new Covers.Result { Answered = true };
                for (var i = 0; i < 20; i++) Cover(track, settings, 5);
                Check(lookups == 1, "not found is asked once: " + lookups);
            });
        }

        static void CoverPerTrack()
        {
            CoversWith(delegate
            {
                var first = Track("SoundCloud.exe", "A", "One");
                var second = Track("SoundCloud.exe", "A", "Two");
                var settings = new Settings();
                Covers.ReadPicture = delegate(Track track) { return new byte[] { (byte)(track.Title == "One" ? 1 : 2) }; };
                Covers.Find(first, settings);
                var cover = Cover(second, settings, 1);
                Check(cover != null && cover.StartsWith("https://host/2"), "the second track's own: " + cover);
                Check(Cover(first, settings, 1).StartsWith("https://host/1"), "and the first's its own");
            });
        }

        static void AlbumOnce()
        {
            CoversWith(delegate
            {
                var first = Track("SoundCloud.exe", "A", "One");
                var second = Track("SoundCloud.exe", "A", "Two");
                first.Album = second.Album = "Album";
                var settings = new Settings();
                var cover = Cover(first, settings, 0);
                Check(Cover(second, settings, 1) == cover, "the same link");
                Check(uploads == 1, "uploaded once: " + uploads);
            });
        }

        static void CoverSettingsChanged()
        {
            CoversWith(delegate
            {
                var track = Track("SoundCloud.exe", "A", "B");
                iTunesAnswer = Found("https://itunes/3");
                Check(Cover(track, new Settings(), 0).StartsWith("https://host/"), "from the player");
                Check(Cover(track, new Settings(), 1).StartsWith("https://host/") && uploads == 1, "settings read again: nothing uploaded again");
                Check(Cover(track, new Settings { CoverSource = "itunes" }, 1) == "https://itunes/3", "from iTunes once chosen");
            });
        }

        // A picture of one's own in the settings kept the real cover out of the status.
        static void CoverOverImage()
        {
            CoversWith(delegate
            {
                var track = Track("SoundCloud.exe", "A", "B");
                var settings = new Settings { Image = "https://example.com/own.png" };
                networkDown = true;
                Check(Activity.Image(track, settings) == "https://example.com/own.png", "the own picture first");
                Cover(track, settings, 0);
                networkDown = false;
                Cover(track, settings, 10);
                Check(Activity.Image(track, settings).StartsWith("https://host/"), "then the cover");
            });
        }

        // The picture picked for a track with no cover, until the cover comes.
        static void FallbackPicture()
        {
            CoversWith(delegate
            {
                var path = Settings.FallbackPicturePath;
                try
                {
                    Directory.CreateDirectory(Log.Folder);
                    File.WriteAllBytes(path, new byte[] { 9 });
                    var track = Track("SoundCloud.exe", "A", "B");
                    var settings = new Settings { Image = "https://example.com/own.png" };
                    picture = null;
                    Activity.Image(track, settings);
                    Settle();
                    var image = Activity.Image(track, settings);
                    Check(image != null && image.StartsWith("https://host/9"), "the picked picture, before the settings' link: " + image);
                    picture = new byte[] { 4 };
                    Cover(track, settings, 10);
                    Check(Activity.Image(track, settings).StartsWith("https://host/4"), "then the cover");
                    var always = new Settings { CoverSource = "picture", Image = "https://example.com/own.png" };
                    Check(Activity.Image(track, always).StartsWith("https://host/9"), "\"picture\": the picked one, not the cover");
                    Check(Activity.Image(track, new Settings { CoverSource = "none", Image = "https://example.com/own.png" }) == null, "\"none\": nothing at all");
                    File.Delete(path);
                    Check(Activity.Image(Track("SoundCloud.exe", "C", "D"), always) == "https://example.com/own.png", "removed: the settings' link");
                }
                finally
                {
                    File.Delete(path);
                }
            });
        }

        // LRC parsed, the line of the moment picked, and where it goes in the status.
        static void LyricsLines()
        {
            var lines = Lyrics.Parse("[ar:Kino]\n[00:05.00]Тёплое место\n[00:09.50][01:00.00]Группа крови\n[00:12.00]\n");
            Check(lines.Count == 4, "four timed lines: " + lines.Count);
            Check(Lyrics.Line(lines, 1000) == null, "nothing before the first");
            Check(Lyrics.Line(lines, 6000) == "Тёплое место", "the first");
            Check(Lyrics.Line(lines, 10000) == "Группа крови", "the second");
            Check(Lyrics.Line(lines, 13000) == "♪", "a pause");
            Check(Lyrics.Line(lines, 61000) == "Группа крови", "a repeated line");
            var track = Track("SoundCloud.exe", "Kino", "Группа крови");
            var settings = new Settings { Image = "https://example.com/a.png" };
            track.Album = "Album";
            Check((string)Assets(Build(track, settings, "Группа крови"))["large_text"] == "Группа крови", "under the lines, in place of the album");
            settings.Image = "";
            Check((string)Build(track, settings, "Группа крови")["state"] == "Группа крови", "no picture: in place of the second line");
            track.IsPlaying = false;
            Check((string)Build(track, settings, "Группа крови")["state"] != "Группа крови", "not on pause");
        }

        static void LyricsRetry()
        {
            var now = Lyrics.Now;
            var lookUp = Lyrics.LookUp;
            var clock = new DateTime(2026, 10, 28, 12, 0, 0);
            var lookups = 0;
            var down = true;
            Lyrics.Reset();
            Lyrics.Now = delegate { return clock; };
            Lyrics.LookUp = delegate
            {
                lookups++;
                return down ? null : Lyrics.Parse("[00:01.00]line");
            };
            try
            {
                var track = Track("SoundCloud.exe", "A", "B");
                Func<List<LyricLine>> find = delegate
                {
                    Lyrics.Find(track);
                    var pending = (System.Collections.IDictionary)typeof(Lyrics).GetField("pending", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
                    foreach (System.Threading.Tasks.Task job in pending.Values) job.Wait();
                    return Lyrics.Find(track);
                };
                Check(find() == null && lookups == 1, "none while the site is down");
                Check(find() == null && lookups == 1, "not asked again at once");
                down = false;
                clock = clock.AddSeconds(3);
                var lines = find();
                Check(lines != null && lines.Count == 1 && lookups == 2, "found when the site is back");
                Check(find() == lines && lookups == 2, "asked once");
            }
            finally
            {
                Lyrics.Now = now;
                Lyrics.LookUp = lookUp;
                Lyrics.Reset();
            }
        }

        class FakeHostHandler : System.Net.Http.HttpMessageHandler
        {
            public int PrimaryStatus = 503;
            public readonly List<string> Requests = new List<string>();
            public string Installation, Slot;
            protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellation)
            {
                var origin = request.RequestUri.GetLeftPart(UriPartial.Authority);
                Requests.Add(origin);
                if (request.Headers.Contains("X-Nowcast-Installation")) Installation = request.Headers.GetValues("X-Nowcast-Installation").First();
                if (request.Headers.Contains("X-Nowcast-Slot")) Slot = request.Headers.GetValues("X-Nowcast-Slot").First();
                var status = PrimaryStatus;
                var response = new System.Net.Http.HttpResponseMessage((System.Net.HttpStatusCode)status);
                response.Content = new System.Net.Http.StringContent(status == 200 ? origin + "/" + new string('a', 40) + ".png" : (status == 403 ? "Banned" : "unavailable"));
                return System.Threading.Tasks.Task.FromResult(response);
            }
        }

        static void TrackedPicture()
        {
            var saved = Hosts.UploadHttp;
            var path = StatusReporter.IdentityPath;
            var folder = Path.Combine(Path.GetTempPath(), "nowcast-tracked-" + Guid.NewGuid().ToString("N"));
            try
            {
                StatusReporter.IdentityPath = Path.Combine(folder, "installation");
                var fake = new FakeHostHandler { PrimaryStatus = 200 };
                Hosts.UploadHttp = new System.Net.Http.HttpClient(fake);
                Hosts.UploadOwn(new byte[] { 0x89, 0x50 }, "status");
                Check(fake.Requests.Count == 1 && fake.Requests[0] == Hosts.Reserve, "tracked metadata reaches VPS directly");
                Check(fake.Slot == "status" && fake.Installation == StatusReporter.Installation(), "same persistent token on picture and status");
            }
            finally
            {
                Hosts.UploadHttp.Dispose(); Hosts.UploadHttp = saved;
                StatusReporter.IdentityPath = path;
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        static void HostFallback()
        {
            var saved = Hosts.UploadHttp;
            try
            {
                var fake = new FakeHostHandler { PrimaryStatus = 200 };
                Hosts.UploadHttp = new System.Net.Http.HttpClient(fake);
                var own = Hosts.UploadOwn(new byte[] { 0x89, 0x50 });
                var player = Hosts.Upload(new byte[] { 1 });
                Check(own.Keep == TimeSpan.FromHours(6) && player.Keep == TimeSpan.FromHours(1), "picture lifetimes preserved");
                Check(fake.Requests.Count == 2 && fake.Requests.All(origin => origin == Hosts.Reserve), "all pictures go directly to VPS");
                foreach (var status in new[] { 403, 451, 413, 415 })
                {
                    fake.PrimaryStatus = status;
                    fake.Requests.Clear();
                    var rejected = false;
                    try { Hosts.UploadOwn(new byte[] { 1 }); } catch (Hosts.Rejected) { rejected = true; }
                    Check(rejected && fake.Requests.Count == 1, "moderation rejection preserved: " + status);
                }
                fake.PrimaryStatus = 503;
                fake.Requests.Clear();
                var failed = false;
                try { Hosts.Upload(new byte[] { 1 }); } catch (InvalidOperationException) { failed = true; }
                Check(failed && fake.Requests.Count == 1 && fake.Requests[0] == Hosts.Reserve, "VPS outage never sends images to Cloudflare");
                fake.PrimaryStatus = 200;
                Check(Hosts.Upload(new byte[] { 1 }).Url.StartsWith(Hosts.Reserve + "/"), "VPS is retried after recovery");
            }
            finally
            {
                Hosts.UploadHttp.Dispose();
                Hosts.UploadHttp = saved;
            }
        }

        // "Listening to nowcast": two lines, the time from the start, the picture, the server on a click.
        static void StatusReports()
        {
            var identityPath = StatusReporter.IdentityPath;
            var identityFolder = Path.Combine(Path.GetTempPath(), "nowcast-identity-" + Guid.NewGuid().ToString("N"));
            StatusReporter.IdentityPath = Path.Combine(identityFolder, "installation");
            var originalSend = StatusReporter.Send;
            var originalNow = StatusReporter.Now;
            var now = DateTime.UtcNow;
            var bodies = new List<string>();
            Action wait = delegate {
                var busy = typeof(StatusReporter).GetField("busy", BindingFlags.NonPublic | BindingFlags.Static);
                for (var i = 0; i < 1000 && (bool)busy.GetValue(null); i++) Thread.Sleep(5);
                Check(!(bool)busy.GetValue(null), "report completed");
            };
            try
            {
                StatusReporter.Now = delegate { return now; };
                StatusReporter.Send = delegate(string body) { bodies.Add(body); };
                StatusReporter.Report("Привет", "Вторая строка", "listening", null); wait();
                Check(bodies.Count == 1 && bodies[0].Contains("Привет"), "text without picture sent");
                var payload = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,string>>(bodies[0]);
                Check(payload["client"].Length == 64 && payload["client"] == StatusReporter.Installation(), "installation persists across reports and reads");
                StatusReporter.Report("Привет", "Вторая строка", "listening", null); wait();
                Check(bodies.Count == 1, "unchanged text is not sent every tick");
                now = now.AddHours(1);
                StatusReporter.Report("Привет", "Вторая строка", "listening", null); wait();
                Check(bodies.Count == 2, "active status renewed");
                var pic = new string('a', 40) + ".png";
                StatusReporter.Report("Привет", "Вторая строка", "listening", Hosts.Own + "/" + pic); wait();
                Check(bodies.Count == 3 && bodies[2].Contains(pic), "late picture attaches to text");
                StatusReporter.Send = delegate(string body) { throw new InvalidOperationException("offline"); };
                StatusReporter.Report("Changed", "", "playing", null); wait();
                StatusReporter.Send = delegate(string body) { bodies.Add(body); };
                StatusReporter.Report("Changed", "", "playing", null); wait();
                Check(bodies.Count == 3, "failure backs off");
                now = now.AddMinutes(1);
                StatusReporter.Report("Changed", "", "playing", null); wait();
                Check(bodies.Count == 4, "failed report retried");
            }
            finally
            {
                StatusReporter.Send = originalSend; StatusReporter.Now = originalNow;
                StatusReporter.IdentityPath = identityPath;
                if (Directory.Exists(identityFolder)) Directory.Delete(identityFolder, true);
            }
        }

        static void OwnStatus()
        {
            var activity = Activity.Own("listening", "Делаю бит", "в FL Studio", 1000, "https://host/own");
            Check((int)activity["type"] == 2 && (int)activity["status_display_type"] == 0, "Listening, the application's name");
            Check((int)Activity.Own("watching", "Ok", "", 1000, null)["type"] == 3, "Watching");
            Check((int)Activity.Own("playing", "Ok", "", 1000, null)["type"] == 0, "Playing");
            Check((string)activity["details"] == "Делаю бит" && (string)activity["state"] == "в FL Studio", "the two lines");
            Check(!Activity.Own("listening", "Ok", " ", 1000, null).ContainsKey("state"), "no second line, none sent");
            Check((long)((Dictionary<string, object>)activity["timestamps"])["start"] == 1000, "the start");
            var assets = Assets(activity);
            Check((string)assets["large_image"] == "https://host/own", "the picture");
            Check((string)assets["large_url"] == Program.DiscordInvite && (string)activity["details_url"] == Program.DiscordInvite, "a click opens the server");
            Check(!activity.ContainsKey("buttons"), "no button");
            Check(!Activity.Own("listening", "Ok", "", 1000, null).ContainsKey("assets"), "no picture, no assets");
        }

        static void OwnSettings()
        {
            var apply = typeof(Settings).GetMethod("Apply", BindingFlags.NonPublic | BindingFlags.Instance);
            var settings = new Settings();
            Check(!settings.Own && !settings.ShowsOwn && settings.OwnType == "listening", "off at first, listening");
            apply.Invoke(settings, new object[] { new Dictionary<string, object> { { "ownType", "dancing" } } });
            Check(settings.OwnType == "listening", "an unknown type is the default");
            apply.Invoke(settings, new object[] { new Dictionary<string, object> { { "ownType", "playing" }, { "ownLine2", "x" } } });
            Check(settings.OwnType == "playing" && settings.OwnLine2 == "x", "type and line 2 read");
            apply.Invoke(settings, new object[] { new Dictionary<string, object> { { "own", true }, { "ownTitle", " " }, { "ownMinutes", 999999 } } });
            Check(settings.Own && !settings.ShowsOwn, "no title, nothing shown");
            Check(settings.OwnMinutes == 100 * 60, "at most 100 hours: " + settings.OwnMinutes);
            settings.OwnTitle = "Бит";
            Check(settings.ShowsOwn, "shown with a title");
            Check(settings.OwnClientIdFor() == Settings.OwnClientId, "nowcast's application");
            settings.ClientId = "123";
            Check(settings.OwnClientIdFor() == "123", "one's own application wins");
            foreach (var bad in new[] { "заходи на casino-x.com", "t.me/bonus", "bonus . com", "www.site", "https://x", "Лучшее КАЗИНО", "1xbet бонус", "Промокод 777", "porn", "18+ видео", "сайт.рф" })
            {
                settings.OwnTitle = bad;
                Check(!settings.ShowsOwn, "not shown: " + bad);
            }
            foreach (var good in new[] { "Делаю бит", "Слушаю Dean Martin", "v1.2 вышла", "т.е. всё", "Ст. Петербург", "Mr. Smith", "ft.Drake", "feat. Rihanna" })
            {
                settings.OwnTitle = good;
                Check(settings.ShowsOwn, "shown: " + good);
            }
            settings.OwnTitle = "Ок";
            settings.OwnLine2 = "ставки тут";
            Check(!settings.ShowsOwn, "the second line too");
        }

        static void OwnPicture()
        {
            CoversWith(delegate
            {
                var path = Path.Combine(Path.GetTempPath(), "nowcast-own-test-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Check(Covers.FindFile(path) == null, "no file, no picture");
                    File.WriteAllBytes(path, new byte[] { 5 });
                    networkDown = true;
                    Covers.FindFile(path);
                    Settle();
                    Check(Covers.FindFile(path) == null, "not uploaded while the network is down");
                    networkDown = false;
                    clock = clock.AddSeconds(3);
                    Covers.FindFile(path);
                    Settle();
                    var first = Covers.FindFile(path);
                    Check(first != null && first.StartsWith("https://host/5"), "uploaded: " + first);
                    clock = clock.AddHours(2.5);
                    Check(Covers.FindFile(path) == first, "the old link while it is renewed");
                    Settle();
                    var renewed = Covers.FindFile(path);
                    Check(renewed != null && renewed != first, "renewed");
                    File.WriteAllBytes(path, new byte[] { 6, 6 });
                    Covers.FindFile(path);
                    Settle();
                    Check(Covers.FindFile(path).StartsWith("https://host/6"), "another picture picked");
                    File.Delete(path);
                    Check(Covers.FindFile(path) == null, "removed");
                }
                finally
                {
                    File.Delete(path);
                }
            });
        }

        // Any picture becomes one a host takes: JPEG, or PNG with transparency, at most 300 px.
        static void OwnPictureFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "nowcast-own-test-" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                using (var picture = new Bitmap(800, 400, PixelFormat.Format24bppRgb)) picture.Save(path, ImageFormat.Bmp);
                var bytes = CoverImage.FromFile(path);
                Check(bytes[0] == 0xFF && bytes[1] == 0xD8, "a JPEG from a BMP");
                using (var made = new Bitmap(new MemoryStream(bytes))) Check(made.Width == 300 && made.Height == 150, "scaled down: " + made.Width + "x" + made.Height);
                using (var picture = new Bitmap(100, 100, PixelFormat.Format32bppArgb)) picture.Save(path, ImageFormat.Png);
                bytes = CoverImage.FromFile(path);
                Check(bytes[0] == 0x89 && bytes[1] == 0x50, "a PNG keeps its transparency");
            }
            finally
            {
                File.Delete(path);
            }
        }

        // Waits for the uploads of pictures from files.
        static void Settle()
        {
            var files = (System.Collections.IDictionary)typeof(Covers).GetField("files", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            foreach (var state in files.Values)
            {
                var job = (System.Threading.Tasks.Task)state.GetType().GetField("Job").GetValue(state);
                if (job != null) job.Wait();
            }
        }

        static Track Track(string app, string artist, string title)
        {
            return new Track
            {
                App = app,
                Source = Sources.Resolve(app, new Settings()),
                Artist = artist,
                Title = title,
                Album = "",
                IsPlaying = true,
            };
        }

        static Dictionary<string, object> Build(Track track, Settings settings, string lyric = null)
        {
            // No lookups: the settings' link stands for the picture.
            settings.CoverSource = "picture";
            return Activity.Build(track, null, settings, lyric);
        }

        static Dictionary<string, object> Assets(Dictionary<string, object> activity)
        {
            return activity.ContainsKey("assets") ? (Dictionary<string, object>)activity["assets"] : new Dictionary<string, object>();
        }

        static Dictionary<string, object> ButtonOf(Dictionary<string, object> activity)
        {
            return ((Dictionary<string, object>[])activity["buttons"])[0];
        }

        static void Run(string name, Action test)
        {
            try
            {
                test();
                Console.WriteLine("ok    " + name);
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine("FAIL  " + name + ": " + (e.InnerException ?? e).Message);
            }
        }

        static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception(what);
        }
    }
}
