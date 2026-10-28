package main

import (
	"bytes"
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

// Covers with a clock of its own, a fake player, hosts and iTunes.
type coverRig struct {
	c           *Covers
	clock       time.Time
	picture     *picture
	networkDown bool
	uploads     int32
	lookups     int32
	iTunes      string
	iTunesError bool
}

func newRig() *coverRig {
	r := &coverRig{clock: time.Date(2026, 10, 28, 12, 0, 0, 0, time.UTC)}
	r.c = newCovers(nil)
	r.c.now = func() time.Time { return r.clock }
	r.c.readPicture = func(*Track) (*picture, error) { return r.picture, nil }
	r.c.upload = func(data []byte) (Uploaded, error) {
		n := atomic.AddInt32(&r.uploads, 1)
		if r.networkDown {
			return Uploaded{}, errors.New("network down")
		}
		return Uploaded{URL: fmt.Sprintf("https://host/%d/%d", data[0], n), Keep: 3 * time.Hour}, nil
	}
	r.c.uploadOwn = r.c.upload
	r.c.lookUp = func(*Track) coverResult {
		atomic.AddInt32(&r.lookups, 1)
		if r.iTunesError {
			return coverResult{Failed: true}
		}
		if r.iTunes == "" {
			return coverResult{Answered: true}
		}
		return coverResult{Entry: &coverEntry{URL: r.iTunes, FromITunes: true, Renew: forever, Gone: forever}, Answered: true}
	}
	return r
}

// The cover after the lookups started by the first look have finished.
func (r *coverRig) cover(track *Track, s *Settings, seconds float64) string {
	r.clock = r.clock.Add(time.Duration(seconds * float64(time.Second)))
	r.c.Find(track, s)
	for _, job := range r.c.pending {
		<-job.done
	}
	return r.c.Find(track, s)
}

func song(title string) *Track {
	return &Track{App: "player", Source: &SourceInfo{Name: "Player"}, Artist: "Artist", Title: title, IsPlaying: true}
}

func TestCoverRetries(t *testing.T) {
	r := newRig()
	s := defaultSettings()
	s.CoverSearch = false
	track := song("Song")
	r.picture = &picture{Data: []byte{1}}
	r.networkDown = true
	if r.cover(track, s, 0) != "" {
		t.Fatal("no cover while the network is down")
	}
	tried := atomic.LoadInt32(&r.uploads)
	if r.cover(track, s, 1) != "" || atomic.LoadInt32(&r.uploads) != tried {
		t.Fatal("not tried again at once")
	}
	for i := 0; i < 20*60; i++ {
		r.cover(track, s, 1)
	}
	if n := atomic.LoadInt32(&r.uploads); n < 15 || n > 30 {
		t.Fatalf("tried about once a minute: %d", n)
	}
	r.networkDown = false
	if r.cover(track, s, 61) == "" {
		t.Fatal("the cover comes when the network is back")
	}
}

func TestCoverRenewed(t *testing.T) {
	r := newRig()
	s := defaultSettings()
	track := song("Song")
	r.picture = &picture{Data: []byte{7}}
	first := r.cover(track, s, 0)
	if first == "" || r.cover(track, s, 3600) != first {
		t.Fatalf("a cover, the same an hour later: %q", first)
	}
	renewed := r.cover(track, s, 1.5*3600)
	if renewed == "" || renewed == first {
		t.Fatalf("a new link before the host deletes the old one: %q", renewed)
	}
	r.networkDown = true
	if r.cover(track, s, 3*3600) != "" {
		t.Fatal("a deleted link is not sent")
	}
}

func TestCoverITunes(t *testing.T) {
	r := newRig()
	s := defaultSettings()
	track := song("Song")
	r.iTunes = "https://itunes/1"
	cover := ""
	for i := 0; i < 6 && cover == ""; i++ {
		cover = r.cover(track, s, 1)
	}
	if cover != "https://itunes/1" {
		t.Fatalf("iTunes stands in: %q", cover)
	}
	r.picture = &picture{Data: []byte{7}}
	if c := r.cover(track, s, 5); !strings.HasPrefix(c, "https://host/7") {
		t.Fatalf("the player's picture in its place: %q", c)
	}

	r = newRig()
	r.iTunes = "https://itunes/1"
	r.picture = &picture{Data: []byte{7}}
	s.CoverSource = "itunes"
	if r.cover(track, s, 0) != "https://itunes/1" || r.cover(track, s, 60) != "https://itunes/1" {
		t.Fatal("from iTunes first, and it stays")
	}

	r = newRig()
	r.iTunesError = true
	s = defaultSettings()
	for i := 0; i < 6; i++ {
		r.cover(track, s, 1)
	}
	if r.lookups != 1 {
		t.Fatalf("asked once: %d", r.lookups)
	}
	r.iTunesError = false
	r.iTunes = "https://itunes/2"
	if c := r.cover(track, s, 10); c != "https://itunes/2" || r.lookups != 2 {
		t.Fatalf("asked again after the error: %q %d", c, r.lookups)
	}
}

func TestCoverStaleAndShared(t *testing.T) {
	r := newRig()
	s := defaultSettings()
	first, second := song("One"), song("Two")
	r.picture = &picture{Data: []byte{1}}
	r.cover(first, s, 0)
	// The player still gives the first track's picture for the second.
	if c := r.cover(second, s, 1); c != "" {
		t.Fatalf("the previous track's picture is not taken at once: %q", c)
	}
	r.picture = &picture{Data: []byte{2}}
	if c := r.cover(second, s, 1); !strings.HasPrefix(c, "https://host/2") {
		t.Fatalf("the second track's own: %q", c)
	}

	r = newRig()
	a, b := song("One"), song("Two")
	a.Album, b.Album = "Album", "Album"
	r.picture = &picture{Data: []byte{3}}
	cover := r.cover(a, s, 0)
	if r.cover(b, s, 1) != cover || r.uploads != 1 {
		t.Fatalf("one album, one upload: %d", r.uploads)
	}
}

func TestCoverPublicLink(t *testing.T) {
	r := newRig()
	s := defaultSettings()
	r.picture = &picture{URL: "https://i.scdn.co/image/ab67"}
	if c := r.cover(song("Song"), s, 0); c != "https://i.scdn.co/image/ab67" || r.uploads != 0 {
		t.Fatalf("a public link goes as it is: %q, %d uploads", c, r.uploads)
	}
	picture, err := readPicture(&Track{ArtURL: "https://i.scdn.co/image/ab67"})
	if err != nil || picture.URL == "" {
		t.Fatal("Spotify's picture is public")
	}
}

func TestOwnPicture(t *testing.T) {
	r := newRig()
	path := filepath.Join(t.TempDir(), "own.png")
	if r.c.FindFile(path) != "" {
		t.Fatal("no file, no picture")
	}
	writePNG(t, path, 40, 40, color.NRGBA{200, 10, 10, 255})
	settle := func() {
		if job := r.c.files[path].job; job != nil {
			<-job.done
		}
	}
	r.networkDown = true
	r.c.FindFile(path)
	settle()
	if r.c.FindFile(path) != "" {
		t.Fatal("not uploaded while the network is down")
	}
	r.networkDown = false
	r.clock = r.clock.Add(3 * time.Second)
	r.c.FindFile(path)
	settle()
	first := r.c.FindFile(path)
	if !strings.HasPrefix(first, "https://host/") {
		t.Fatalf("uploaded: %q", first)
	}
	r.clock = r.clock.Add(150 * time.Minute)
	if r.c.FindFile(path) != first {
		t.Fatal("the old link while it is renewed")
	}
	settle()
	if renewed := r.c.FindFile(path); renewed == "" || renewed == first {
		t.Fatal("renewed")
	}
	os.Remove(path)
	if r.c.FindFile(path) != "" {
		t.Fatal("removed")
	}
}

func writePNG(t *testing.T, path string, width, height int, fill color.NRGBA) {
	picture := image.NewNRGBA(image.Rect(0, 0, width, height))
	for i := 0; i < len(picture.Pix); i += 4 {
		picture.Pix[i], picture.Pix[i+1], picture.Pix[i+2], picture.Pix[i+3] = fill.R, fill.G, fill.B, fill.A
	}
	var out bytes.Buffer
	png.Encode(&out, picture)
	if err := os.WriteFile(path, out.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestPreparePicture(t *testing.T) {
	// Spotify's way: the artwork, and the logo below it on a transparent strip.
	picture := image.NewNRGBA(image.Rect(0, 0, 640, 760))
	for y := 0; y < 640; y++ {
		for x := 0; x < 640; x++ {
			picture.SetNRGBA(x, y, color.NRGBA{30, 120, 200, 255})
		}
	}
	for y := 700; y < 740; y++ {
		for x := 20; x < 200; x++ {
			picture.SetNRGBA(x, y, color.NRGBA{30, 215, 96, 255})
		}
	}
	var in bytes.Buffer
	png.Encode(&in, picture)
	out := preparePicture(in.Bytes())
	if out[0] != 0xFF || out[1] != 0xD8 {
		t.Fatal("a JPEG")
	}
	cover, err := jpeg.Decode(bytes.NewReader(out))
	if err != nil || cover.Bounds().Dx() != 300 || cover.Bounds().Dy() != 300 {
		t.Fatalf("square, the strip cut off, scaled down: %v %v", cover.Bounds(), err)
	}
	small := image.NewNRGBA(image.Rect(0, 0, 100, 100))
	var smallPNG bytes.Buffer
	png.Encode(&smallPNG, small)
	if !bytes.Equal(preparePicture(smallPNG.Bytes()), smallPNG.Bytes()) {
		t.Fatal("a small one as it came")
	}
}

func TestPictureFromFile(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "wide.png")
	writePNG(t, path, 800, 400, color.NRGBA{1, 2, 3, 255})
	data, err := pictureFromFile(path)
	if err != nil || data[0] != 0xFF {
		t.Fatalf("a JPEG from an opaque PNG: %v", err)
	}
	made, _ := jpeg.Decode(bytes.NewReader(data))
	if made.Bounds().Dx() != 300 || made.Bounds().Dy() != 150 {
		t.Fatalf("scaled down: %v", made.Bounds())
	}
	writePNG(t, path, 100, 100, color.NRGBA{1, 2, 3, 0})
	if data, _ := pictureFromFile(path); !isPNG(data) {
		t.Fatal("a PNG keeps its transparency")
	}
	os.WriteFile(path, []byte("not a picture"), 0o644)
	if _, err := pictureFromFile(path); err == nil {
		t.Fatal("not a picture")
	}
}

func TestHosts(t *testing.T) {
	var got []byte
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/upload" || r.Header.Get("Content-Type") != "image/png" || !strings.HasPrefix(r.Header.Get("User-Agent"), "discord-nowcast/") {
			http.Error(w, "bad", 400)
			return
		}
		got = make([]byte, r.ContentLength)
		r.Body.Read(got)
		fmt.Fprintf(w, "%s/abc.png%s", "http://"+r.Host, map[bool]string{true: "?own", false: ""}[r.URL.Query().Get("kind") == "own"])
	}))
	defer server.Close()
	saved := ownHost
	ownHost = server.URL
	defer func() { ownHost = saved }()
	image := []byte{0x89, 0x50, 1, 2}
	url, err := toOwn(image, "")
	if err != nil || url != server.URL+"/abc.png" || !bytes.Equal(got, image) {
		t.Fatalf("our host: %q %v", url, err)
	}
	if own, err := uploadOwn(image); err != nil || !strings.HasSuffix(own.URL, "?own") || own.Keep != 48*time.Hour {
		t.Fatalf("own picture: %+v %v", own, err)
	}

	// The order: the quickest first, a failed one left out, all failed: the one due back first.
	now := time.Now()
	for _, h := range hosts {
		h.downUntil = time.Time{}
	}
	if order := hostOrder(now); len(order) != 2 || order[0].name != "nowcast" {
		t.Fatal("ours first")
	}
	hosts[0].downUntil = now.Add(time.Minute)
	if order := hostOrder(now); len(order) != 1 || order[0].name != "uguu.se" {
		t.Fatal("a failed host left out")
	}
	hosts[1].downUntil = now.Add(2 * time.Minute)
	if order := hostOrder(now); len(order) != 1 || order[0].name != "nowcast" {
		t.Fatal("all failed: the one due back first")
	}
	for _, h := range hosts {
		h.downUntil = time.Time{}
	}
}

func TestUploadHedge(t *testing.T) {
	saved := hosts
	defer func() { hosts = saved }()
	slow := &host{name: "slow", keep: time.Hour, took: 1, upload: func([]byte) (string, error) {
		time.Sleep(3 * time.Second)
		return "https://slow", nil
	}}
	quick := &host{name: "quick", keep: time.Hour, took: 2, upload: func([]byte) (string, error) { return "https://quick", nil }}
	hosts = []*host{slow, quick}
	hedge = 100 * time.Millisecond
	defer func() { hedge = 1500 * time.Millisecond }()
	begin := time.Now()
	got, err := upload([]byte{1})
	if err != nil || got.URL != "https://quick" || time.Since(begin) > time.Second {
		t.Fatalf("the next host starts alongside a slow one: %+v %v", got, err)
	}
}

func TestFallbackPicture(t *testing.T) {
	r := newRig()
	e := &Engine{covers: r.c}
	s := defaultSettings()
	s.Image = "https://example.com/own.png"
	s.FallbackPicture = filepath.Join(t.TempDir(), "fallback.png")
	writePNG(t, s.FallbackPicture, 40, 40, color.NRGBA{9, 9, 9, 255})
	track := song("Song")
	e.image(track, s)
	<-r.c.files[s.FallbackPicture].job.done
	if got := e.image(track, s); !strings.HasPrefix(got, "https://host/") {
		t.Fatalf("the picked picture, before the settings' link: %q", got)
	}
	r.picture = &picture{URL: "https://i.scdn.co/image/x"}
	if got := r.cover(track, s, 10); got != "https://i.scdn.co/image/x" || e.image(track, s) != got {
		t.Fatalf("then the cover: %q", got)
	}
	os.Remove(s.FallbackPicture)
	s.CoverSource = "none"
	if got := e.image(song("Other"), s); got != s.Image {
		t.Fatalf("removed: the settings' link: %q", got)
	}
}
