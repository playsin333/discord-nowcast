using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    static class StatusReporter
    {
        internal static Action<string> Send = Post;
        internal static Func<DateTime> Now = delegate { return DateTime.UtcNow; };
        static readonly object gate = new object();
        static bool busy;
        static string sent, failed;
        static DateTime renew, retry;

        public static void Report(string title, string line2, string type, string image)
        {
            var picture = "";
            Uri uri;
            if (Uri.TryCreate(image, UriKind.Absolute, out uri) &&
                (uri.GetLeftPart(UriPartial.Authority) == Hosts.Own || uri.GetLeftPart(UriPartial.Authority) == Hosts.Reserve))
                picture = uri.AbsolutePath.TrimStart('/');
            var body = new JavaScriptSerializer().Serialize(new Dictionary<string, string> {
                { "title", Clip(title) }, { "line2", Clip(line2) }, { "type", type }, { "picture", picture }
            });
            lock (gate)
            {
                if (busy || (body == sent && Now() < renew) || (body == failed && Now() < retry)) return;
                busy = true;
            }
            Task.Run(delegate {
                try
                {
                    Send(body);
                    lock (gate) { sent = body; renew = Now().AddHours(1); failed = null; }
                }
                catch (Exception e)
                {
                    lock (gate) { failed = body; retry = Now().AddMinutes(1); }
                    Log.Write("Custom status report failed: " + e.GetBaseException().GetType().Name);
                }
                finally { lock (gate) { busy = false; } }
            });
        }

        static string Clip(string value)
        {
            value = value.Trim();
            if (value.Length <= 128) return value;
            var length = char.IsHighSurrogate(value[127]) ? 127 : 128;
            return value.Substring(0, length);
        }

        static void Post(string body)
        {
            using (var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"))
            using (var response = Hosts.UploadHttp.PostAsync(Hosts.Reserve + "/status", content).GetAwaiter().GetResult())
                response.EnsureSuccessStatusCode();
        }
    }
}
