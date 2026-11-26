using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Nowcast
{
    static class Announcements
    {
        internal const string Feed = "https://raw.githubusercontent.com/" + Program.Repo + "/main/announcement.json";

        internal static async Task<string> Fetch()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 4096 })
                return await client.GetStringAsync(Feed).ConfigureAwait(false);
        }

        internal static void Deliver(string json, string marker, Action<string> notify)
        {
            var item = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            object id, body;
            if (item == null || !item.TryGetValue("id", out id) || !(id is int) ||
                !item.TryGetValue("message", out body) || !(body is string)) return;
            int sequence = (int)id;
            string message = ((string)body).Trim();
            if (sequence <= 0 || message.Length == 0 || message.Length > 240) return;
            if (File.Exists(marker) && sequence <= int.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture)) return;
            notify(message);
            // Save after the notification was handed to Windows.
            string temp = marker + ".tmp";
            File.WriteAllText(temp, sequence.ToString(CultureInfo.InvariantCulture));
            if (File.Exists(marker)) File.Replace(temp, marker, null);
            else File.Move(temp, marker);
        }
    }
}
