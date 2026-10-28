package main

import (
	"context"
	"fmt"
	"strings"
	"time"

	"github.com/godbus/dbus/v5"
)

// MPRIS, the players' common board on D-Bus (org.mpris.MediaPlayer2.*): the
// counterpart of the Windows media sessions. Almost every player and
// browser on Linux writes what it plays there.
const mprisPrefix = "org.mpris.MediaPlayer2."

type Media struct {
	conn *dbus.Conn
	// Each player's .desktop file and own name, by its bus name: they do not change.
	ids map[string]PlayerID
	// The last position each player gave and when it was first seen: a
	// player that keeps one position while it plays is not taken to have
	// started anew every round.
	positions map[string]seenPosition
	// When each player's track or state last changed: the latest is "current".
	changes map[string]seenChange
}

type seenPosition struct {
	position float64
	at       int64
}

type seenChange struct {
	key     string
	playing bool
	at      time.Time
}

func newMedia() *Media {
	return &Media{ids: map[string]PlayerID{}, positions: map[string]seenPosition{}, changes: map[string]seenChange{}}
}

func (m *Media) open() error {
	if m.conn != nil && m.conn.Connected() {
		return nil
	}
	conn, err := dbus.ConnectSessionBus()
	if err != nil {
		return err
	}
	m.conn = conn
	return nil
}

func (m *Media) Close() {
	if m.conn != nil {
		m.conn.Close()
		m.conn = nil
	}
}

// Every player with something to show.
func (m *Media) Read(settings *Settings) ([]*Track, error) {
	if err := m.open(); err != nil {
		return nil, err
	}
	var names []string
	if err := m.call(m.conn.BusObject(), "org.freedesktop.DBus.ListNames").Store(&names); err != nil {
		// The bus can go away under the program: asked for anew next time.
		m.Close()
		return nil, err
	}
	var tracks []*Track
	alive := map[string]bool{}
	for _, name := range names {
		// playerctld only repeats another player.
		if !strings.HasPrefix(name, mprisPrefix) || name == mprisPrefix+"playerctld" {
			continue
		}
		alive[name] = true
		track, err := m.readPlayer(name, settings)
		if err != nil || track == nil {
			// A player can close between listing and reading; it is left out.
			continue
		}
		tracks = append(tracks, track)
	}
	for name := range m.ids {
		if !alive[name] {
			delete(m.ids, name)
			delete(m.positions, name)
			delete(m.changes, name)
		}
	}
	// The player last heard from is the current one.
	var latest *Track
	var latestAt time.Time
	for _, track := range tracks {
		if at := m.changes[mprisPrefix+track.App].at; latest == nil || at.After(latestAt) {
			latest, latestAt = track, at
		}
	}
	if latest != nil {
		latest.IsCurrent = true
	}
	return tracks, nil
}

func (m *Media) call(object dbus.BusObject, method string, args ...interface{}) *dbus.Call {
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	return object.CallWithContext(ctx, method, 0, args...)
}

func (m *Media) properties(name, iface string) (map[string]dbus.Variant, error) {
	var values map[string]dbus.Variant
	err := m.call(m.conn.Object(name, "/org/mpris/MediaPlayer2"), "org.freedesktop.DBus.Properties.GetAll", iface).Store(&values)
	return values, err
}

func (m *Media) readPlayer(name string, settings *Settings) (*Track, error) {
	values, err := m.properties(name, "org.mpris.MediaPlayer2.Player")
	if err != nil {
		return nil, err
	}
	status, _ := values["PlaybackStatus"].Value().(string)
	// Stopped players often keep the last track: nothing plays there.
	if status != "Playing" && status != "Paused" {
		return nil, nil
	}
	metadata, _ := values["Metadata"].Value().(map[string]dbus.Variant)
	track := &Track{
		App:       strings.TrimPrefix(name, mprisPrefix),
		Artist:    strings.TrimSpace(strings.Join(stringsOf(metadata["xesam:artist"]), ", ")),
		Title:     strings.TrimSpace(stringOf(metadata["xesam:title"])),
		Album:     strings.TrimSpace(stringOf(metadata["xesam:album"])),
		IsPlaying: status == "Playing",
		ArtURL:    stringOf(metadata["mpris:artUrl"]),
		URL:       stringOf(metadata["xesam:url"]),
		Duration:  float64(numberOf(metadata["mpris:length"])) / 1e6,
	}
	if track.Artist == "" && track.Title == "" {
		return nil, nil
	}

	id, ok := m.ids[name]
	if !ok {
		id = PlayerID{Bus: track.App}
		if about, err := m.properties(name, "org.mpris.MediaPlayer2"); err == nil {
			id.DesktopEntry = stringOf(about["DesktopEntry"])
			id.Identity = stringOf(about["Identity"])
		}
		m.ids[name] = id
	}
	track.Player = id
	track.Source = resolveSource(id, settings)
	if track.Source.IsBrowser || track.Source.ClientID == "" {
		if site := siteSource(track.URL, track.ArtURL); site != nil {
			track.Source = site
		}
	}
	repair(track)

	// Position: as of now when it moved, as of when it was first seen when it did not.
	now := time.Now().UnixMilli()
	if position, ok := values["Position"]; ok {
		track.Position = float64(numberOf(position)) / 1e6
		seen, had := m.positions[name]
		if had && seen.position == track.Position {
			track.UpdatedAt = seen.at
		} else {
			track.UpdatedAt = now
			m.positions[name] = seenPosition{position: track.Position, at: now}
		}
	}
	if track.Duration < 0 {
		track.Duration = 0
	}

	change := m.changes[name]
	if change.key != track.Key() || change.playing != track.IsPlaying {
		m.changes[name] = seenChange{key: track.Key(), playing: track.IsPlaying, at: time.Now()}
	}
	return track, nil
}

func stringOf(v dbus.Variant) string {
	switch value := v.Value().(type) {
	case string:
		return value
	case dbus.ObjectPath:
		return string(value)
	case []string:
		return strings.Join(value, ", ")
	}
	return ""
}

func stringsOf(v dbus.Variant) []string {
	switch value := v.Value().(type) {
	case []string:
		return value
	case string:
		return []string{value}
	case []interface{}:
		var out []string
		for _, item := range value {
			out = append(out, fmt.Sprint(item))
		}
		return out
	}
	return nil
}

// Players disagree on the type of the length and position: int64 by the
// specification, uint64, int32 or a double elsewhere.
func numberOf(v dbus.Variant) int64 {
	switch value := v.Value().(type) {
	case int64:
		return value
	case uint64:
		return int64(value)
	case int32:
		return int64(value)
	case uint32:
		return int64(value)
	case float64:
		return int64(value)
	}
	return 0
}
