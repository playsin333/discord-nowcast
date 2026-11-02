using System;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Nowcast
{
    // Upload directly to the VPS. Keep the legacy origin only to recognize old links.
    static class Hosts
    {
        public static readonly HttpClient Http = Client();
        internal static HttpClient UploadHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        public const string Own = "https://nowcast-covers.playsin333.workers.dev";
        public const string Reserve = "https://nowcast.85-192-42-111.sslip.io:10443";
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

        public static Uploaded UploadOwn(byte[] image, string slot) { return Managed(image, "?kind=own", slot); }

        static Uploaded Managed(byte[] image, string query, string slot = null)
        {
            return new Uploaded { Url = To(Reserve, image, query, slot), Keep = TimeSpan.FromHours(query == "" ? 1 : 6) };
        }

        static string To(string origin, byte[] image, string query, string slot)
        {
            using (var content = new ByteArrayContent(image))
            using (var request = new HttpRequestMessage(HttpMethod.Post, origin + "/upload" + query))
            {
                content.Headers.TryAddWithoutValidation("Content-Type", IsPng(image) ? "image/png" : "image/jpeg");
                request.Content = content;
                request.Headers.TryAddWithoutValidation("User-Agent", "discord-nowcast/" + Program.Version);
                if (slot != null)
                {
                    request.Headers.TryAddWithoutValidation("X-Nowcast-Installation", StatusReporter.Installation());
                    request.Headers.TryAddWithoutValidation("X-Nowcast-Slot", slot);
                }
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
