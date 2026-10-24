using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // The cover: the picture the player handed to Windows, the same one its own
    // flyout shows. Discord only shows pictures by URL, so it goes to a
    // temporary file host (Hosts). Only when the player gives no picture, or
    // no host takes it, the album art from the iTunes search is used, if the
    // same artist and title are found there.
    static class Covers
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        // Tracks without a cover yet: when to look again and how many looks are left.
        static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> triesLeft = new Dictionary<string, int>();
        // Tracks already looked up in iTunes: one search per track.
        static readonly HashSet<string> searched = new HashSet<string>();

        // What was last taken from each player: its track, a fingerprint of the
        // picture and the album. Right after a track changes Windows can still
        // hand over the previous track's picture; this is how that is noticed.
        class Seen
        {
            public string Key;
            public string Print;
            public string Album;
        }

        static readonly Dictionary<string, Seen> lastByApp = new Dictionary<string, Seen>();
        // Times a track's picture was the same as the previous track's.
        static readonly Dictionary<string, int> staleLooks = new Dictionary<string, int>();
        // Times a track had no picture from the player at all.
        static readonly Dictionary<string, int> noPicture = new Dictionary<string, int>();
        // Tracks the player has a picture for: their cover comes as soon as an upload works.
        static readonly HashSet<string> hasPicture = new HashSet<string>();
        // How many times, a second apart, to look for the player's picture before
        // turning to iTunes: browsers hand it over a few seconds after the track starts.
        const int PictureLooks = 5;

        // Raised when a lookup is done, so that the status goes out at once and
        // does not wait for the engine's next round.
        public static event Action Ready;

        static void Notify()
        {
            var handler = Ready;
            if (handler != null) handler();
        }
        // Lookups and uploads under way. They run in the background: a slow
        // host held the whole status back for up to a minute.
        static readonly Dictionary<string, Task<string>> pending = new Dictionary<string, Task<string>>();

        // The URL of the track's cover, or null when there is none yet; the
        // status goes without it and gets it when the lookup is done.
        // The settings say where from first: the player's picture or iTunes.
        public static string Find(Track track, Settings settings)
        {
            var iTunesFirst = settings.CoverSource == "itunes";
            var search = iTunesFirst || settings.CoverSearch;
            string url;
            var key = track.Key;
            if (cache.TryGetValue(key, out url)) return url;
            Task<string> job;
            if (pending.TryGetValue(key, out job))
            {
                if (!job.IsCompleted) return null;
                pending.Remove(key);
                if (job.Result != null)
                {
                    cache[key] = job.Result;
                    return job.Result;
                }
                // With the player's picture in hand the upload is tried again soon.
                Later(key, hasPicture.Contains(key) ? 2 : 10);
                return null;
            }
            if (!IsDue(key)) return null;
            if (cache.Count > 500)
            {
                cache.Clear();
                retryAt.Clear();
                triesLeft.Clear();
                searched.Clear();
                staleLooks.Clear();
                noPicture.Clear();
                hasPicture.Clear();
            }
            byte[] image;
            try
            {
                // On this thread: the thumbnail is a Windows Runtime object of the session.
                image = Media.ReadThumbnail(track);
            }
            catch (Exception e)
            {
                Log.Write("Cover not read: " + Message(e));
                image = null;
            }

            // iTunes first: one search, and the player's picture when the track is
            // not there. After that it goes on as below, without iTunes.
            if (iTunesFirst && searched.Add(key))
            {
                var picture = image != null && !IsStale(track, CoverImage.Fingerprint(image)) ? image : null;
                Start(key, () => FromITunes(track) ?? (picture != null ? FromPlayer(track, picture, false) : null));
                return null;
            }

            if (image != null)
            {
                var print = CoverImage.Fingerprint(image);
                if (IsStale(track, print))
                {
                    Later(key, 1);
                    return null;
                }
                // The cover will come as soon as an upload works: many tries, not ten.
                // On the first one iTunes may stand in when no host takes the picture.
                var isFirst = hasPicture.Add(key);
                if (isFirst) triesLeft[key] = 40;
                var orITunes = isFirst && search && searched.Add(key);
                lastByApp[track.App] = new Seen { Key = key, Print = print, Album = track.Album };
                Start(key, () => FromPlayer(track, image, orITunes));
                return null;
            }

            // The player has no picture, or has not handed it over yet. Look again
            // every second for a few seconds before turning to iTunes, whose
            // art can belong to another release.
            int looks;
            noPicture.TryGetValue(key, out looks);
            noPicture[key] = ++looks;
            if (search && looks >= PictureLooks && searched.Add(key))
            {
                Start(key, () => FromITunes(track));
                return null;
            }
            Later(key, looks < PictureLooks ? 1 : 3);
            return null;
        }

        // Runs a lookup in the background and tells the engine when it is done.
        static void Start(string key, Func<string> work)
        {
            var task = Task.Run(work);
            pending[key] = task;
            task.ContinueWith(t => Notify());
        }

        // In the background: the player's picture, made ready and uploaded.
        // orITunes: when no host takes it, the album art from iTunes instead
        // (a host that bans the IP or is down left the status with no cover).
        static string FromPlayer(Track track, byte[] image, bool orITunes)
        {
            var begin = DateTime.Now;
            try
            {
                var url = Hosts.Upload(CoverImage.Prepare(image));
                Log.Write("Cover from the player: " + Engine.Describe(track) + " " + url + " (" + (int)(DateTime.Now - begin).TotalMilliseconds + " ms)");
                return url;
            }
            catch (Exception e)
            {
                Log.Write("Cover not uploaded, will try again: " + Message(e));
                return orITunes ? FromITunes(track) : null;
            }
        }

        // In the background: the album art from iTunes.
        static string FromITunes(Track track)
        {
            var url = Search(track);
            if (url != null) Log.Write("Cover from iTunes: " + Engine.Describe(track) + " " + url);
            return url;
        }

        // Whether the picture is still the previous track's: the title has
        // changed but Windows has not replaced the picture yet. Tracks of one
        // album share a picture and are taken at once; for others the picture
        // is read again a few times before the two are believed to be the same.
        static bool IsStale(Track track, string print)
        {
            Seen before;
            if (!lastByApp.TryGetValue(track.App, out before) || before.Key == track.Key || before.Print != print) return false;
            if (track.Album != "" && string.Equals(before.Album, track.Album, StringComparison.OrdinalIgnoreCase)) return false;
            int looks;
            staleLooks.TryGetValue(track.Key, out looks);
            staleLooks[track.Key] = ++looks;
            if (looks > 4) return false;
            Log.Write("The picture of " + Engine.Describe(track) + " is still the previous track's, looking again");
            return true;
        }

        // The album art for the same artist and title in the iTunes search, or
        // null. A video's own frame says more than an album, so videos are left
        // to the player's picture. A search finds songs with the same title by
        // other artists, so both have to match.
        static string Search(Track track)
        {
            if (track.Source.IsVideo || track.Artist == "" || track.Title == "") return null;
            var artist = Norm(Regex.Split(track.Artist, @",|&|\bfeat\.?|\bft\.?|\bx\b", RegexOptions.IgnoreCase)[0]);
            var title = Norm(track.Title);
            if (artist.Length < 2 || title.Length < 2) return null;
            var term = Uri.EscapeDataString(track.Artist + " " + Regex.Replace(track.Title, @"\s*[(\[].*?[)\]]", ""));
            foreach (var country in Text.Ru ? new[] { "RU", "US" } : new[] { "US" })
            {
                try
                {
                    var text = Hosts.Http.GetStringAsync("https://itunes.apple.com/search?entity=song&limit=10&country=" + country + "&term=" + term).Result;
                    var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                    foreach (Dictionary<string, object> result in (ArrayList)reply["results"])
                    {
                        var foundArtist = Norm(result["artistName"] as string ?? "");
                        var foundTitle = Norm(result["trackName"] as string ?? "");
                        var art = result.ContainsKey("artworkUrl100") ? result["artworkUrl100"] as string : null;
                        if (art == null || !Alike(foundArtist, artist) || !Alike(foundTitle, title)) continue;
                        return art.Replace("100x100bb", "600x600bb");
                    }
                }
                catch (Exception e)
                {
                    Log.Write("iTunes search failed: " + Message(e));
                    return null;
                }
            }
            return null;
        }

        // Lowercase letters and digits only, without (feat. …), [Remix] and the like.
        static string Norm(string text)
        {
            text = Regex.Replace(text, @"[(\[].*?[)\]]", "");
            text = Regex.Replace(text, @"\s(feat|ft)\.?\s.*$", "", RegexOptions.IgnoreCase);
            return Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
        }

        // The same, or one starts the other: "Song" and "Song - Remastered 2011".
        static bool Alike(string found, string wanted)
        {
            if (found == wanted) return true;
            var shorter = Math.Min(found.Length, wanted.Length);
            return shorter >= 4 && (found.StartsWith(wanted) || wanted.StartsWith(found));
        }

        // Whether a track that had no cover may be looked at again.
        public static bool IsDue(string key)
        {
            DateTime at;
            int left;
            Task<string> job;
            if (pending.TryGetValue(key, out job)) return job.IsCompleted;
            if (!retryAt.TryGetValue(key, out at)) return true;
            return DateTime.Now >= at && triesLeft.TryGetValue(key, out left) && left > 0;
        }

        static void Later(string key, int seconds)
        {
            int left;
            if (!triesLeft.TryGetValue(key, out left)) left = 10;
            triesLeft[key] = left - 1;
            retryAt[key] = DateTime.Now.AddSeconds(seconds);
        }

        static string Message(Exception e)
        {
            return e.GetBaseException().Message;
        }
    }
}
