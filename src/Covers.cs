using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // The cover: the album art from the iTunes search when the same artist and
    // title are found there, a link Discord shows as is. Otherwise the picture
    // the player handed to Windows; Discord only shows pictures by URL, so it
    // goes to litterbox.catbox.moe, a temporary image host that keeps it three
    // days, or uguu.se when litterbox is down.
    static class Covers
    {
        static readonly HttpClient http = Client();

        // Hosts behind a firewall turn away requests that do not say what sends them.
        static HttpClient Client()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        // Tracks without a cover yet: when to look again and how many looks are left.
        static readonly Dictionary<string, DateTime> retryAt = new Dictionary<string, DateTime>();
        static readonly Dictionary<string, int> triesLeft = new Dictionary<string, int>();
        // Tracks already looked up in iTunes: one search per track.
        static readonly HashSet<string> searched = new HashSet<string>();
        // Lookups and uploads under way. They run in the background: a slow
        // host held the whole status back for up to a minute.
        static readonly Dictionary<string, Task<string>> pending = new Dictionary<string, Task<string>>();

        // The URL of the track's cover, or null when there is none yet; the
        // status goes without it and gets it when the lookup is done.
        // search: look in iTunes first.
        public static string Find(Track track, bool search)
        {
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
                Later(key, 10);
                return null;
            }
            if (!IsDue(key)) return null;
            if (cache.Count > 500)
            {
                cache.Clear();
                retryAt.Clear();
                triesLeft.Clear();
                searched.Clear();
            }
            var isSearch = search && searched.Add(key);
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
            if (!isSearch && image == null)
            {
                // Browsers hand the picture to Windows a few seconds after
                // the track starts, so no cover now does not mean none.
                Later(key, 3);
                return null;
            }
            pending[key] = Task.Run(() => Look(track, isSearch, image));
            return null;
        }

        // In the background: the iTunes art, else the player's picture uploaded.
        static string Look(Track track, bool search, byte[] image)
        {
            if (search)
            {
                var url = Search(track);
                if (url != null) return url;
            }
            if (image == null) return null;
            try
            {
                return Upload(image);
            }
            catch (Exception e)
            {
                Log.Write("Cover not uploaded, will try again: " + Message(e));
                return null;
            }
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
                    var text = http.GetStringAsync("https://itunes.apple.com/search?entity=song&limit=10&country=" + country + "&term=" + term).Result;
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

        // Both hosts are tried for every cover; one that failed lately goes
        // last, so a cover does not wait for it to time out first. uguu.se
        // comes first: since October 2026 litterbox answers "412 No file!"
        // to every upload, its own site's included.
        static readonly string[] hosts = { "uguu.se", "litterbox" };
        static readonly DateTime[] downUntil = new DateTime[hosts.Length];

        static string Upload(byte[] image)
        {
            Exception last = null;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < hosts.Length; i++)
                {
                    // First the hosts that work, then the ones that failed lately.
                    if ((DateTime.Now < downUntil[i]) != (pass == 1)) continue;
                    try
                    {
                        return i == 0 ? ToUguu(image) : ToLitterbox(image);
                    }
                    catch (Exception e)
                    {
                        Log.Write(hosts[i] + " failed: " + Message(e));
                        downUntil[i] = DateTime.Now.AddMinutes(10);
                        last = e;
                    }
                }
            }
            throw last;
        }

        // litterbox.catbox.moe: kept for three days.
        static string ToLitterbox(byte[] image)
        {
            var fields = new Dictionary<string, string> { { "reqtype", "fileupload" }, { "time", "72h" } };
            var text = Post("https://litterbox.catbox.moe/resources/internals/api.php", Form(fields, "fileToUpload", image)).Trim();
            if (!text.StartsWith("https://") || text.Contains(" ")) throw new InvalidOperationException(Short(text));
            return text;
        }

        // uguu.se: kept for three hours, enough for a track; Discord keeps its own copy.
        static string ToUguu(byte[] image)
        {
            var text = Post("https://uguu.se/upload", Form(new Dictionary<string, string>(), "files[]", image));
            try
            {
                var reply = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                var files = (ArrayList)reply["files"];
                var url = (string)((Dictionary<string, object>)files[0])["url"];
                if (url.StartsWith("https://")) return url;
            }
            catch (Exception)
            {
            }
            throw new InvalidOperationException(Short(text));
        }

        static string Post(string url, HttpContent form)
        {
            using (form)
            {
                var response = http.PostAsync(url, form).Result;
                var text = response.Content.ReadAsStringAsync().Result;
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException((int)response.StatusCode + " " + Short(text));
                return text;
            }
        }

        // multipart/form-data written by hand, as a browser sends it: the one
        // MultipartFormDataContent writes (a quoted boundary, an extra
        // filename*) is not understood by every server.
        static HttpContent Form(Dictionary<string, string> fields, string fileField, byte[] image)
        {
            var boundary = "----nowcast" + Guid.NewGuid().ToString("N");
            var head = new StringBuilder();
            foreach (var pair in fields)
            {
                head.Append("--" + boundary + "\r\n");
                head.Append("Content-Disposition: form-data; name=\"" + pair.Key + "\"\r\n\r\n");
                head.Append(pair.Value + "\r\n");
            }
            head.Append("--" + boundary + "\r\n");
            head.Append("Content-Disposition: form-data; name=\"" + fileField + "\"; filename=\"" + (IsPng(image) ? "cover.png" : "cover.jpg") + "\"\r\n");
            head.Append("Content-Type: " + (IsPng(image) ? "image/png" : "image/jpeg") + "\r\n\r\n");
            var start = Encoding.UTF8.GetBytes(head.ToString());
            var end = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");
            var body = new byte[start.Length + image.Length + end.Length];
            start.CopyTo(body, 0);
            image.CopyTo(body, start.Length);
            end.CopyTo(body, start.Length + image.Length);
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", "multipart/form-data; boundary=" + boundary);
            return content;
        }

        // PNG starts with 0x89 'P'; players otherwise give JPEG.
        static bool IsPng(byte[] image)
        {
            return image.Length > 1 && image[0] == 0x89 && image[1] == 0x50;
        }

        // An error page's title or heading, or the start of the reply: never
        // the whole page in the log.
        static string Short(string text)
        {
            var title = Regex.Match(text, "<title>(.*?)</title>|<h1>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = title.Success ? title.Groups[1].Value + title.Groups[2].Value : text;
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 120 ? text.Substring(0, 120) + "…" : text;
        }

        static string Message(Exception e)
        {
            return e.GetBaseException().Message;
        }
    }
}
