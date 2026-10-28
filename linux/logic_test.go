package main

import (
	"os"
	"strings"
	"testing"
	"time"
)

func TestMain(m *testing.M) {
	// The log and config.json of the tests go to a folder of their own.
	dir, _ := os.MkdirTemp("", "nowcast-test")
	folder = dir
	logToStderr = false
	ru = false
	code := m.Run()
	os.RemoveAll(dir)
	os.Exit(code)
}

func TestDefaults(t *testing.T) {
	s := defaultSettings()
	if !s.CheckUpdates || s.HideAlbum || s.CoverSource != "player" || !s.Buttons || s.InList != "source" || s.ClientID != defaultClientID {
		t.Fatalf("defaults: %+v", s)
	}
	for _, c := range []struct{ json, want string }{
		{`{"covers": false}`, "none"},
		{`{"covers": true}`, "player"},
		{`{"covers": false, "coverSource": "itunes"}`, "itunes"},
		{`{"coverSource": "elsewhere"}`, "player"},
	} {
		s := defaultSettings()
		if err := s.apply([]byte(c.json)); err != nil || s.CoverSource != c.want {
			t.Errorf("%s: %s, %v", c.json, s.CoverSource, err)
		}
	}
}

func TestSaveAndLoad(t *testing.T) {
	s := defaultSettings()
	s.OwnTitle = `Бит "в" FL`
	s.Hide = []string{"chromium"}
	s.Names["firefox"] = "YouTube"
	if err := s.Save(); err != nil {
		t.Fatal(err)
	}
	loaded := loadSettings()
	if loaded.OwnTitle != s.OwnTitle || len(loaded.Hide) != 1 || loaded.Names["firefox"] != "YouTube" {
		t.Fatalf("read back: %+v", loaded)
	}
	os.WriteFile(configPath(), []byte("{broken"), 0o644)
	if loadSettings().ClientID != defaultClientID {
		t.Fatal("a broken file is the defaults")
	}
	os.Remove(configPath())
}

func TestOwnSettings(t *testing.T) {
	s := defaultSettings()
	if s.Own || s.ShowsOwn() || s.OwnType != "listening" {
		t.Fatal("off at first, listening")
	}
	s.apply([]byte(`{"ownType": "dancing"}`))
	if s.OwnType != "listening" {
		t.Fatal("an unknown type is the default")
	}
	s.apply([]byte(`{"own": true, "ownTitle": " ", "ownMinutes": 999999, "ownType": "playing"}`))
	if !s.Own || s.ShowsOwn() || s.OwnMinutes != maxOwnMinutes || s.OwnType != "playing" {
		t.Fatalf("no title, nothing shown; at most 100 hours: %+v", s)
	}
	s.OwnTitle = "Бит"
	if !s.ShowsOwn() || s.OwnClientIDFor() != ownClientID {
		t.Fatal("shown with a title, under nowcast's application")
	}
	s.ClientID = "123"
	if s.OwnClientIDFor() != "123" {
		t.Fatal("one's own application wins")
	}
	for _, bad := range []string{"заходи на casino-x.com", "t.me/bonus", "bonus . com", "www.site", "https://x", "Лучшее КАЗИНО", "1xbet бонус", "Промокод 777", "porn", "18+ видео", "сайт.рф"} {
		if isClean(bad) {
			t.Errorf("not shown: %s", bad)
		}
	}
	for _, good := range []string{"Делаю бит", "Слушаю Dean Martin", "v1.2 вышла", "т.е. всё", "Ст. Петербург", "Mr. Smith", "ft.Drake", "feat. Rihanna"} {
		if !isClean(good) {
			t.Errorf("shown: %s", good)
		}
	}
	s.OwnTitle, s.OwnLine2 = "Ок", "ставки тут"
	if s.ShowsOwn() {
		t.Error("the second line too")
	}
	home, _ := os.UserHomeDir()
	s.OwnPicture = "~/pic.png"
	if s.OwnPicturePath() != home+"/pic.png" {
		t.Errorf("home folder: %s", s.OwnPicturePath())
	}
}

func TestSources(t *testing.T) {
	s := defaultSettings()
	check := func(id PlayerID, name, clientID string, isBrowser bool) {
		t.Helper()
		got := resolveSource(id, s)
		if got.Name != name || got.ClientID != clientID || got.IsBrowser != isBrowser {
			t.Errorf("%+v: %+v", id, got)
		}
	}
	check(PlayerID{Bus: "spotify", Identity: "Spotify"}, "Spotify", "1563159215709749389", false)
	check(PlayerID{Bus: "firefox.instance_1_42", DesktopEntry: "firefox", Identity: "Mozilla Firefox"}, "Firefox", "", true)
	check(PlayerID{Bus: "chromium.instance4242", DesktopEntry: "google-chrome", Identity: "Google Chrome"}, "Chrome", "", true)
	check(PlayerID{Bus: "chromium.instance4242", DesktopEntry: "chromium", Identity: "Chromium"}, "Chromium", "", true)
	check(PlayerID{Bus: "telegram", DesktopEntry: "org.telegram.desktop", Identity: "Telegram Desktop"}, "Telegram", "1563159812382920706", false)
	check(PlayerID{Bus: "yandex-music", Identity: "Yandex Music"}, "Yandex Music", "1563159088265822392", false)
	check(PlayerID{Bus: "vlc", Identity: "VLC media player"}, "VLC", "", false)
	check(PlayerID{Bus: "rhythmbox", Identity: "Rhythmbox"}, "Rhythmbox", "", false)
	check(PlayerID{Bus: "org.gnome.Lollypop"}, "Lollypop", "", false)
	s.Names["firefox"] = "YouTube"
	got := resolveSource(PlayerID{Bus: "firefox.instance_1_42"}, s)
	if got.Name != "YouTube" || !got.IsVideo || got.IsBrowser {
		t.Errorf("own name: %+v", got)
	}

	for _, c := range []struct{ page, picture, name string }{
		{"https://www.youtube.com/watch?v=x", "", "YouTube"},
		{"https://music.youtube.com/watch?v=x", "", "YouTube Music"},
		{"https://soundcloud.com/a/b", "", "SoundCloud"},
		{"https://music.yandex.ru/album/1", "", "Yandex Music"},
		{"https://vk.com/audio", "", "VK Music"},
		{"", "https://i.ytimg.com/vi/x/hq.jpg", "YouTube"},
		{"https://example.com/", "", ""},
		{"file:///home/me/song.mp3", "", ""},
	} {
		site := siteSource(c.page, c.picture)
		name := ""
		if site != nil {
			name = site.Name
		}
		if name != c.name {
			t.Errorf("%s %s: %q", c.page, c.picture, name)
		}
	}
}

func TestRepair(t *testing.T) {
	track := &Track{Source: &SourceInfo{Name: "Chrome"}, Title: "Artist - Song (Official Video)"}
	repair(track)
	if track.Artist != "Artist" || track.Title != "Song" {
		t.Errorf("split and cleaned: %q / %q", track.Artist, track.Title)
	}
	youtube := siteSource("https://www.youtube.com/watch", "")
	topic := &Track{Source: youtube, Artist: "Artist - Topic", Title: "Song"}
	repair(topic)
	if topic.Artist != "Artist" || topic.Source.IsVideo || !youtube.IsVideo {
		t.Errorf("a Topic channel is music, the rule untouched: %+v %+v", topic, youtube)
	}
}

func trackOf(source *SourceInfo, artist, title string) *Track {
	return &Track{App: "player", Source: source, Artist: artist, Title: title, IsPlaying: true}
}

func TestActivityLines(t *testing.T) {
	s := defaultSettings()
	spotify := resolveSource(PlayerID{Bus: "spotify"}, s)
	a := buildActivity(trackOf(spotify, "LiL PEEP", "driveway"), nil, s, "")
	if a["details"] != "driveway" || a["state"] != "LiL PEEP" || a["status_display_type"] != 0 || a["type"] != 2 {
		t.Errorf("a service's own application: %v", a)
	}
	vlc := resolveSource(PlayerID{Bus: "vlc"}, s)
	a = buildActivity(trackOf(vlc, "Artist", "Song"), nil, s, "")
	if a["details"] != "Artist — Song" || a["state"] != "VLC" || a["status_display_type"] != 1 {
		t.Errorf("the general application: %v", a)
	}
	if _, ok := a["assets"]; ok {
		t.Error("no cover, no assets")
	}
	track := trackOf(vlc, "Artist", "Song")
	track.Album = "Album"
	a = buildActivity(track, nil, s, "https://host/1")
	if a["assets"].(object)["large_text"] != "Album" {
		t.Errorf("the album: %v", a["assets"])
	}
	s.HideAlbum = true
	if _, ok := buildActivity(track, nil, s, "https://host/1")["assets"].(object)["large_text"]; ok {
		t.Error("hidden album")
	}
	track.IsPlaying = false
	if a := buildActivity(track, nil, s, ""); a["state"] != "VLC · paused" {
		t.Errorf("paused: %v", a["state"])
	}
	youtube := siteSource("https://www.youtube.com/", "")
	if a := buildActivity(trackOf(youtube, "Channel", "Video"), nil, s, ""); a["type"] != 3 {
		t.Error("YouTube is Watching")
	}
	start := int64(1000)
	track = trackOf(vlc, "A", "B")
	track.Duration = 200
	timestamps := buildActivity(track, &start, s, "")["timestamps"].(object)
	if timestamps["start"] != int64(1000) || timestamps["end"] != int64(201000) {
		t.Errorf("timestamps: %v", timestamps)
	}
	track.Duration = 0
	if _, ok := buildActivity(track, &start, s, "")["timestamps"].(object)["end"]; ok {
		t.Error("no length: start only")
	}
}

func TestButton(t *testing.T) {
	s := defaultSettings()
	yandex := resolveSource(PlayerID{Bus: "yandex-music"}, s)
	link := makeButton(trackOf(yandex, "Artist", "Song"))["url"].(string)
	if !strings.HasPrefix(link, "https://www.youtube.com/") || strings.Contains(strings.ToLower(link), "yandex") {
		t.Errorf("Yandex Music's button goes to YouTube: %s", link)
	}
	soundCloud := resolveSource(PlayerID{Bus: "soundcloud"}, s)
	if b := makeButton(trackOf(soundCloud, "A", "B")); b["url"] != "https://soundcloud.com/search?q=A%20B" || b["label"] != "Listen on SoundCloud" {
		t.Errorf("SoundCloud's own search: %v", b)
	}
	long := trackOf(yandex, strings.Repeat("Исполнитель ", 20), strings.Repeat("Очень длинное название трека ", 20))
	a := buildActivity(long, nil, s, "")
	link = a["buttons"].([]object)[0]["url"].(string)
	if len(link) > 512 {
		t.Errorf("button link within 512: %d", len(link))
	}
	if units(a["details"].(string)) > 128 {
		t.Error("title within 128")
	}
	emoji := trackOf(yandex, "A", strings.Repeat("a", 126)+"😀😀😀")
	details := buildActivity(emoji, nil, s, "")["details"].(string)
	if units(details) > 128 || !strings.HasSuffix(details, "a…") {
		t.Errorf("no half of an emoji: %q (%d)", details, units(details))
	}
	if limit("x", 128) != "x⠀" {
		t.Error("at least two characters")
	}
	if escapeData("A b/ц~") != "A%20b%2F%D1%86~" {
		t.Errorf("escape: %s", escapeData("A b/ц~"))
	}
}

func TestOwnActivity(t *testing.T) {
	a := ownActivity("listening", "Делаю бит", "в FL Studio", 1000, "https://host/own")
	if a["type"] != 2 || a["status_display_type"] != 0 || a["details"] != "Делаю бит" || a["state"] != "в FL Studio" {
		t.Errorf("listening, the two lines: %v", a)
	}
	if ownActivity("watching", "Ok", "", 1000, "")["type"] != 3 || ownActivity("playing", "Ok", "", 1000, "")["type"] != 0 {
		t.Error("watching, playing")
	}
	if _, ok := ownActivity("listening", "Ok", " ", 1000, "")["state"]; ok {
		t.Error("no second line, none sent")
	}
	assets := a["assets"].(object)
	if assets["large_image"] != "https://host/own" || assets["large_url"] != discordInvite || a["details_url"] != discordInvite {
		t.Errorf("the picture, and a click opens the server: %v", a)
	}
	if a["timestamps"].(object)["start"] != int64(1000) {
		t.Error("the start")
	}
	if _, ok := a["buttons"]; ok {
		t.Error("no button")
	}
}

func TestStartFor(t *testing.T) {
	e := &Engine{}
	now := time.Now().UnixMilli()
	track := &Track{App: "p", Source: &SourceInfo{Name: "P"}, Title: "Song", IsPlaying: true, Position: 30, Duration: 200, UpdatedAt: now}
	first := *e.startFor(track, now)
	second := *e.startFor(track, now+1000)
	if first != second {
		t.Error("the same report, the same start a second later")
	}
	if first != now-30000 {
		t.Errorf("started 30 s ago: %d", now-first)
	}
	track.Position = 120
	seek := *e.startFor(track, now+2000)
	if first-seek < 3000 {
		t.Error("a new position is a seek")
	}
	none := &Track{App: "p", Title: "x", IsPlaying: true}
	if e.startFor(none, now) != nil {
		t.Error("no length, no position: no time")
	}
	// A player that keeps one position while it plays: the start does not creep.
	frozen := &Track{App: "q", Title: "y", IsPlaying: true, Position: 10, Duration: 100, UpdatedAt: now - 5000}
	a := *e.startFor(frozen, now)
	b := *e.startFor(frozen, now+4000)
	if a != b || a != now-15000 {
		t.Errorf("frozen position: %d %d", now-a, now-b)
	}
}

func TestPick(t *testing.T) {
	e := &Engine{settings: defaultSettings()}
	s := e.settings
	vlc := resolveSource(PlayerID{Bus: "vlc"}, s)
	paused := &Track{App: "a", Source: vlc, Title: "paused"}
	video := &Track{App: "b", Source: vlc, Title: "feed", IsPlaying: true}
	song := &Track{App: "c", Source: vlc, Title: "song", IsPlaying: true, Duration: 100}
	if e.pick([]*Track{paused, video, song}) != song {
		t.Error("a track with a length first")
	}
	if e.pick([]*Track{paused}) != nil {
		t.Error("nothing on pause")
	}
	s.ShowPaused = true
	if e.pick([]*Track{paused}) != paused {
		t.Error("showPaused")
	}
	s.Hide = []string{"VLC"}
	if e.pick([]*Track{song}) != nil {
		t.Error("hidden by the name shown")
	}
}

func TestVersions(t *testing.T) {
	if !newer("1.10.0", "1.9.3") || newer("1.5.0", "1.5.0") || newer("1.5.0", "dev") || !newer("1.5.1", "1.5") {
		t.Error("version order")
	}
}
