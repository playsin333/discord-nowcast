using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
            Run("defaults", Defaults);
            Run("sources", SourcesByApp);
            Run("lines of a service", ServiceLines);
            Run("lines of other players", OtherLines);
            Run("album line", AlbumLine);
            Run("button", Button);
            Run("long titles", LongTitles);
            Run("titles from sites", Repair);
            Run("frozen timeline", FrozenTimeline);
            Run("cover without Spotify's logo", CoverCrop);
            Console.WriteLine(failed == 0 ? "All passed" : failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        // A patch once turned update checks off for every new install.
        static void Defaults()
        {
            var settings = new Settings();
            Check(settings.CheckUpdates, "update checks on");
            Check(!settings.HideAlbum, "album shown");
            Check(settings.Covers && settings.Buttons, "covers and button on");
            Check(settings.InList == "source", "inList is source");
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
            var settings = new Settings { Covers = false, Image = "https://example.com/a.png" };
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

        static Dictionary<string, object> Build(Track track, Settings settings)
        {
            settings.Covers = false;
            return Activity.Build(track, null, settings);
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
