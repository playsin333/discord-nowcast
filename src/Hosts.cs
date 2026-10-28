using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Nowcast
{
    // Cloudflare first, our VPS on an outage. A moderation rejection must
    // never escape to an unrelated public host.
    static class Hosts
    {
        public static readonly HttpClient Http = Client();
        internal static HttpClient UploadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        public const string Own = "https://nowcast-covers.playsin333.workers.dev";
        public const string Reserve = "https://nowcast.85-192-42-111.sslip.io:10443";
        static readonly string[] hosts = { Own, Reserve };
        static readonly DateTime[] downUntil = new DateTime[hosts.Length];
        static readonly int[] failures = new int[hosts.Length];
        internal static Func<DateTime> Now = delegate { return DateTime.UtcNow; };
        static readonly object book = new object();

        static HttpClient Client()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        public class Uploaded { public string Url; public TimeSpan Keep; }

        public class Rejected : InvalidOperationException
        {
            public readonly int Status;
            public Rejected(int status, string message) : base(status + " " + message) { Status = status; }
        }

        public static Uploaded Upload(byte[] image) { return Managed(image, ""); }
        public static Uploaded UploadOwn(byte[] image) { return Managed(image, "?kind=own"); }

        static Uploaded Managed(byte[] image, string query)
        {
            Exception last = null;
            foreach (var host in Order(Now()))
            {
                try
                {
                    var url = To(hosts[host], image, query);
                    lock (book) { failures[host] = 0; downUntil[host] = DateTime.MinValue; }
                    return new Uploaded { Url = url, Keep = TimeSpan.FromHours(query == "" ? 1 : 6) };
                }
                catch (Rejected) { throw; }
                catch (Exception e)
                {
                    last = e;
                    Log.Write(hosts[host] + " failed: " + e.GetBaseException().Message);
                    lock (book)
                    {
                        if (host == 0) downUntil[host] = Now().AddMinutes(30);
                        failures[host]++;
                    }
                }
            }
            throw last ?? new InvalidOperationException("No cover host available");
        }

        // Fixed priority: a recovered Cloudflare is tried before the VPS.
        static List<int> Order(DateTime now)
        {
            var result = new List<int>();
            lock (book)
            {
                if (now >= downUntil[0]) result.Add(0);
                result.Add(1); // Always allow the reserve, even during a Cloudflare pause.
            }
            return result;
        }

        static string To(string origin, byte[] image, string query)
        {
            using (var content = new ByteArrayContent(image))
            using (var request = new HttpRequestMessage(HttpMethod.Post, origin + "/upload" + query))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", IsPng(image) ? "image/png" : "image/jpeg");
                request.Content = content;
                request.Headers.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
                request.Headers.ExpectContinue = false;
                using (var response = UploadHttp.SendAsync(request).GetAwaiter().GetResult())
                {
                    var answer = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
                    var status = (int)response.StatusCode;
                    if (status == 400 || (status == 403 && answer == "Banned") || status == 413 || status == 415 || status == 451)
                        throw new Rejected(status, Short(answer));
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException(status + " " + Short(answer));
                    if (!Regex.IsMatch(answer, "^" + Regex.Escape(origin) + "/[0-9a-f]{40}\\.(jpg|png)$"))
                        throw new InvalidOperationException(Short(answer));
                    return answer;
                }
            }
        }

        static bool IsPng(byte[] image) { return image.Length > 1 && image[0] == 0x89 && image[1] == 0x50; }
        static string Short(string text)
        {
            var title = Regex.Match(text, "<title>(.*?)</title>|<h1>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = title.Success ? title.Groups[1].Value + title.Groups[2].Value : text;
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length > 120 ? text.Substring(0, 120) + "…" : text;
        }
    }
}
