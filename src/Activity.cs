using System;
using System.Collections.Generic;

namespace Nowcast
{
    // The Discord activity for a track: "Listening to …" (type 2), or
    // "Watching …" (type 3) for video services.
    static class Activity
    {
        // Links on the cover and the title line. Turned off for the rest of the
        // run when this Discord turns down a status that has them.
        public static bool UseLinks = true;

        // lyric: the line of the song sung now, or null.
        public static Dictionary<string, object> Build(Track track, long? startedAt, Settings settings, string lyric = null)
        {
            var source = track.Source.Name;
            var artist = track.Artist != "" ? track.Artist : source;
            var activity = new Dictionary<string, object> { { "type", track.Source.IsVideo ? 3 : 2 }, { "instance", false } };

            // What the member list shows after "Listening to": status_display_type
            // 0 is the application name, 1 the state line, 2 the details line.
            switch (settings.InList)
            {
                case "artist":
                    Lines(activity, track.Title, artist, 1);
                    break;
                case "title":
                    Lines(activity, track.Title, artist, 2);
                    break;
                case "app":
                    Lines(activity, track.Title, artist, 0);
                    break;
                default:
                    // The application is named after the service ("Listening to
                    // SoundCloud"): the source is in the header already, so the lines
                    // are the title and the artist. Otherwise the source has a line.
                    if (track.Source.ClientId != null && settings.ClientIdFor(track.Source) == track.Source.ClientId)
                        Lines(activity, track.Title, artist, 0);
                    else
                        Lines(activity, track.Artist != "" ? track.Artist + " — " + track.Title : track.Title, source, 1);
                    break;
            }
            var state = (string)activity["state"];
            if (!track.IsPlaying) activity["state"] = Limit(state + " · " + Text.Paused);

            var assets = new Dictionary<string, object>();
            var image = Image(track, settings);
            if (image != null)
            {
                assets["large_image"] = image;
                // The line under the others: the album, when there is one and it is
                // not hidden. Not the source: it is in the header or a line above.
                if (!settings.HideAlbum && track.Album != "") assets["large_text"] = Limit(track.Album);
            }
            // The line being sung: under the others in place of the album, or in
            // place of the second line when there is no picture to hold it.
            if (lyric != null && track.IsPlaying)
            {
                if (assets.ContainsKey("large_image")) assets["large_text"] = Limit(lyric);
                else activity["state"] = Limit(lyric);
            }
            if (assets.Count > 0) activity["assets"] = assets;

            if (startedAt.HasValue && track.IsPlaying)
            {
                // No length: the start alone, and Discord counts the time played.
                var timestamps = new Dictionary<string, object> { { "start", startedAt.Value } };
                if (track.Duration > 0) timestamps["end"] = startedAt.Value + (long)(track.Duration * 1000);
                activity["timestamps"] = timestamps;
            }

            var button = Button(track);
            if (settings.Buttons) activity["buttons"] = new[] { button };
            // A click on the cover or the title opens the same page as the button.
            var url = (string)button["url"];
            if (UseLinks && url.Length <= 256)
            {
                activity["details_url"] = url;
                if (assets.ContainsKey("large_image")) assets["large_url"] = url;
            }
            return activity;
        }

        // One's own status: "Listening to nowcast", "Watching nowcast" (the
        // application's name in the header) or "Playing" (Discord puts the name
        // under the header), the two lines, the time it has been going and the
        // picture; a click on the picture or the first line opens the nowcast server.
        public static Dictionary<string, object> Own(string type, string title, string line2, long startedAt, string image)
        {
            var activity = new Dictionary<string, object> { { "type", type == "playing" ? 0 : type == "watching" ? 3 : 2 }, { "instance", false } };
            activity["details"] = Limit(title);
            if (line2.Trim() != "") activity["state"] = Limit(line2);
            activity["status_display_type"] = 0;
            activity["timestamps"] = new Dictionary<string, object> { { "start", startedAt } };
            if (UseLinks) activity["details_url"] = Program.DiscordInvite;
            if (image != null)
            {
                var assets = new Dictionary<string, object> { { "large_image", image } };
                if (UseLinks) assets["large_url"] = Program.DiscordInvite;
                activity["assets"] = assets;
            }
            return activity;
        }

        // The picture for the status: the track's cover, or until it comes (or
        // when there is none) the picture picked for that, or the settings'
        // link; null for none. With "picture" the picked one on every track,
        // with "none" nothing.
        public static string Image(Track track, Settings settings)
        {
            if (settings.CoverSource == "none") return null;
            var cover = settings.Covers ? Covers.Find(track, settings) : null;
            if (cover != null) return cover;
            var picked = Covers.FindFile(Settings.FallbackPicturePath);
            if (picked != null) return picked;
            return settings.Image != "" ? settings.Image : null;
        }

        static void Lines(Dictionary<string, object> activity, string details, string state, int inList)
        {
            activity["details"] = Limit(details);
            activity["state"] = Limit(state);
            activity["status_display_type"] = inList;
        }

        // "Listen on SoundCloud": a search for the track in the same service,
        // since players do not tell Windows the track's link. YouTube otherwise.
        // On Twitch the artist is the channel and the title the stream's, so
        // the search is for the channel.
        static Dictionary<string, object> Button(Track track)
        {
            var words = track.Source.Name == "Twitch" && track.Artist != "" ? track.Artist : track.Artist + " " + track.Title;
            var hasOwn = track.Source.Search != null;
            var label = hasOwn ? string.Format(track.Source.IsVideo ? Text.WatchOn : Text.ListenOn, track.Source.Name) : Text.FindOnYouTube;
            var site = hasOwn ? track.Source.Search : "https://www.youtube.com/results?search_query=";
            // Discord turns down the whole status for a button link over 512
            // characters, and a Cyrillic letter takes six escaped: a long
            // Russian title got no status at all. The query is cut to fit.
            words = words.Trim();
            var query = Uri.EscapeDataString(words);
            while (site.Length + query.Length > 512 && words.Length > 1)
            {
                words = Cut(words, words.Length * 9 / 10).TrimEnd();
                query = Uri.EscapeDataString(words);
            }
            var url = site + query;
            return new Dictionary<string, object> { { "label", Limit(label, 32) }, { "url", url } };
        }

        // Discord takes strings of 2 to 128 characters (button labels: 32).
        static string Limit(string text, int max = 128)
        {
            text = text.Trim();
            if (text.Length > max) text = Cut(text, max - 1) + "…";
            while (text.Length < 2) text += "⠀";
            return text;
        }

        // The first length characters, without splitting an emoji in two:
        // half of one makes the JSON invalid and Discord drops the status.
        static string Cut(string text, int length)
        {
            if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text.Substring(0, length);
        }
    }
}
