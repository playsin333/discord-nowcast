package main

import (
	"os"
	"strings"
	"testing"
	"time"

	"github.com/godbus/dbus/v5"
	"github.com/godbus/dbus/v5/prop"
)

// A player of our own on the session bus, as MPRIS players are: run the
// tests under dbus-run-session to have one (CI does).
func startFakePlayer(t *testing.T, name string, metadata map[string]dbus.Variant, status string, position int64) *prop.Properties {
	if os.Getenv("DBUS_SESSION_BUS_ADDRESS") == "" {
		t.Skip("no session bus: run under dbus-run-session")
	}
	conn, err := dbus.ConnectSessionBus()
	if err != nil {
		t.Skip("no session bus: ", err)
	}
	t.Cleanup(func() { conn.Close() })
	properties, err := prop.Export(conn, "/org/mpris/MediaPlayer2", prop.Map{
		"org.mpris.MediaPlayer2": {
			"Identity":     {Value: "Test Player"},
			"DesktopEntry": {Value: "test-player"},
		},
		"org.mpris.MediaPlayer2.Player": {
			"PlaybackStatus": {Value: status, Writable: true},
			"Metadata":       {Value: metadata, Writable: true},
			"Position":       {Value: position, Writable: true},
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	if reply, err := conn.RequestName(mprisPrefix+name, dbus.NameFlagDoNotQueue); err != nil || reply != dbus.RequestNameReplyPrimaryOwner {
		t.Fatalf("bus name: %v %v", reply, err)
	}
	return properties
}

func song1() map[string]dbus.Variant {
	return map[string]dbus.Variant{
		"mpris:trackid": dbus.MakeVariant(dbus.ObjectPath("/track/1")),
		"xesam:title":   dbus.MakeVariant("Song (Official Video)"),
		"xesam:artist":  dbus.MakeVariant([]string{"Artist"}),
		"xesam:album":   dbus.MakeVariant("Album"),
		"mpris:length":  dbus.MakeVariant(int64(200_000_000)),
		"xesam:url":     dbus.MakeVariant("https://soundcloud.com/artist/song"),
	}
}

func TestMPRIS(t *testing.T) {
	player := startFakePlayer(t, "testplayer.instance42", song1(), "Playing", 30_000_000)
	startFakePlayer(t, "playerctld", song1(), "Playing", 0)
	media := newMedia()
	defer media.Close()
	tracks, err := media.Read(defaultSettings())
	if err != nil {
		t.Fatal(err)
	}
	if len(tracks) != 1 {
		t.Fatalf("one player, playerctld left out: %d", len(tracks))
	}
	track := tracks[0]
	if track.Artist != "Artist" || track.Title != "Song" || track.Album != "Album" || !track.IsPlaying || track.Duration != 200 || track.Position != 30 || !track.IsCurrent {
		t.Fatalf("read: %+v", track)
	}
	if track.Source.Name != "SoundCloud" || track.Player.Identity != "Test Player" || track.App != "testplayer.instance42" {
		t.Fatalf("source by the page: %+v %+v", track.Source, track.Player)
	}
	// A position that does not move keeps the time it was first seen at.
	first := track.UpdatedAt
	time.Sleep(20 * time.Millisecond)
	tracks, _ = media.Read(defaultSettings())
	if tracks[0].UpdatedAt != first {
		t.Fatal("the same position, the same time")
	}
	player.SetMust("org.mpris.MediaPlayer2.Player", "PlaybackStatus", "Stopped")
	if tracks, _ := media.Read(defaultSettings()); len(tracks) != 0 {
		t.Fatal("a stopped player shows nothing")
	}
}

// The whole way: a player on D-Bus, the engine, and Discord on a socket.
func TestEngineEndToEnd(t *testing.T) {
	startFakePlayer(t, "spotify", map[string]dbus.Variant{
		"xesam:title":  dbus.MakeVariant("driveway"),
		"xesam:artist": dbus.MakeVariant([]string{"LiL PEEP"}),
		"mpris:length": dbus.MakeVariant(uint64(180_000_000)),
		"mpris:artUrl": dbus.MakeVariant("https://i.scdn.co/image/ab67616d0000b273"),
	}, "Playing", 10_000_000)
	fake := startFakeDiscord(t, "")
	os.Remove(configPath())
	engine := newEngine()
	engine.Start()
	defer engine.Stop()
	hello := fake.next(t)
	if hello["client_id"] != "1563159215709749389" {
		t.Fatalf("Spotify's application: %v", hello)
	}
	var activity map[string]interface{}
	deadline := time.Now().Add(10 * time.Second)
	for activity == nil || activity["assets"] == nil {
		if time.Now().After(deadline) {
			t.Fatalf("no status with the cover: %v", activity)
		}
		args := fake.next(t)["args"].(map[string]interface{})
		activity, _ = args["activity"].(map[string]interface{})
	}
	if activity["details"] != "driveway" || activity["state"] != "LiL PEEP" {
		t.Fatalf("status: %v", activity)
	}
	if activity["assets"].(map[string]interface{})["large_image"] != "https://i.scdn.co/image/ab67616d0000b273" {
		t.Fatalf("cover: %v", activity["assets"])
	}
	timestamps := activity["timestamps"].(map[string]interface{})
	if end, start := timestamps["end"].(float64), timestamps["start"].(float64); end-start != 180000 {
		t.Fatalf("timestamps: %v", timestamps)
	}
	if !strings.Contains(engine.Status(), "driveway") {
		t.Fatalf("tray line: %s", engine.Status())
	}
}
