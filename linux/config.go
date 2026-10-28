package main

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

const (
	// The general Discord application, for players without one of their own.
	defaultClientID = "1562901782579912804"
	// The application of one's own status: "Listening to nowcast".
	ownClientID = "1565004020647727184"
	// How long one's own status may say it has been going: 100 hours.
	maxOwnMinutes = 100 * 60
)

func configPath() string { return filepath.Join(folder, "config.json") }

// config.json: the same keys as on Windows, plus ownPicture (the window that
// picks the picture there has no counterpart here). Written with the
// defaults on the first run; a broken file falls back to the defaults and
// says so in the log.
type Settings struct {
	ClientID string
	// What the member list shows after "Listening to": source, artist, title, app.
	InList     string
	ShowPaused bool
	// "player", "itunes" or "none", as on Windows.
	CoverSource  string
	CoverSearch  bool
	Buttons      bool
	HideAlbum    bool
	CheckUpdates bool
	Image        string
	Own          bool
	OwnType      string
	OwnTitle     string
	OwnLine2     string
	OwnMinutes   int
	// A JPEG, PNG, GIF or WebP file for one's own status; "" is own-picture next to config.json.
	OwnPicture string
	Hide       []string
	Names      map[string]string
	// The order "names" came in: a later name wins, as on Windows.
	nameOrder []string
}

func defaultSettings() *Settings {
	return &Settings{
		ClientID:     defaultClientID,
		InList:       "source",
		CoverSource:  "player",
		CoverSearch:  true,
		Buttons:      true,
		CheckUpdates: true,
		OwnType:      "listening",
		Names:        map[string]string{},
	}
}

func loadSettings() *Settings {
	s, err := readSettings()
	if err != nil {
		logf("config.json could not be read, using the defaults: %v", err)
	}
	return s
}

// The settings, written with the defaults when there is no file yet. On an
// error: the defaults, and the file is left as it is for its owner to mend.
func readSettings() (*Settings, error) {
	s := defaultSettings()
	os.MkdirAll(folder, 0o755)
	data, err := os.ReadFile(configPath())
	if os.IsNotExist(err) {
		return s, s.Save()
	}
	if err != nil {
		return s, err
	}
	if err := s.apply(data); err != nil {
		return defaultSettings(), err
	}
	return s, nil
}

func (s *Settings) apply(data []byte) error {
	data = bytes.TrimPrefix(data, []byte("\xef\xbb\xbf"))
	var values map[string]json.RawMessage
	if err := json.Unmarshal(data, &values); err != nil {
		return err
	}
	str := func(key string) (string, bool) {
		var v string
		raw, ok := values[key]
		if !ok || json.Unmarshal(raw, &v) != nil {
			return "", false
		}
		return v, true
	}
	boolean := func(key string, fallback bool) bool {
		var v bool
		raw, ok := values[key]
		if !ok || json.Unmarshal(raw, &v) != nil {
			return fallback
		}
		return v
	}
	if v, ok := str("clientId"); ok && strings.TrimSpace(v) != "" {
		s.ClientID = strings.TrimSpace(v)
	}
	if v, ok := str("inList"); ok {
		s.InList = v
	}
	s.ShowPaused = boolean("showPaused", s.ShowPaused)
	if v, _ := str("coverSource"); v == "player" || v == "itunes" || v == "none" {
		s.CoverSource = v
	} else if !boolean("covers", true) {
		// From before the choice: "covers": false.
		s.CoverSource = "none"
	}
	s.CoverSearch = boolean("coverSearch", s.CoverSearch)
	s.Buttons = boolean("buttons", s.Buttons)
	s.HideAlbum = boolean("hideAlbum", s.HideAlbum)
	s.CheckUpdates = boolean("checkUpdates", s.CheckUpdates)
	if v, ok := str("image"); ok {
		s.Image = v
	}
	s.Own = boolean("own", s.Own)
	if v, _ := str("ownType"); v == "listening" || v == "watching" || v == "playing" {
		s.OwnType = v
	}
	if v, ok := str("ownTitle"); ok {
		s.OwnTitle = v
	}
	if v, ok := str("ownLine2"); ok {
		s.OwnLine2 = v
	}
	var minutes float64
	if raw, ok := values["ownMinutes"]; ok && json.Unmarshal(raw, &minutes) == nil && minutes == float64(int(minutes)) {
		s.OwnMinutes = int(minutes)
	}
	if s.OwnMinutes < 0 {
		s.OwnMinutes = 0
	}
	if s.OwnMinutes > maxOwnMinutes {
		s.OwnMinutes = maxOwnMinutes
	}
	if v, ok := str("ownPicture"); ok {
		s.OwnPicture = v
	}
	var hide []interface{}
	if raw, ok := values["hide"]; ok && json.Unmarshal(raw, &hide) == nil {
		for _, word := range hide {
			if w, ok := word.(string); ok && w != "" {
				s.Hide = append(s.Hide, w)
			}
		}
	}
	if raw, ok := values["names"]; ok {
		s.readNames(raw)
	}
	return nil
}

// "names" in the order of the file: a later key that also matches wins.
func (s *Settings) readNames(raw json.RawMessage) {
	decoder := json.NewDecoder(bytes.NewReader(raw))
	if token, err := decoder.Token(); err != nil || token != json.Delim('{') {
		return
	}
	for decoder.More() {
		token, err := decoder.Token()
		if err != nil {
			return
		}
		key, _ := token.(string)
		var value interface{}
		if decoder.Decode(&value) != nil {
			return
		}
		if v, ok := value.(string); ok {
			if _, seen := s.Names[key]; !seen {
				s.nameOrder = append(s.nameOrder, key)
			}
			s.Names[key] = v
		}
	}
}

// Every setting, one per line, so the file stays easy to edit by hand.
func (s *Settings) Save() error {
	encode := func(v interface{}) string {
		var buffer bytes.Buffer
		encoder := json.NewEncoder(&buffer)
		encoder.SetEscapeHTML(false)
		encoder.Encode(v)
		return strings.TrimSpace(buffer.String())
	}
	hide := s.Hide
	if hide == nil {
		hide = []string{}
	}
	var names []string
	for _, key := range s.nameKeys() {
		names = append(names, encode(key)+": "+encode(s.Names[key]))
	}
	lines := []string{
		`  "clientId": ` + encode(s.ClientID),
		`  "inList": ` + encode(s.InList),
		`  "showPaused": ` + encode(s.ShowPaused),
		`  "coverSource": ` + encode(s.CoverSource),
		`  "coverSearch": ` + encode(s.CoverSearch),
		`  "buttons": ` + encode(s.Buttons),
		`  "hideAlbum": ` + encode(s.HideAlbum),
		`  "checkUpdates": ` + encode(s.CheckUpdates),
		`  "image": ` + encode(s.Image),
		`  "own": ` + encode(s.Own),
		`  "ownType": ` + encode(s.OwnType),
		`  "ownTitle": ` + encode(s.OwnTitle),
		`  "ownLine2": ` + encode(s.OwnLine2),
		`  "ownMinutes": ` + encode(s.OwnMinutes),
		`  "ownPicture": ` + encode(s.OwnPicture),
		`  "hide": ` + encode(hide),
		`  "names": {` + strings.Join(names, ", ") + `}`,
	}
	os.MkdirAll(folder, 0o755)
	// Written aside and moved in: the engine never reads half a file.
	temporary := configPath() + ".tmp"
	if err := os.WriteFile(temporary, []byte("{\n"+strings.Join(lines, ",\n")+"\n}\n"), 0o644); err != nil {
		return err
	}
	return os.Rename(temporary, configPath())
}

func (s *Settings) nameKeys() []string {
	keys := append([]string(nil), s.nameOrder...)
	seen := map[string]bool{}
	for _, key := range keys {
		seen[key] = true
	}
	for key := range s.Names {
		if !seen[key] {
			keys = append(keys, key)
		}
	}
	return keys
}

func (s *Settings) Covers() bool { return s.CoverSource != "none" }

// Whether one's own status is on and has a title to show, one without
// links and the plain words of casino ads and 18+.
func (s *Settings) ShowsOwn() bool {
	return s.Own && len([]rune(strings.TrimSpace(s.OwnTitle))) >= 2 && isClean(s.OwnTitle) && isClean(s.OwnLine2)
}

// The picture file of one's own status: ownPicture ("~/" for the home
// folder), or own-picture next to config.json.
func (s *Settings) OwnPicturePath() string {
	path := strings.TrimSpace(s.OwnPicture)
	if path == "" {
		return filepath.Join(folder, "own-picture")
	}
	path = strings.TrimPrefix(path, "file://")
	if strings.HasPrefix(path, "~/") {
		home, _ := os.UserHomeDir()
		path = filepath.Join(home, path[2:])
	}
	return path
}

// Links: "site.com", "t.me/x", "www…", "http…" (a known zone only: "ft.Drake"
// and "Mr. Smith" are not links). Words: as they are written in ads; a
// spaced or misspelt one gets through, a lazy one does not. As
// Settings.IsClean on Windows; Go's \w and \b know ASCII only, hence the
// letter classes.
var unclean = regexp.MustCompile(`(?i)https?:|www\.|[\p{L}\p{N}_-]\s?\.\s?(?:com|ru|net|org|io|me|gg|xyz|top|bet|casino|club|site|online|shop|store|pro|info|biz|cc|co|su|ua|kz|by|tv|link|app|dev|fun|win|vip|live|ly|to|рф)(?:$|[^\p{L}\p{N}_])|` +
	`казино|casino|ставк|букмек|1\s?win|1\s?xbet|pin-?up|vavada|вулкан|слоты|джекпот|промокод|` +
	`порн|porn|onlyfans|xxx|nsfw|18\+|секс|sex|голые|nudes`)

func isClean(text string) bool { return !unclean.MatchString(text) }

// The application one's own status goes out under, unless clientId names one of your own.
func (s *Settings) OwnClientIDFor() string {
	if s.ClientID != defaultClientID {
		return s.ClientID
	}
	return ownClientID
}

// The application a source goes out under: its own one ("Listening to
// SoundCloud"), unless clientId names an application of your own.
func (s *Settings) ClientIDFor(source *SourceInfo) string {
	if s.ClientID != defaultClientID || source.ClientID == "" {
		return s.ClientID
	}
	return source.ClientID
}

// Whether a word in "hide" matches the player: by its name on D-Bus or by the name shown.
func (s *Settings) Hides(app, source string) bool {
	for _, word := range s.Hide {
		if containsFold(app, word) || containsFold(source, word) {
			return true
		}
	}
	return false
}

func containsFold(text, part string) bool {
	return strings.Contains(strings.ToLower(text), strings.ToLower(part))
}
