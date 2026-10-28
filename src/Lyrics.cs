using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // A line of a song's text and when it is sung, ms from the start.
    class LyricLine
    {
        public long At;
        public string Text;
    }

    // The song's text with timings from lrclib.net, an open database: the line
    // sung now goes into the status. Looked up once per track in the
    // background; a failed lookup is tried again, 2 … 60 s apart, while the
    // track plays. Discord takes a status every 4 s, so a quick line can be
    // skipped: the status shows the line of the moment it goes out.
    static class Lyrics
    {
        // By track: its lines, empty when the database has no timed text.
        static readonly Dictionary<string, List<LyricLine>> found = new Dictionary<string, List<LyricLine>>();
        static readonly Dictionary<string, Task<List<LyricLine>>> pending = new Dictionary<string, Task<List<LyricLine>>>();
        static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> failures = new Dictionary<string, int>();

        // Stand-ins for the tests: the clock and the lookup (null: failed, try again).
        public static Func<DateTime> Now = delegate { return DateTime.Now; };
        public static Func<Track, List<LyricLine>> LookUp = FromLrclib;

        // Raised when a lookup is done, so the line goes out without waiting a round.
        public static event Action Ready;

        // The track's lines, or null while they are looked for or when there are none.
        public static List<LyricLine> Find(Track track)
        {
            var key = track.Key;
            List<LyricLine> lines;
            if (found.TryGetValue(key, out lines)) return lines.Count > 0 ? lines : null;
            Task<List<LyricLine>> job;
            if (pending.TryGetValue(key, out job))
            {
                if (!job.IsCompleted) return null;
                pending.Remove(key);
                if (job.Result != null)
                {
                    if (found.Count > 200) found.Clear();
                    found[key] = job.Result;
                    failures.Remove(key);
                    retryAt.Remove(key);
                    return job.Result.Count > 0 ? job.Result : null;
                }
                int count;
                failures.TryGetValue(key, out count);
                failures[key] = ++count;
                retryAt[key] = Now().AddSeconds(Math.Min(60, 1 << count));
                return null;
            }
            DateTime at;
            if (retryAt.TryGetValue(key, out at) && Now() < at) return null;
            job = Task.Run(delegate
            {
                try
                {
                    return LookUp(track);
                }
                catch (Exception e)
                {
                    Log.Write("Lyrics lookup failed: " + e.GetBaseException().Message);
                    return null;
                }
            });
            pending[key] = job;
            job.ContinueWith(t =>
            {
                var handler = Ready;
                if (handler != null) handler();
            });
            return null;
        }

        public static void Reset()
        {
            found.Clear();
            pending.Clear();
            retryAt.Clear();
            failures.Clear();
        }

        // The line sung at ms from the start: null before the first, "♪" for a
        // pause between lines.
        public static string Line(List<LyricLine> lines, long ms)
        {
            if (lines == null) return null;
            string line = null;
            foreach (var next in lines)
            {
                if (next.At > ms) break;
                line = next.Text;
            }
            if (line == null) return null;
            return line.Trim() == "" ? "♪" : line.Trim();
        }

        static readonly Regex stamp = new Regex(@"\[(\d+):(\d+(?:[.,]\d+)?)\]");

        // LRC: "[01:02.50]words", several times before one line when it repeats.
        public static List<LyricLine> Parse(string lrc)
        {
            var lines = new List<LyricLine>();
            if (string.IsNullOrEmpty(lrc)) return lines;
            foreach (var raw in lrc.Split('\n'))
            {
                var matches = stamp.Matches(raw);
                if (matches.Count == 0) continue;
                var last = matches[matches.Count - 1];
                var text = raw.Substring(last.Index + last.Length).Trim();
                foreach (Match match in matches)
                {
                    var minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var seconds = double.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    lines.Add(new LyricLine { At = (long)((minutes * 60 + seconds) * 1000), Text = text });
                }
            }
            lines.Sort(delegate(LyricLine a, LyricLine b) { return a.At.CompareTo(b.At); });
            return lines;
        }

        // In the background: the timed text from lrclib.net, by artist, title and
        // length; empty when there is none, null when the site could not be asked.
        static List<LyricLine> FromLrclib(Track track)
        {
            if (track.Artist == "" || track.Title == "") return new List<LyricLine>();
            var query = "artist_name=" + Uri.EscapeDataString(track.Artist) + "&track_name=" + Uri.EscapeDataString(track.Title);
            var exact = Get("https://lrclib.net/api/get?" + query + (track.Duration > 0 ? "&duration=" + (int)Math.Round(track.Duration) : ""));
            if (exact == null) return null;
            var synced = exact as Dictionary<string, object>;
            var text = synced != null && synced.ContainsKey("syncedLyrics") ? synced["syncedLyrics"] as string : null;
            if (text == null)
            {
                // No exact match: the closest by length among those found.
                var results = Get("https://lrclib.net/api/search?" + query) as object[];
                // The site busy or down: asked again later.
                if (results == null) return null;
                foreach (Dictionary<string, object> result in results)
                {
                    var lyrics = result.ContainsKey("syncedLyrics") ? result["syncedLyrics"] as string : null;
                    if (string.IsNullOrEmpty(lyrics)) continue;
                    var length = result.ContainsKey("duration") ? Convert.ToDouble(result["duration"], CultureInfo.InvariantCulture) : 0;
                    if (track.Duration > 0 && Math.Abs(length - track.Duration) > 3) continue;
                    text = lyrics;
                    break;
                }
            }
            var lines = Parse(text);
            Log.Write("Lyrics for " + Engine.Describe(track) + ": " + (lines.Count > 0 ? lines.Count + " lines" : "none"));
            return lines;
        }

        // The reply as JSON; an empty object for "not found", null when the site did not answer.
        static object Get(string url)
        {
            using (var response = Hosts.Http.GetAsync(url).Result)
            {
                var status = (int)response.StatusCode;
                if (status == 404) return new Dictionary<string, object> { { "syncedLyrics", null } };
                if (!response.IsSuccessStatusCode)
                {
                    Log.Write("lrclib.net: " + status);
                    return null;
                }
                return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(response.Content.ReadAsStringAsync().Result);
            }
        }
    }
}
