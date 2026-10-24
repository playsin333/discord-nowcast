using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Nowcast
{
    // A line of the sources list in the tray menu and the settings window.
    class SourceChoice
    {
        public string Name;
        public string App;
        public bool IsShown;
    }

    // %APPDATA%\discord-nowcast\config.json. Written with the defaults on the
    // first run; a broken file falls back to the defaults and says so in the log.
    class Settings
    {
        public static readonly string FilePath = Path.Combine(Log.Folder, "config.json");

        // The general Discord application, for players without one of their own.
        public const string DefaultClientId = "1562901782579912804";

        public string ClientId = DefaultClientId;
        // What the member list shows after "Listening to": source, artist, title, app.
        public string InList = "source";
        public bool ShowPaused = false;
        public bool Covers = true;
        // Album art from the iTunes search when the track is found there.
        public bool CoverSearch = true;
        public bool Buttons = true;
        // No caption under the lines of the status: neither the album nor the source.
        public bool HideAlbum = false;
        public bool CheckUpdates = true;
        public string Image = "";
        public List<string> Hide = new List<string>();
        public Dictionary<string, string> Names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                Directory.CreateDirectory(Log.Folder);
                if (!File.Exists(FilePath))
                {
                    settings.Save();
                }
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (values != null) settings.Apply(values);
            }
            catch (Exception e)
            {
                Log.Write("config.json could not be read, using the defaults: " + e.Message);
            }
            return settings;
        }

        // Writes every setting, one per line, so the file stays easy to edit by hand.
        public void Save()
        {
            var json = new JavaScriptSerializer();
            var names = new List<string>();
            foreach (var pair in Names) names.Add(json.Serialize(pair.Key) + ": " + json.Serialize(pair.Value));
            var lines = new[]
            {
                "  \"clientId\": " + json.Serialize(ClientId),
                "  \"inList\": " + json.Serialize(InList),
                "  \"showPaused\": " + json.Serialize(ShowPaused),
                "  \"covers\": " + json.Serialize(Covers),
                "  \"coverSearch\": " + json.Serialize(CoverSearch),
                "  \"buttons\": " + json.Serialize(Buttons),
                "  \"hideAlbum\": " + json.Serialize(HideAlbum),
                "  \"checkUpdates\": " + json.Serialize(CheckUpdates),
                "  \"image\": " + json.Serialize(Image),
                "  \"hide\": " + json.Serialize(Hide),
                "  \"names\": {" + string.Join(", ", names) + "}",
            };
            Directory.CreateDirectory(Log.Folder);
            File.WriteAllText(FilePath, "{\r\n" + string.Join(",\r\n", lines) + "\r\n}\r\n", new UTF8Encoding(false));
        }

        // The application a source goes out under: its own one ("Listening to
        // SoundCloud"), unless clientId names an application of your own.
        public string ClientIdFor(SourceInfo source)
        {
            if (ClientId != DefaultClientId || source.ClientId == null) return ClientId;
            return source.ClientId;
        }

        // Whether a word in "hide" matches the app: by its ID or by the name shown.
        public bool Hides(string app, string source)
        {
            foreach (var word in Hide)
            {
                if (app.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (source.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // Each app playing now, plus the hidden ones, so they can come back.
        public List<SourceChoice> SourceChoices(IEnumerable<Track> sessions)
        {
            var list = new List<SourceChoice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var track in sessions)
            {
                if (!seen.Add(track.Source.Name)) continue;
                list.Add(new SourceChoice { Name = track.Source.Name, App = track.App, IsShown = !Hides(track.App, track.Source.Name) });
            }
            foreach (var word in Hide)
            {
                if (seen.Add(word)) list.Add(new SourceChoice { Name = word, App = word, IsShown = false });
            }
            return list;
        }

        // Shows or hides a source, as the tray's Sources menu does.
        public void SetHidden(string app, string source, bool isHidden)
        {
            if (isHidden)
            {
                if (!Hides(app, source)) Hide.Add(source);
            }
            else
            {
                Hide.RemoveAll(word =>
                    app.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    source.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        void Apply(Dictionary<string, object> values)
        {
            var clientId = Get<string>(values, "clientId");
            if (clientId != null && clientId.Trim() != "") ClientId = clientId.Trim();
            InList = Get<string>(values, "inList") ?? InList;
            ShowPaused = Get(values, "showPaused", ShowPaused);
            Covers = Get(values, "covers", Covers);
            CoverSearch = Get(values, "coverSearch", CoverSearch);
            Buttons = Get(values, "buttons", Buttons);
            HideAlbum = Get(values, "hideAlbum", HideAlbum);
            CheckUpdates = Get(values, "checkUpdates", CheckUpdates);
            Image = Get<string>(values, "image") ?? Image;

            var hide = Get<ArrayList>(values, "hide");
            if (hide != null)
            {
                foreach (var word in hide)
                {
                    if (word is string && (string)word != "") Hide.Add((string)word);
                }
            }
            var names = Get<Dictionary<string, object>>(values, "names");
            if (names != null)
            {
                foreach (var pair in names)
                {
                    if (pair.Value is string) Names[pair.Key] = (string)pair.Value;
                }
            }
        }

        static T Get<T>(Dictionary<string, object> values, string key) where T : class
        {
            object value;
            return values.TryGetValue(key, out value) ? value as T : null;
        }

        static bool Get(Dictionary<string, object> values, string key, bool fallback)
        {
            object value;
            return values.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }
    }
}
