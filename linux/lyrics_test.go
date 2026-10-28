package main

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestLyricLines(t *testing.T) {
	lines := parseLRC("[ar:Kino]\n[00:05.00]Тёплое место\n[00:09.50][01:00.00]Группа крови\n[00:12.00]\n")
	if len(lines) != 4 {
		t.Fatalf("four timed lines: %d", len(lines))
	}
	for ms, want := range map[int64]string{1000: "", 6000: "Тёплое место", 10000: "Группа крови", 13000: "♪", 61000: "Группа крови"} {
		if got := lyricAt(lines, ms); got != want {
			t.Errorf("%d ms: %q, want %q", ms, got, want)
		}
	}
	s := defaultSettings()
	track := trackOf(resolveSource(PlayerID{Bus: "vlc"}, s), "Kino", "Группа крови")
	track.Album = "Album"
	if a := buildActivity(track, nil, s, "https://host/1", "Группа крови"); a["assets"].(object)["large_text"] != "Группа крови" {
		t.Errorf("under the lines, in place of the album: %v", a["assets"])
	}
	if a := buildActivity(track, nil, s, "", "Группа крови"); a["state"] != "Группа крови" {
		t.Errorf("no picture: in place of the second line: %v", a["state"])
	}
	track.IsPlaying = false
	if a := buildActivity(track, nil, s, "", "Группа крови"); a["state"] == "Группа крови" {
		t.Error("not on pause")
	}
}

func TestLyricsRetry(t *testing.T) {
	clock := time.Date(2026, 10, 28, 12, 0, 0, 0, time.UTC)
	l := newLyrics(nil)
	l.now = func() time.Time { return clock }
	lookups, down := 0, true
	l.lookUp = func(*Track) ([]lyricLine, bool) {
		lookups++
		if down {
			return nil, false
		}
		return parseLRC("[00:01.00]line"), true
	}
	track := song("Song")
	find := func() []lyricLine {
		l.Find(track)
		for _, job := range l.pending {
			<-job.done
		}
		return l.Find(track)
	}
	if find() != nil || lookups != 1 {
		t.Fatal("none while the site is down")
	}
	if find() != nil || lookups != 1 {
		t.Fatal("not asked again at once")
	}
	down = false
	clock = clock.Add(3 * time.Second)
	if lines := find(); len(lines) != 1 || lookups != 2 {
		t.Fatalf("found when the site is back: %v %d", lines, lookups)
	}
	if find(); lookups != 2 {
		t.Fatal("asked once")
	}
}

func TestLrclib(t *testing.T) {
	busy := true
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/get":
			http.NotFound(w, r)
		case "/search":
			if busy {
				http.Error(w, "busy", 503)
				return
			}
			fmt.Fprint(w, `[{"duration": 300, "syncedLyrics": "[00:01.00]wrong"}, {"duration": 201, "syncedLyrics": "[00:01.00]right"}, {"duration": 200, "syncedLyrics": null}]`)
		}
	}))
	defer server.Close()
	saved := lrclibBase
	lrclibBase = server.URL
	defer func() { lrclibBase = saved }()
	track := song("Song")
	track.Duration = 200
	if _, ok := fromLrclib(track); ok {
		t.Fatal("a busy site is asked again later")
	}
	busy = false
	lines, ok := fromLrclib(track)
	if !ok || len(lines) != 1 || lines[0].Text != "right" {
		t.Fatalf("the one close by length: %v %v", lines, ok)
	}
}
