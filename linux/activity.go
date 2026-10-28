package main

import (
	"fmt"
	"strings"
)

const discordInvite = "https://discord.gg/pHzmvghmJR"

// Links on the cover and the title line. Turned off for the rest of the
// run when this Discord turns down a status that has them.
var useLinks = true

type object = map[string]interface{}

// The Discord activity for a track: "Listening to …" (type 2), or
// "Watching …" (type 3) for video services. start: when the track started,
// Unix ms, or nil; image: the picture's link, or "".
func buildActivity(track *Track, start *int64, settings *Settings, image string) object {
	source := track.Source.Name
	artist := track.Artist
	if artist == "" {
		artist = source
	}
	kind := 2
	if track.Source.IsVideo {
		kind = 3
	}
	activity := object{"type": kind, "instance": false}

	// What the member list shows after "Listening to": status_display_type
	// 0 is the application name, 1 the state line, 2 the details line.
	switch settings.InList {
	case "artist":
		lines(activity, track.Title, artist, 1)
	case "title":
		lines(activity, track.Title, artist, 2)
	case "app":
		lines(activity, track.Title, artist, 0)
	default:
		// The application is named after the service ("Listening to
		// SoundCloud"): the source is in the header already, so the lines
		// are the title and the artist. Otherwise the source has a line.
		if track.Source.ClientID != "" && settings.ClientIDFor(track.Source) == track.Source.ClientID {
			lines(activity, track.Title, artist, 0)
		} else if track.Artist != "" {
			lines(activity, track.Artist+" — "+track.Title, source, 1)
		} else {
			lines(activity, track.Title, source, 1)
		}
	}
	if !track.IsPlaying {
		activity["state"] = limit(activity["state"].(string)+" · "+text.Paused, 128)
	}

	assets := object{}
	if image != "" {
		assets["large_image"] = image
		// The line under the others: the album, when there is one and it is
		// not hidden. Not the source: it is in the header or a line above.
		if !settings.HideAlbum && track.Album != "" {
			assets["large_text"] = limit(track.Album, 128)
		}
	}
	if len(assets) > 0 {
		activity["assets"] = assets
	}

	if start != nil && track.IsPlaying {
		// No length: the start alone, and Discord counts the time played.
		timestamps := object{"start": *start}
		if track.Duration > 0 {
			timestamps["end"] = *start + int64(track.Duration*1000)
		}
		activity["timestamps"] = timestamps
	}

	button := makeButton(track)
	if settings.Buttons {
		activity["buttons"] = []object{button}
	}
	// A click on the cover or the title opens the same page as the button.
	link := button["url"].(string)
	if useLinks && len(link) <= 256 {
		activity["details_url"] = link
		if _, ok := assets["large_image"]; ok {
			assets["large_url"] = link
		}
	}
	return activity
}

// One's own status: "Listening to nowcast", "Watching nowcast" (the
// application's name in the header) or "Playing" (Discord puts the name
// under the header), the two lines, the time it has been going and the
// picture; a click on the picture or the first line opens the nowcast server.
func ownActivity(kind, title, line2 string, start int64, image string) object {
	number := 2
	switch kind {
	case "playing":
		number = 0
	case "watching":
		number = 3
	}
	activity := object{"type": number, "instance": false}
	activity["details"] = limit(title, 128)
	if strings.TrimSpace(line2) != "" {
		activity["state"] = limit(line2, 128)
	}
	activity["status_display_type"] = 0
	activity["timestamps"] = object{"start": start}
	if useLinks {
		activity["details_url"] = discordInvite
	}
	if image != "" {
		assets := object{"large_image": image}
		if useLinks {
			assets["large_url"] = discordInvite
		}
		activity["assets"] = assets
	}
	return activity
}

func lines(activity object, details, state string, inList int) {
	activity["details"] = limit(details, 128)
	activity["state"] = limit(state, 128)
	activity["status_display_type"] = inList
}

// "Listen on SoundCloud": a search for the track in the same service, since
// players do not say the track's link. YouTube otherwise. On Twitch the
// artist is the channel and the title the stream's, so the search is for
// the channel.
func makeButton(track *Track) object {
	words := track.Artist + " " + track.Title
	if track.Source.Name == "Twitch" && track.Artist != "" {
		words = track.Artist
	}
	label := text.FindOnYouTube
	site := "https://www.youtube.com/results?search_query="
	if track.Source.Search != "" {
		format := text.ListenOn
		if track.Source.IsVideo {
			format = text.WatchOn
		}
		label = fmt.Sprintf(format, track.Source.Name)
		site = track.Source.Search
	}
	// Discord turns down the whole status for a button link over 512
	// characters, and a Cyrillic letter takes six escaped: a long Russian
	// title got no status at all. The query is cut to fit.
	words = strings.TrimSpace(words)
	query := escapeData(words)
	for len(site)+len(query) > 512 && units(words) > 1 {
		words = strings.TrimRight(cut(words, units(words)*9/10), " \t\n")
		query = escapeData(words)
	}
	return object{"label": limit(label, 32), "url": site + query}
}

// Discord takes strings of 2 to 128 characters (button labels: 32),
// counted in UTF-16 as JavaScript counts them.
func limit(s string, max int) string {
	s = strings.TrimSpace(s)
	if units(s) > max {
		s = cut(s, max-1) + "…"
	}
	for units(s) < 2 {
		s += "⠀"
	}
	return s
}

// The length in UTF-16 units: an emoji counts two.
func units(s string) int {
	n := 0
	for _, r := range s {
		n += runeUnits(r)
	}
	return n
}

func runeUnits(r rune) int {
	if r >= 0x10000 && r <= 0x10FFFF {
		return 2
	}
	return 1
}

// At most length UTF-16 units from the start, whole characters only: half
// an emoji would make the JSON invalid, and Discord drops the status.
func cut(s string, length int) string {
	n := 0
	for i, r := range s {
		size := runeUnits(r)
		if n+size > length {
			return s[:i]
		}
		n += size
	}
	return s
}

// As Uri.EscapeDataString: everything but letters, digits and -._~ escaped,
// UTF-8 bytes in capitals.
func escapeData(s string) string {
	const hex = "0123456789ABCDEF"
	var b strings.Builder
	for i := 0; i < len(s); i++ {
		c := s[i]
		if c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.' || c == '_' || c == '~' {
			b.WriteByte(c)
		} else {
			b.WriteByte('%')
			b.WriteByte(hex[c>>4])
			b.WriteByte(hex[c&15])
		}
	}
	return b.String()
}
