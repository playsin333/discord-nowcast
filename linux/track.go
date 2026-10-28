package main

import (
	"regexp"
	"strings"
)

type Track struct {
	// The player's bus name after org.mpris.MediaPlayer2.: "spotify", "firefox.instance_1_42".
	App       string
	Player    PlayerID
	Source    *SourceInfo
	Artist    string
	Title     string
	Album     string
	IsPlaying bool
	// The player most recently heard from, as Windows' "current session".
	IsCurrent bool
	// Seconds; Position is as of UpdatedAt (Unix ms, 0 when unknown).
	Position  float64
	Duration  float64
	UpdatedAt int64
	// mpris:artUrl: file://, https:// or data:.
	ArtURL string
	// xesam:url: the page in a browser, the file in a player.
	URL string
}

func (t *Track) Key() string { return t.App + "|" + t.Artist + "|" + t.Title }

func describe(track *Track) string {
	line := track.Title
	if track.Artist != "" {
		line = track.Artist + " — " + track.Title
	}
	return line + " · " + track.Source.Name
}

// Tags sites add to titles: (Official Video), [HD] and the like.
var (
	junk   = regexp.MustCompile(`(?i)\s*[(\[](?:official\s+)?(?:music\s+|lyric\s+)?(?:video|audio|visuali[sz]er|lyrics?)(?:\s+video)?[)\]]|\s*[(\[](?:hd|hq|4k|official|клип|премьера клипа|премьера)[)\]]`)
	dashed = regexp.MustCompile(`^(.+?)\s+[-–—]\s+(.+)$`)
	topic  = regexp.MustCompile(`(?i)\s*-\s*Topic$`)
	vevo   = regexp.MustCompile(`VEVO$`)
)

// As Media.Repair on Windows.
func repair(track *Track) {
	artist := strings.TrimSpace(track.Artist)
	title := strings.TrimSpace(junk.ReplaceAllString(track.Title, ""))
	// Browser tabs often give no artist and "Artist - Title" as the title.
	if artist == "" {
		if m := dashed.FindStringSubmatch(title); m != nil {
			artist, title = m[1], m[2]
		}
	}
	// "Artist - Topic" and "ArtistVEVO" channels post music, not videos:
	// "Listening to YouTube" for them, and the cover from iTunes.
	isMusic := topic.MatchString(artist) || vevo.MatchString(artist)
	if isMusic && track.Source.IsVideo && track.Source.Name == "YouTube" {
		copied := *track.Source
		copied.IsVideo = false
		track.Source = &copied
	}
	artist = topic.ReplaceAllString(artist, "")
	artist = vevo.ReplaceAllString(artist, "")
	track.Artist = strings.TrimSpace(artist)
	track.Title = title
}
