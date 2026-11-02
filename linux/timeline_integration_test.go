package main

import (
	"math"
	"os"
	"testing"
	"time"
)

// Exercise the actual send gate through a fake MPRIS player and Discord IPC.
// No sleeps or network services: backdate only the previous send slot.
func TestTimelineDiscordUpdates(t *testing.T) {
	metadata := song1()
	delete(metadata, "mpris:length")
	player := startFakePlayer(t, "timelinefix", metadata, "Playing", 30_000_000)
	fake := startFakeDiscord(t, "")
	if err := os.WriteFile(configPath(), []byte(`{"coverSource":"none","showPaused":true,"checkUpdates":false}`), 0o600); err != nil {
		t.Fatal(err)
	}
	defer os.Remove(configPath())
	e := newEngine()
	defer e.media.Close()
	defer e.discord.Close()
	tick := func() {
		t.Helper()
		if err := e.tick(); err != "" {
			t.Fatal(err)
		}
	}
	activity := func() map[string]interface{} {
		t.Helper()
		return fake.next(t)["args"].(map[string]interface{})["activity"].(map[string]interface{})
	}
	allow := func() { e.sentAt = time.Now().Add(-4 * time.Second) }
	checkPosition := func(a map[string]interface{}, want float64) {
		t.Helper()
		start := a["timestamps"].(map[string]interface{})["start"].(float64)
		if got := float64(time.Now().UnixMilli()) - start; math.Abs(got-want*1000) > 2000 {
			t.Fatalf("position %g ms, want %g s", got, want)
		}
	}
	tick()
	fake.next(t) // handshake
	first := activity()
	if _, ok := first["timestamps"].(map[string]interface{})["end"]; ok {
		t.Fatal("unknown duration must not invent an end")
	}
	e.resendAt = nil

	metadata = song1()
	player.SetMust("org.mpris.MediaPlayer2.Player", "Metadata", metadata)
	allow()
	tick()
	times := activity()["timestamps"].(map[string]interface{})
	if times["end"].(float64)-times["start"].(float64) != 200000 {
		t.Fatal("late duration did not reach Discord")
	}

	player.SetMust("org.mpris.MediaPlayer2.Player", "Position", int64(60_000_000))
	allow()
	tick()
	checkPosition(activity(), 60)
	player.SetMust("org.mpris.MediaPlayer2.Player", "Position", int64(75_000_000))
	tick()
	player.SetMust("org.mpris.MediaPlayer2.Player", "Position", int64(90_000_000))
	tick()
	if len(fake.frames) != 0 {
		t.Fatal("updates must wait for the four-second slot")
	}
	allow()
	tick()
	checkPosition(activity(), 90)

	player.SetMust("org.mpris.MediaPlayer2.Player", "PlaybackStatus", "Paused")
	allow()
	tick()
	if _, ok := activity()["timestamps"]; ok {
		t.Fatal("pause sent a running clock")
	}
	player.SetMust("org.mpris.MediaPlayer2.Player", "PlaybackStatus", "Playing")
	allow()
	tick()
	checkPosition(activity(), 90)
	player.SetMust("org.mpris.MediaPlayer2.Player", "Position", int64(0))
	allow()
	tick()
	checkPosition(activity(), 0)
}
