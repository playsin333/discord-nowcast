package main

import (
	"net/url"
	"regexp"
	"strings"
)

type SourceInfo struct {
	// As shown in the status: "Яндекс Музыка", "SoundCloud", "Firefox".
	Name string
	// Search page the status button opens, the query appended; "": YouTube.
	Search string
	// The Discord application named after the service, so the status reads
	// "Listening to SoundCloud"; "": the general one from the settings.
	ClientID string
	// "Watching" (type 3) instead of "Listening to": YouTube, Twitch.
	IsVideo bool
	// A browser: the site may be found by the page's address.
	IsBrowser bool
}

type rule struct {
	pattern   *regexp.Regexp
	name      string
	search    string
	clientID  string
	isVideo   bool
	isBrowser bool
}

// Go's \b knows ASCII letters only: these stand for it around Cyrillic words.
const wordStart, wordEnd = `(?:^|[^\p{L}\p{N}_])`, `(?:$|[^\p{L}\p{N}_])`

func newRule(pattern, name, search, clientID string, isVideo bool) *rule {
	return &rule{pattern: regexp.MustCompile("(?i)" + pattern), name: name, search: search, clientID: clientID, isVideo: isVideo}
}

func browser(pattern, name string) *rule {
	r := newRule(pattern, name, "", "", false)
	r.isBrowser = true
	return r
}

// As in src/Sources.cs. Order matters: Yandex Music before Yandex Browser,
// YouTube Music before YouTube. Yandex Music has no search link of its own:
// with a button to music.yandex.ru Discord took the status but did not show
// it to anyone else. A player is matched by its name on D-Bus, its .desktop
// file and the name it gives itself.
var rules = []*rule{
	newRule(`yandex.*music|music.*yandex|яндекс.?музык`, t("Яндекс Музыка", "Yandex Music"), "", "1563159088265822392", false),
	newRule(`spotify`, "Spotify", "https://open.spotify.com/search/", "1563159215709749389", false),
	newRule(`soundcloud`, "SoundCloud", "https://soundcloud.com/search?q=", "1563159356940361779", false),
	newRule(`youtube.?music`, "YouTube Music", "https://music.youtube.com/search?q=", "1563162689163886642", false),
	newRule(`youtube`, "YouTube", "https://www.youtube.com/results?search_query=", "1563159477673263216", true),
	newRule(`twitch`, "Twitch", "https://www.twitch.tv/search?term=", "1563159932218646588", true),
	newRule(`telegram|tdesktop|ayugram|64gram|kotatogram`, "Telegram", "", "1563159812382920706", false),
	newRule(`vk.?music|vk музык|vkontakte|вконтакте|`+wordStart+`vk`+wordEnd+`|`+wordStart+`вк`+wordEnd, t("VK Музыка", "VK Music"), "https://vk.com/audio?q=", "1563159553619525723", false),
	newRule(`applemusic|apple music|itunes|cider`, "Apple Music", "https://music.apple.com/search?term=", "1563159657676021881", false),
	newRule(`aimp`, "AIMP", "", "1563516800287907893", false),
	newRule(`vlc`, "VLC", "", "", false),
	browser(`yandex`, t("Яндекс Браузер", "Yandex Browser")),
	browser(`chrome`, "Chrome"),
	browser(`chromium`, "Chromium"),
	browser(`msedge|microsoft.?edge`, "Edge"),
	browser(`firefox|librewolf|floorp|zen`, "Firefox"),
	browser(`opera`, "Opera"),
	browser(`vivaldi`, "Vivaldi"),
	browser(`brave`, "Brave"),
	browser(`thorium`, "Thorium"),
	// The KDE browser extension speaks for whichever browser it is in.
	browser(`plasma-browser-integration`, t("Браузер", "Browser")),
}

// Where a web page or its picture lives, and the service it is. The
// player's own words could name a site, so only addresses count here.
var sites = []struct {
	host, service string
}{
	{"music.youtube.com", "youtube music"},
	{"youtube.com", "youtube"},
	{"youtu.be", "youtube"},
	{"ytimg.com", "youtube"},
	{"soundcloud.com", "soundcloud"},
	{"sndcdn.com", "soundcloud"},
	{"spotify.com", "spotify"},
	{"scdn.co", "spotify"},
	{"music.yandex.", "yandex music"},
	{"vk.com", "vk music"},
	{"vk.ru", "vk music"},
	{"userapi.com", "vk music"},
	{"vkuser.net", "vk music"},
	{"twitch.tv", "twitch"},
	{"jtvnw.net", "twitch"},
	{"music.apple.com", "apple music"},
	{"mzstatic.com", "apple music"},
	{"web.telegram.org", "telegram"},
}

// What MPRIS says about the player: its bus name after
// org.mpris.MediaPlayer2., its .desktop file and the name it gives itself.
type PlayerID struct {
	Bus, DesktopEntry, Identity string
}

func (p PlayerID) text() string {
	return p.Bus + " " + p.DesktopEntry + " " + p.Identity
}

func resolveSource(player PlayerID, settings *Settings) *SourceInfo {
	name := ""
	// Your own names from config.json come first: {"chromium": "YouTube"}.
	for _, key := range settings.nameKeys() {
		if containsFold(player.Bus, key) || containsFold(player.DesktopEntry, key) || containsFold(player.Identity, key) {
			name = settings.Names[key]
		}
	}
	var r *rule
	if name != "" {
		r = match(name)
	}
	if r == nil {
		r = match(player.text())
	}
	if name == "" {
		if r != nil {
			name = r.name
		} else {
			name = fallbackName(player)
		}
	}
	return info(name, r)
}

func info(name string, r *rule) *SourceInfo {
	if r == nil {
		return &SourceInfo{Name: name}
	}
	return &SourceInfo{
		Name:     name,
		Search:   r.search,
		ClientID: r.clientID,
		IsVideo:  r.isVideo,
		// Only when the name is ours: a name from "names" stays as given.
		IsBrowser: r.isBrowser && r.name == name,
	}
}

// The service a browser plays from, by the page's address (xesam:url) or,
// when it gives none, by where the picture comes from; nil when it is none
// of those with an application of their own.
func siteSource(page, picture string) *SourceInfo {
	for _, address := range []string{page, picture} {
		parsed, err := url.Parse(address)
		if err != nil || (parsed.Scheme != "https" && parsed.Scheme != "http") {
			continue
		}
		host := strings.ToLower(parsed.Hostname())
		for _, site := range sites {
			if host == site.host || strings.HasSuffix(host, "."+site.host) || (strings.HasSuffix(site.host, ".") && strings.HasPrefix(host, site.host)) {
				if r := match(site.service); r != nil && r.clientID != "" {
					return info(r.name, r)
				}
			}
		}
	}
	return nil
}

func match(text string) *rule {
	for _, r := range rules {
		if r.pattern.MatchString(text) {
			return r
		}
	}
	return nil
}

var instanceSuffix = regexp.MustCompile(`\.instance[_0-9]*$`)

// The name the player gives itself ("Rhythmbox"), else its .desktop file's
// or bus name's last part: org.gnome.Lollypop -> Lollypop.
func fallbackName(player PlayerID) string {
	if name := strings.TrimSpace(player.Identity); name != "" {
		return name
	}
	for _, id := range []string{player.DesktopEntry, instanceSuffix.ReplaceAllString(player.Bus, "")} {
		parts := strings.Split(id, ".")
		if last := parts[len(parts)-1]; last != "" {
			return strings.ToUpper(last[:1]) + last[1:]
		}
	}
	return text.Music
}
