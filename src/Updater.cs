using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // Read release metadata only; downloading and replacing the exe is manual.
    static class Updater
    {
        public static string ReleasesUrl
        {
            get { return "https://github.com/" + Program.Repo + "/releases/latest"; }
        }

        static readonly HttpClient http = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("discord-nowcast/" + Program.Version);
            return client;
        }

        public static string FindNewer()
        {
            var text = http.GetStringAsync("https://api.github.com/repos/" + Program.Repo + "/releases/latest").Result;
            return NewerTag(text);
        }

        internal static string NewerTag(string text)
        {
            var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            object value;
            var tag = values.TryGetValue("tag_name", out value) ? value as string : null;
            Version version;
            if (tag == null || !Version.TryParse(tag.TrimStart('v'), out version)) return null;
            return version > new Version(Program.Version) ? tag : null;
        }
    }
}
