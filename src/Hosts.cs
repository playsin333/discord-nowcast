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
    // Temporary file hosts for the player's picture: Discord only shows
    // pictures by URL. uguu.se, x0.at and litterbox.catbox.moe.
    static class Hosts
    {
        public static readonly HttpClient Http = Client();

        // Hosts behind a firewall turn away requests that do not say what sends them.
        static HttpClient Client()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        // Every host is tried for a cover, the one that was quickest lately first
        // and those that failed lately last. A host that has not answered within
        // Hedge milliseconds does not hold the others back: the next one starts
        // alongside it, and the first to deliver wins.
        // litterbox is the last: since October 2026 it answers "412 No file!"
        // to every upload, its own site's included.
        static readonly string[] hosts = { "uguu.se", "x0.at", "litterbox" };
        static readonly DateTime[] downUntil = new DateTime[hosts.Length];
        // How long each host took last time, in ms; the start values keep the order above.
        static readonly int[] took = { 1000, 1100, 5000 };
        const int Hedge = 1500;
        // The uploads run side by side and each keeps the book above.
        static readonly object book = new object();

        public static string Upload(byte[] image)
        {
            var now = DateTime.Now;
            var order = new List<int>();
            for (var i = 0; i < hosts.Length; i++) order.Add(i);
            lock (book)
            {
                order.Sort(delegate(int a, int b)
                {
                    var aDown = now < downUntil[a];
                    var bDown = now < downUntil[b];
                    if (aDown != bDown) return aDown ? 1 : -1;
                    return took[a].CompareTo(took[b]);
                });
            }

            var running = new List<Task<string>>();
            Exception last = null;
            var next = 0;
            var lastStart = DateTime.Now;
            while (true)
            {
                // The next host starts when none is running, or when those running are slow.
                var waited = (int)(DateTime.Now - lastStart).TotalMilliseconds;
                if (next < order.Count && (running.Count == 0 || waited >= Hedge))
                {
                    var host = order[next++];
                    var job = Task.Run(() => To(host, image));
                    job.ContinueWith(t => { var seen = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    running.Add(job);
                    lastStart = DateTime.Now;
                    waited = 0;
                }
                if (running.Count == 0) break;
                var wait = next < order.Count ? Math.Max(1, Hedge - waited) : -1;
                var done = Task.WaitAny(running.ToArray(), wait);
                if (done < 0) continue;
                var finished = running[done];
                running.RemoveAt(done);
                if (!finished.IsFaulted) return finished.Result;
                last = finished.Exception.GetBaseException();
            }
            throw last;
        }

        // One upload to one host, with the book-keeping: how long it took, or that the host is down.
        static string To(int host, byte[] image)
        {
            var begin = DateTime.Now;
            try
            {
                var url = host == 0 ? ToUguu(image) : host == 1 ? ToX0(image) : ToLitterbox(image);
                lock (book) took[host] = (took[host] + (int)(DateTime.Now - begin).TotalMilliseconds) / 2;
                return url;
            }
            catch (Exception e)
            {
                Log.Write(hosts[host] + " failed: " + Message(e));
                lock (book) downUntil[host] = DateTime.Now.AddMinutes(10);
                throw;
            }
        }

        // litterbox.catbox.moe: kept for three days.
        static string ToLitterbox(byte[] image)
        {
            var fields = new Dictionary<string, string> { { "reqtype", "fileupload" }, { "time", "72h" } };
            var text = Post("https://litterbox.catbox.moe/resources/internals/api.php", Form(fields, "fileToUpload", image)).Trim();
            if (!text.StartsWith("https://") || text.Contains(" ")) throw new InvalidOperationException(Short(text));
            return text;
        }

        // x0.at: kept for 3 to 100 days, longer for small files.
        static string ToX0(byte[] image)
        {
            var text = Post("https://x0.at/", Form(new Dictionary<string, string>(), "file", image)).Trim();
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
                var response = Http.PostAsync(url, form).Result;
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
