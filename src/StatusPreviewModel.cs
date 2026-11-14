using System;
using System.Collections.Generic;

namespace Nowcast
{
    // The same activity formatter as the real status, without media/network work.
    sealed class StatusPreviewModel
    {
        public string Header, Details, State, Extra, Button, Member, Hint;

        public static StatusPreviewModel Music(Track track, Settings settings, bool hasImage, string lyric)
        {
            bool example = track == null;
            if (example) track = new Track
            {
                App = "preview", Source = new SourceInfo { Name = "Nowcast", ClientId = Settings.DefaultClientId },
                Title = Text.PreviewTitle, Artist = Text.PreviewArtist, Album = Text.PreviewAlbum, IsPlaying = true,
            };
            var activity = Activity.BuildWithImage(track, null, settings, hasImage ? "preview" : null, lyric);
            string app = settings.ClientId != Settings.DefaultClientId ? Text.PreviewApplication
                : track.Source.ClientId != null ? track.Source.Name : "Nowcast";
            app = settings.ActivityNameOverride ?? app;
            var model = From(activity, app);
            int display = (int)activity["status_display_type"];
            model.Member = Text.PreviewMember + Prefix((int)activity["type"])
                + (display == 1 ? model.State : display == 2 ? model.Details : app);
            model.Hint = example ? Text.PreviewExample : Text.PreviewHint;
            return model;
        }

        public static StatusPreviewModel Own(string type, string title, string line2, int minutes, bool hasImage, string activityName = null)
        {
            var activity = Activity.Own(type, string.IsNullOrWhiteSpace(title) ? Text.PreviewOwnTitle : title, line2, 0, hasImage ? "preview" : null, activityName);
            var model = From(activity, activityName ?? "Nowcast");
            model.Extra = string.Format("{0:00}:{1:00}:00", minutes / 60, minutes % 60);
            model.Member = Text.PreviewMember + model.Header;
            model.Hint = Text.PreviewHint;
            return model;
        }

        static string Prefix(int type)
        { return type == 0 ? Text.PreviewPlaying : type == 3 ? Text.PreviewWatching : Text.PreviewListening; }

        static StatusPreviewModel From(Dictionary<string, object> activity, string app)
        {
            var model = new StatusPreviewModel
            {
                Header = Prefix((int)activity["type"]) + app,
                Details = Read(activity, "details"), State = Read(activity, "state"), Extra = "", Button = "",
            };
            object value;
            if (activity.TryGetValue("assets", out value)) model.Extra = Read((Dictionary<string, object>)value, "large_text");
            if (activity.TryGetValue("buttons", out value))
            {
                var buttons = (Dictionary<string, object>[])value;
                if (buttons.Length > 0) model.Button = Read(buttons[0], "label");
            }
            return model;
        }

        static string Read(Dictionary<string, object> values, string key)
        { object value; return values.TryGetValue(key, out value) ? (string)value : ""; }
    }
}
