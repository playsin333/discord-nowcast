using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // The cover: the picture the player handed to Windows, the same one its own
    // flyout shows. Discord only shows pictures by URL, so it goes to a host
    // (Hosts). Only when the player gives no picture, or no host takes it,
    // the album art from the iTunes search is used, if the same artist and
    // title are found there; the player's picture replaces it when it comes.
    static class Covers
    {
        // A cover in hand: its link, where it came from, when to upload the
        // picture again (the host will delete it) and when the link is gone.
        public class Entry
        {
            public string Url;
            public bool FromITunes;
            public DateTime Renew;
            public DateTime Gone;
        }

        // What a lookup in the background brought: a cover, or that it failed
        // and is to be tried again; Answered: iTunes gave its answer, found or not.
        public class Result
        {
            public Entry Entry;
            public string Print;
            public bool Failed;
            public bool Answered;
        }

        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        // Uploads by the picture's fingerprint: a cover shared by an album's tracks goes up once.
        static readonly Dictionary<string, Entry> byPrint = new Dictionary<string, Entry>();
        // When a track may be looked at again, and its failures in a row.
        static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        // Tracks iTunes has answered for: one search per track. A search that
        // failed is not an answer and is tried again.
        static readonly HashSet<string> searched = new HashSet<string>();
        // iTunes turned the searches down (403, 429): none until then.
        static DateTime iTunesPausedUntil;
        static readonly object iTunesBook = new object();

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
        // How many times, a second apart, to look for the player's picture before
        // turning to iTunes: browsers hand it over a few seconds after the track starts.
        const int PictureLooks = 5;
        // After that, and while an iTunes cover stands in, the player is asked this often.
        const int LookEvery = 5;
        // The track looked for last, and the settings the covers in hand were found with.
        static string current;
        static string mode;

        // Stand-ins for the tests: the clock, the player's picture, the upload and iTunes.
        public static Func<DateTime> Now = delegate { return DateTime.Now; };
        public static Func<Track, byte[]> ReadPicture = Media.ReadThumbnail;
        public static Func<byte[], Hosts.Uploaded> Upload = delegate(byte[] image) { return Hosts.Upload(CoverImage.Prepare(image)); };
        public static Func<Track, Result> LookUp = FromITunes;
        public static Func<byte[], Hosts.Uploaded> UploadOwn = delegate(byte[] image) { return Hosts.UploadOwn(CoverImage.Prepare(image)); };

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
        static readonly Dictionary<string, Task<Result>> pending = new Dictionary<string, Task<Result>>();

        // The URL of the track's cover, or null when there is none yet; the
        // status goes without it and gets it when the lookup is done.
        // The settings say where from first: the player's picture or iTunes.
        public static string Find(Track track, Settings settings)
        {
            var iTunesFirst = settings.CoverSource == "itunes";
            var search = iTunesFirst || settings.CoverSearch;
            var key = track.Key;
            var now = Now();

            // Covers found with other settings are not what these ask for.
            var asked = settings.CoverSource + "|" + settings.CoverSearch;
            if (asked != mode)
            {
                Reset();
                mode = asked;
            }
            if (cache.Count + retryAt.Count > 1000) Reset();
            // A track played again gets its tries back: the network may be up by now.
            if (key != current)
            {
                current = key;
                retryAt.Remove(key);
                failures.Remove(key);
                noPicture.Remove(key);
            }

            Task<Result> job;
            if (pending.TryGetValue(key, out job))
            {
                if (!job.IsCompleted) return Usable(key, now);
                pending.Remove(key);
                Take(key, job.Result, iTunesFirst, now);
            }

            Entry entry;
            cache.TryGetValue(key, out entry);
            if (entry != null && now >= entry.Gone)
            {
                cache.Remove(key);
                entry = null;
            }
            // In hand and good: an iTunes cover in "from the player" only stands in.
            var better = entry != null && (now >= entry.Renew || (entry.FromITunes && !iTunesFirst));
            if (entry != null && !better) return entry.Url;
            DateTime at;
            if (retryAt.TryGetValue(key, out at) && now < at) return Usable(key, now);

            byte[] image;
            try
            {
                // On this thread: the thumbnail is a Windows Runtime object of the session.
                image = ReadPicture(track);
            }
            catch (Exception e)
            {
                Log.Write("Cover not read: " + Message(e));
                image = null;
            }

            // iTunes first: one search, and the player's picture when the track is
            // not there. After that it goes on as below, without iTunes.
            if (iTunesFirst && entry == null && search && !searched.Contains(key) && ITunesOpen(now))
            {
                var picture = image != null && !IsStale(track, CoverImage.Fingerprint(image)) ? image : null;
                Start(key, delegate
                {
                    var found = LookUp(track);
                    if (found.Entry != null || picture == null) return found;
                    var uploaded = FromPlayer(track, picture, null, false);
                    uploaded.Answered = found.Answered;
                    return uploaded;
                });
                return null;
            }

            if (image != null)
            {
                var print = CoverImage.Fingerprint(image);
                if (IsStale(track, print))
                {
                    retryAt[key] = now.AddSeconds(1);
                    return Usable(key, now);
                }
                lastByApp[track.App] = new Seen { Key = key, Print = print, Album = track.Album };
                Entry known;
                if (byPrint.TryGetValue(print, out known) && now < known.Renew)
                {
                    cache[key] = known;
                    return known.Url;
                }
                // When no host takes it, iTunes may stand in, once it has not answered yet.
                var orITunes = entry == null && search && !searched.Contains(key) && ITunesOpen(now);
                Start(key, () => FromPlayer(track, image, print, orITunes));
                return Usable(key, now);
            }

            // No picture now: a cover that needs renewing stays until it is gone,
            // and the player is asked again in a few seconds.
            if (entry != null)
            {
                retryAt[key] = now.AddSeconds(LookEvery);
                return entry.Url;
            }

            // The player has no picture, or has not handed it over yet. Look again
            // every second for a few seconds before turning to iTunes, whose
            // art can belong to another release.
            int looks;
            noPicture.TryGetValue(key, out looks);
            noPicture[key] = ++looks;
            if (search && looks >= PictureLooks && !searched.Contains(key) && ITunesOpen(now))
            {
                Start(key, () => LookUp(track));
                return null;
            }
            retryAt[key] = now.AddSeconds(looks < PictureLooks ? 1 : LookEvery);
            return null;
        }

        // What a finished lookup brought, into the books.
        static void Take(string key, Result result, bool iTunesFirst, DateTime now)
        {
            if (result.Answered) searched.Add(key);
            if (result.Entry != null)
            {
                cache[key] = result.Entry;
                if (result.Print != null) byPrint[result.Print] = result.Entry;
                failures.Remove(key);
                retryAt.Remove(key);
                // The player's own picture is still looked for.
                if (result.Entry.FromITunes && !iTunesFirst) retryAt[key] = now.AddSeconds(LookEvery);
            }
            else if (result.Failed)
            {
                // 2, 4, 8, 16, 32 s, then once a minute while the track plays.
                int count;
                failures.TryGetValue(key, out count);
                failures[key] = ++count;
                retryAt[key] = now.AddSeconds(Math.Min(60, 1 << count));
            }
            else
            {
                retryAt[key] = now.AddSeconds(LookEvery);
            }
        }

        // The link in hand while a better one is on its way, or null.
        static string Usable(string key, DateTime now)
        {
            Entry entry;
            return cache.TryGetValue(key, out entry) && now < entry.Gone ? entry.Url : null;
        }

        // Everything forgotten: the settings changed, or the books grew too long.
        public static void Reset()
        {
            cache.Clear();
            byPrint.Clear();
            retryAt.Clear();
            failures.Clear();
            searched.Clear();
            staleLooks.Clear();
            noPicture.Clear();
            lastByApp.Clear();
            pending.Clear();
            current = null;
        }

        // Runs a lookup in the background and tells the engine when it is done.
        static void Start(string key, Func<Result> work)
        {
            var task = Task.Run(delegate
            {
                try
                {
                    return work();
                }
                catch (Exception e)
                {
                    Log.Write("Cover lookup failed: " + Message(e));
                    return new Result { Failed = true };
                }
            });
            pending[key] = task;
            task.ContinueWith(t => Notify());
        }

        // In the background: the player's picture, made ready and uploaded.
        // orITunes: when no host takes it, the album art from iTunes instead
        // (a host that bans the IP or is down left the status with no cover).
        static Result FromPlayer(Track track, byte[] image, string print, bool orITunes)
        {
            var begin = Now();
            try
            {
                var entry = Uploaded(Upload(image));
                Log.Write("Cover from the player: " + Engine.Describe(track) + " " + entry.Url + " (" + (int)(Now() - begin).TotalMilliseconds + " ms)");
                return new Result { Entry = entry, Print = print };
            }
            catch (Exception e)
            {
                Log.Write("Cover not uploaded, will try again: " + Message(e));
                if (!orITunes) return new Result { Failed = true };
                var found = LookUp(track);
                found.Failed = true;
                return found;
            }
        }

        // An uploaded picture's link, uploaded again a little before the host deletes it.
        static Entry Uploaded(Hosts.Uploaded uploaded)
        {
            var got = Now();
            return new Entry
            {
                Url = uploaded.Url,
                Renew = got + TimeSpan.FromTicks(uploaded.Keep.Ticks * 8 / 10),
                Gone = got + uploaded.Keep - TimeSpan.FromMinutes(10),
            };
        }

        // A picture of one's own from a file: one's own status's, or the one
        // shown when a track has no cover. Uploaded like a cover, tried again
        // the same way and renewed before the host deletes it. The file is
        // told apart by its time and size, not read every round.
        class FileState
        {
            public string Print;
            public Entry Entry;
            public Task<Result> Job;
            public DateTime RetryAt;
            public int Failures;
        }

        static readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>();

        public static string FindFile(string path)
        {
            var now = Now();
            FileState state;
            if (!files.TryGetValue(path, out state)) files[path] = state = new FileState();
            var file = new FileInfo(path);
            var print = file.Exists ? file.LastWriteTimeUtc.Ticks + ":" + file.Length : null;
            if (print != state.Print)
            {
                state.Print = print;
                state.Entry = null;
                state.Failures = 0;
                state.RetryAt = DateTime.MinValue;
            }
            if (print == null) return null;
            if (state.Job != null)
            {
                if (!state.Job.IsCompleted) return FileUsable(state, now);
                var result = state.Job.Result;
                state.Job = null;
                // A picture picked since is not this one.
                if (result.Print == print)
                {
                    if (result.Entry != null)
                    {
                        state.Entry = result.Entry;
                        state.Failures = 0;
                    }
                    else
                    {
                        state.RetryAt = now.AddSeconds(Math.Min(60, 1 << ++state.Failures));
                    }
                }
            }
            if (state.Entry != null && now < state.Entry.Renew) return state.Entry.Url;
            if (now < state.RetryAt) return FileUsable(state, now);
            var name = Path.GetFileName(path);
            state.Job = Task.Run(delegate
            {
                try
                {
                    var entry = Uploaded(UploadOwn(File.ReadAllBytes(path)));
                    Log.Write("Picture " + name + ": " + entry.Url);
                    return new Result { Entry = entry, Print = print };
                }
                catch (Exception e)
                {
                    Log.Write("Picture " + name + " not uploaded, will try again: " + Message(e));
                    return new Result { Failed = true, Print = print };
                }
            });
            state.Job.ContinueWith(t => Notify());
            return FileUsable(state, now);
        }

        static string FileUsable(FileState state, DateTime now)
        {
            return state.Entry != null && now < state.Entry.Gone ? state.Entry.Url : null;
        }

        // In the background: the album art from iTunes. Its links do not expire.
        static Result FromITunes(Track track)
        {
            bool answered;
            var url = Search(track, out answered);
            if (url == null) return new Result { Answered = answered, Failed = !answered };
            Log.Write("Cover from iTunes: " + Engine.Describe(track) + " " + url);
            var entry = new Entry { Url = url, FromITunes = true, Renew = DateTime.MaxValue, Gone = DateTime.MaxValue };
            return new Result { Entry = entry, Answered = true };
        }

        static bool ITunesOpen(DateTime now)
        {
            lock (iTunesBook) return now >= iTunesPausedUntil;
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
        // other artists, so both have to match. answered: false when iTunes
        // could not be asked, and the search is to be tried again.
        static string Search(Track track, out bool answered)
        {
            answered = true;
            if (track.Source.IsVideo || track.Artist == "" || track.Title == "") return null;
            var artist = Norm(Regex.Split(track.Artist, @",|&|\bfeat\.?|\bft\.?|\bx\b", RegexOptions.IgnoreCase)[0]);
            var title = Norm(track.Title);
            if (artist.Length < 2 || title.Length < 2) return null;
            var term = Uri.EscapeDataString(track.Artist + " " + Regex.Replace(track.Title, @"\s*[(\[].*?[)\]]", ""));
            foreach (var country in Text.Ru ? new[] { "RU", "US" } : new[] { "US" })
            {
                try
                {
                    using (var response = Hosts.Http.GetAsync("https://itunes.apple.com/search?entity=song&limit=10&country=" + country + "&term=" + term).Result)
                    {
                        var status = (int)response.StatusCode;
                        if (status == 403 || status == 429)
                        {
                            // Too many searches from this address: none for a while.
                            lock (iTunesBook) iTunesPausedUntil = Now().AddMinutes(10);
                        }
                        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(status + " " + response.ReasonPhrase);
                        var text = response.Content.ReadAsStringAsync().Result;
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
                }
                catch (Exception e)
                {
                    Log.Write("iTunes search failed: " + Message(e));
                    answered = false;
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

        static string Message(Exception e)
        {
            return e.GetBaseException().Message;
        }
    }
}
