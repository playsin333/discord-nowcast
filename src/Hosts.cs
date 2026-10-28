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
    // Hosts for the player's picture: Discord only shows pictures by URL.
    // Our own (a Cloudflare Worker, server/worker.js) first, then the
    // temporary file hosts uguu.se and litterbox.catbox.moe. x0.at was
    // dropped in October 2026: "403 File type not allowed" to every picture.
    static class Hosts
    {
        public static readonly HttpClient Http = Client();

        // Hosts behind a firewall turn away requests that do not say what sends them.
        // 20 s: a slow connection (a VPN) took over 10 s for one that went through
        // a moment later; the next host starts alongside after Hedge anyway.
        static HttpClient Client()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        // An uploaded picture: its link and how long the host keeps it.
        public class Uploaded
        {
            public string Url;
            public TimeSpan Keep;
        }

        public const string Own = "https://nowcast-covers.playsin333.workers.dev";

        // The quickest lately first. A host that has not answered within
        // Hedge milliseconds does not hold the others back: the next one
        // starts alongside it, and the first to deliver wins. A host that
        // failed is left out for a while, longer each time; when all are
        // out, the one due back first is tried, so a network that comes back
        // is noticed. litterbox is the last: since October 2026 it answers
        // "412 No file!" to every upload, its own site's included.
        static readonly string[] hosts = { "nowcast", "uguu.se", "litterbox" };
        // As the hosts say on their sites (checked 28.10.2026): ours keeps a
        // picture 14 days, uguu.se 3 hours, litterbox the 72 hours asked for.
        static readonly TimeSpan[] keeps = { TimeSpan.FromDays(14), TimeSpan.FromHours(3), TimeSpan.FromHours(72) };
        static readonly DateTime[] downUntil = new DateTime[hosts.Length];
        static readonly int[] failures = new int[hosts.Length];
        static readonly int[] pauses = { 1, 5, 15, 30 };
        // How long each host took last time, in ms; the start values keep the order above.
        static readonly int[] took = { 800, 1000, 5000 };
        const int Hedge = 1500;
        // The uploads run side by side and each keeps the book above.
        static readonly object book = new object();

        public static Uploaded Upload(byte[] image)
        {
            var order = Order(DateTime.Now);
            var running = new List<Task<Uploaded>>();
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

        // The hosts to try, quickest first: those not left out, or the one due back first.
        static List<int> Order(DateTime now)
        {
            var order = new List<int>();
            lock (book)
            {
                for (var i = 0; i < hosts.Length; i++)
                {
                    if (now >= downUntil[i]) order.Add(i);
                }
                if (order.Count == 0)
                {
                    var soonest = 0;
                    for (var i = 1; i < hosts.Length; i++)
                    {
                        if (downUntil[i] < downUntil[soonest]) soonest = i;
                    }
                    order.Add(soonest);
                }
                order.Sort(delegate(int a, int b) { return took[a].CompareTo(took[b]); });
            }
            return order;
        }

        // One upload to one host, with the book-keeping: how long it took, or that the host is down.
        static Uploaded To(int host, byte[] image)
        {
            var begin = DateTime.Now;
            try
            {
                var url = host == 0 ? ToOwn(image) : host == 1 ? ToUguu(image) : ToLitterbox(image);
                lock (book)
                {
                    took[host] = (took[host] + (int)(DateTime.Now - begin).TotalMilliseconds) / 2;
                    failures[host] = 0;
                }
                return new Uploaded { Url = url, Keep = keeps[host] };
            }
            catch (Exception e)
            {
                Log.Write(hosts[host] + " failed: " + Message(e));
                lock (book)
                {
                    downUntil[host] = DateTime.Now.AddMinutes(pauses[Math.Min(failures[host], pauses.Length - 1)]);
                    failures[host]++;
                }
                throw;
            }
        }

        // A picture of one's own status goes to our host alone, where a bad one
        // can be removed and its uploader banned; it is kept there for 2 days.
        public static Uploaded UploadOwn(byte[] image)
        {
            return new Uploaded { Url = ToOwn(image, "?kind=own"), Keep = TimeSpan.FromDays(2) };
        }

        // Ours: the picture itself in the body, the link in the reply. Named by
        // its hash, so one album's cover is kept once for everyone.
        static string ToOwn(byte[] image)
        {
            return ToOwn(image, "");
        }

        static string ToOwn(byte[] image, string query)
        {
            var content = new ByteArrayContent(image);
            content.Headers.TryAddWithoutValidation("Content-Type", IsPng(image) ? "image/png" : "image/jpeg");
            var text = Post(Own + "/upload" + query, content).Trim();
            if (!text.StartsWith(Own + "/") || text.Contains(" ")) throw new InvalidOperationException(Short(text));
            return text;
        }

        // litterbox.catbox.moe: kept for the three days asked for.
        static string ToLitterbox(byte[] image)
        {
            var fields = new Dictionary<string, string> { { "reqtype", "fileupload" }, { "time", "72h" } };
            var text = Post("https://litterbox.catbox.moe/resources/internals/api.php", Form(fields, "fileToUpload", image)).Trim();
            if (!text.StartsWith("https://") || text.Contains(" ")) throw new InvalidOperationException(Short(text));
            return text;
        }

        // uguu.se: kept for three hours.
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
