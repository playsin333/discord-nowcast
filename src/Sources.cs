using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Nowcast
{
    class SourceInfo
    {
        // As shown in the status: "Яндекс Музыка", "SoundCloud", "Chrome".
        public string Name;
        // Search page the status button opens, the query appended; null: YouTube.
        public string Search;
        // The Discord application named after the service, so the status reads
        // "Listening to SoundCloud"; null: the general one from the settings.
        public string ClientId;
        // "Watching" (type 3) instead of "Listening to": YouTube, Twitch.
        public bool IsVideo;
        // A browser: the site may be found by the window title.
        public bool IsBrowser;
    }

    // Which app a session comes from, by its app ID.
    static class Sources
    {
        class Rule
        {
            public Regex Pattern;
            public string Name;
            public string Search;
            public string ClientId;
            public bool IsVideo;
            public bool IsBrowser;

            public Rule(string pattern, string name, string search, string clientId = null, bool isVideo = false)
            {
                Pattern = new Regex(pattern, RegexOptions.IgnoreCase);
                Name = name;
                Search = search;
                ClientId = clientId;
                IsVideo = isVideo;
            }
        }

        static Rule Browser(string pattern, string name)
        {
            return new Rule(pattern, name, null) { IsBrowser = true };
        }

        // Order matters: Yandex Music before Yandex Browser, YouTube Music before YouTube.
        // Yandex Music has no search link of its own: with a button to
        // music.yandex.ru Discord took the status but did not show it to anyone else.
        static readonly Rule[] rules =
        {
            new Rule(@"yandex.*music|music.*yandex|яндекс.?музык", Text.Ru ? "Яндекс Музыка" : "Yandex Music", null, "1563159088265822392"),
            new Rule(@"spotify", "Spotify", "https://open.spotify.com/search/", "1563159215709749389"),
            new Rule(@"soundcloud", "SoundCloud", "https://soundcloud.com/search?q=", "1563159356940361779"),
            new Rule(@"youtube.?music", "YouTube Music", "https://music.youtube.com/search?q=", "1563162689163886642"),
            new Rule(@"youtube", "YouTube", "https://www.youtube.com/results?search_query=", "1563159477673263216", true),
            new Rule(@"twitch", "Twitch", "https://www.twitch.tv/search?term=", "1563159932218646588", true),
            new Rule(@"telegram", "Telegram", null, "1563159812382920706"),
            new Rule(@"vk.?music|vk музык|vkontakte|вконтакте|\bvk\b|\bвк\b", Text.Ru ? "VK Музыка" : "VK Music", "https://vk.com/audio?q=", "1563159553619525723"),
            new Rule(@"applemusic|apple music|itunes", "Apple Music", "https://music.apple.com/search?term=", "1563159657676021881"),
            new Rule(@"zunemusic", Text.Ru ? "Медиаплеер Windows" : "Windows Media Player", null),
            new Rule(@"aimp", "AIMP", null, "1563516800287907893"),
            new Rule(@"foobar", "foobar2000", null),
            new Rule(@"vlc", "VLC", null),
            new Rule(@"musicbee", "MusicBee", null),
            Browser(@"yandex|^browser", Text.Ru ? "Яндекс Браузер" : "Yandex Browser"),
            Browser(@"chrome", "Chrome"),
            Browser(@"msedge|microsoftedge", "Edge"),
            // Firefox names itself by a hash of its folder: 308046B0AF4A39CB in
            // the usual one, other 16 hex digits elsewhere and in its forks (Zen, Floorp, LibreWolf).
            Browser(@"firefox|^[0-9a-f]{16}$", "Firefox"),
            Browser(@"opera", "Opera"),
            Browser(@"vivaldi", "Vivaldi"),
            Browser(@"brave", "Brave"),
        };

        public static SourceInfo Resolve(string app, Settings settings)
        {
            string name = null;
            // Your own names from config.json come first: {"chrome": "YouTube"}.
            foreach (var pair in settings.Names)
            {
                if (app.IndexOf(pair.Key, StringComparison.OrdinalIgnoreCase) >= 0) name = pair.Value;
            }
            // Sites installed as apps (Chrome._crx_…) and Store apps (…!App):
            // the name from the Start menu, so SoundCloud reads as SoundCloud.
            var isStartMenu = false;
            if (name == null && (app.Contains("_crx_") || app.Contains("!")))
            {
                name = StartMenuName(app);
                isStartMenu = name != null;
            }

            // The button follows the name when it is known, the app ID otherwise.
            var byName = name == null ? null : Match(name);
            var rule = byName ?? Match(app);
            // A known service by its own name: "SoundCloud - Music & Audio",
            // as the Start menu has it, reads "SoundCloud".
            if (isStartMenu && byName != null && byName.ClientId != null) name = byName.Name;
            if (name == null) name = rule != null ? rule.Name : Fallback(app);
            return Info(name, rule);
        }

        static SourceInfo Info(string name, Rule rule)
        {
            return new SourceInfo
            {
                Name = name,
                Search = rule != null ? rule.Search : null,
                ClientId = rule != null ? rule.ClientId : null,
                IsVideo = rule != null && rule.IsVideo,
                // Only when the name is ours: a name from "names" stays as given.
                IsBrowser = rule != null && rule.IsBrowser && rule.Name == name,
            };
        }

        // A browser reports itself, not the site. The window shows the site in
        // its title, "Video - YouTube - Google Chrome", but only for the tab in
        // front, so this finds the window whose title holds the track and looks
        // for a site in the rest of it: the track's own words could name one.
        public static SourceInfo FromWindows(string title, string artist)
        {
            foreach (var window in WindowTitles.Read())
            {
                var rest = window;
                var holds = false;
                foreach (var part in new[] { title, artist })
                {
                    if (part.Length < 3 || rest.IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    rest = Regex.Replace(rest, Regex.Escape(part), " ", RegexOptions.IgnoreCase);
                    holds = true;
                }
                if (!holds) continue;
                // A playing SoundCloud tab is titled "▶ Title by Artist", without the site's name.
                if (title.Length >= 2 && artist.Length >= 2 &&
                    window.IndexOf(title + " by " + artist, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var soundCloud = Match("soundcloud");
                    return Info(soundCloud.Name, soundCloud);
                }
                // The Spotify web player is titled "Title • Artist", without the site's name either.
                if (title.Length >= 2 && artist.Length >= 2 &&
                    window.IndexOf(title + " • " + artist, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var spotify = Match("spotify");
                    return Info(spotify.Name, spotify);
                }
                var rule = Match(rest);
                if (rule != null && rule.ClientId != null) return Info(rule.Name, rule);
            }
            return null;
        }

        static Rule Match(string text)
        {
            foreach (var rule in rules)
            {
                if (rule.Pattern.IsMatch(text)) return rule;
            }
            return null;
        }

        // Microsoft.Package_8wekyb3d8bbwe!App or Something.exe -> Package / Something.
        static string Fallback(string app)
        {
            var name = Regex.Replace(app, "!.*$", "");
            name = Regex.Replace(name, "_[a-z0-9]{13}$", "");
            name = Regex.Replace(name, @"\.exe$", "", RegexOptions.IgnoreCase);
            var parts = name.Split('.');
            name = parts[parts.Length - 1];
            return name != "" ? name : (Text.Ru ? "Музыка" : "Music");
        }

        // App ID -> name, as the Start menu (shell:AppsFolder) lists them.
        // Read again at most once a minute, when an unknown app shows up.
        static readonly Dictionary<string, string> startMenu = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static DateTime startMenuReadAt = DateTime.MinValue;

        static string StartMenuName(string app)
        {
            string name;
            if (startMenu.TryGetValue(app, out name)) return name;
            if ((DateTime.Now - startMenuReadAt).TotalSeconds < 60) return null;
            startMenuReadAt = DateTime.Now;
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                var shell = Activator.CreateInstance(shellType);
                var folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                var items = folder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                foreach (var item in (IEnumerable)items)
                {
                    var id = item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null) as string;
                    var title = item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null) as string;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title)) startMenu[id] = title;
                }
            }
            catch (Exception e)
            {
                Log.Write("Start menu names unavailable: " + e.Message);
            }
            return startMenu.TryGetValue(app, out name) ? name : null;
        }
    }
}
