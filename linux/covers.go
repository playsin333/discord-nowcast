package main

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
)

// The cover, as src/Covers.cs on Windows: the picture the player gives
// (mpris:artUrl). Discord only shows pictures by URL, so a file goes to a
// host; a public link from a known image server goes as it is. Only when
// the player gives no picture, or no host takes it, the album art from the
// iTunes search is used, if the same artist and title are found there; the
// player's picture replaces it when it comes.

// A cover in hand: its link, where it came from, when to upload the
// picture again (the host will delete it) and when the link is gone.
type coverEntry struct {
	URL        string
	FromITunes bool
	Renew      time.Time
	Gone       time.Time
}

// What a lookup in the background brought: a cover, or that it failed and
// is to be tried again; Answered: iTunes gave its answer, found or not.
type coverResult struct {
	Entry    *coverEntry
	Print    string
	Failed   bool
	Answered bool
}

// The player's picture: the bytes, or a link Discord can take as it is.
type picture struct {
	Data []byte
	URL  string
}

func (p *picture) print() string {
	if p.URL != "" {
		return "url:" + p.URL
	}
	return fingerprint(p.Data)
}

type coverJob struct {
	done   chan struct{}
	result coverResult
}

func (j *coverJob) finished() bool {
	select {
	case <-j.done:
		return true
	default:
		return false
	}
}

type seenPicture struct {
	key, print, album string
}

// How many times, a second apart, to look for the player's picture before
// turning to iTunes: browsers give it a few seconds after the track starts.
const pictureLooks = 5

// After that, and while an iTunes cover stands in, the player is asked this often.
const lookEvery = 5 * time.Second

var forever = time.Date(9999, 1, 1, 0, 0, 0, 0, time.UTC)

type Covers struct {
	cache map[string]*coverEntry
	// Uploads by the picture's fingerprint: a cover shared by an album's tracks goes up once.
	byPrint map[string]*coverEntry
	// When a track may be looked at again, and its failures in a row.
	retryAt  map[string]time.Time
	failures map[string]int
	// Tracks iTunes has answered for: one search per track. A search that
	// failed is not an answer and is tried again.
	searched map[string]bool
	// What was last taken from each player: right after a track changes a
	// player can still give the previous track's picture.
	lastByApp  map[string]seenPicture
	staleLooks map[string]int
	noPicture  map[string]int
	pending    map[string]*coverJob
	current    string
	mode       string

	// Pictures of one's own from files, by path: they outlive Reset.
	files map[string]*fileState

	// Stand-ins for the tests: the clock, the player's picture, the uploads and iTunes.
	now         func() time.Time
	readPicture func(*Track) (*picture, error)
	upload      func([]byte) (Uploaded, error)
	uploadOwn   func([]byte) (Uploaded, error)
	lookUp      func(*Track) coverResult
	// Told when a lookup is done, so that the status goes out at once.
	ready func()
}

func newCovers(ready func()) *Covers {
	c := &Covers{
		now:         time.Now,
		readPicture: readPicture,
		upload:      func(image []byte) (Uploaded, error) { return upload(preparePicture(image)) },
		uploadOwn:   uploadOwn,
		lookUp:      fromITunes,
		ready:       ready,
		files:       map[string]*fileState{},
	}
	c.Reset()
	return c
}

// Everything forgotten: the settings changed, or the books grew too long.
func (c *Covers) Reset() {
	c.cache = map[string]*coverEntry{}
	c.byPrint = map[string]*coverEntry{}
	c.retryAt = map[string]time.Time{}
	c.failures = map[string]int{}
	c.searched = map[string]bool{}
	c.lastByApp = map[string]seenPicture{}
	c.staleLooks = map[string]int{}
	c.noPicture = map[string]int{}
	c.pending = map[string]*coverJob{}
	c.current = ""
}

// The URL of the track's cover, or "" when there is none yet; the status
// goes without it and gets it when the lookup is done. The settings say
// where from first: the player's picture or iTunes.
func (c *Covers) Find(track *Track, settings *Settings) string {
	iTunesFirst := settings.CoverSource == "itunes"
	search := iTunesFirst || settings.CoverSearch
	key := track.Key()
	now := c.now()

	// Covers found with other settings are not what these ask for.
	asked := fmt.Sprintf("%s|%v", settings.CoverSource, settings.CoverSearch)
	if asked != c.mode {
		c.Reset()
		c.mode = asked
	}
	if len(c.cache)+len(c.retryAt) > 1000 {
		c.Reset()
	}
	// A track played again gets its tries back: the network may be up by now.
	if key != c.current {
		c.current = key
		delete(c.retryAt, key)
		delete(c.failures, key)
		delete(c.noPicture, key)
	}

	if job, ok := c.pending[key]; ok {
		if !job.finished() {
			return c.usable(key, now)
		}
		delete(c.pending, key)
		c.take(key, job.result, iTunesFirst, now)
	}

	entry := c.cache[key]
	if entry != nil && !now.Before(entry.Gone) {
		delete(c.cache, key)
		entry = nil
	}
	// In hand and good: an iTunes cover in "from the player" only stands in.
	better := entry != nil && (!now.Before(entry.Renew) || (entry.FromITunes && !iTunesFirst))
	if entry != nil && !better {
		return entry.URL
	}
	if at, ok := c.retryAt[key]; ok && now.Before(at) {
		return c.usable(key, now)
	}

	image, err := c.readPicture(track)
	if err != nil {
		logf("Cover not read: %v", err)
		image = nil
	}

	// iTunes first: one search, and the player's picture when the track is
	// not there. After that it goes on as below, without iTunes.
	if iTunesFirst && entry == nil && search && !c.searched[key] && iTunesOpen(now) {
		var fallback *picture
		if image != nil && !c.isStale(track, image.print()) {
			fallback = image
		}
		c.start(key, func() coverResult {
			found := c.lookUp(track)
			if found.Entry != nil || fallback == nil {
				return found
			}
			uploaded := c.fromPlayer(track, fallback, "", false)
			uploaded.Answered = found.Answered
			return uploaded
		})
		return ""
	}

	if image != nil {
		print := image.print()
		if c.isStale(track, print) {
			c.retryAt[key] = now.Add(time.Second)
			return c.usable(key, now)
		}
		c.lastByApp[track.App] = seenPicture{key: key, print: print, album: track.Album}
		if known, ok := c.byPrint[print]; ok && now.Before(known.Renew) {
			c.cache[key] = known
			return known.URL
		}
		// When no host takes it, iTunes may stand in, once it has not answered yet.
		orITunes := entry == nil && search && !c.searched[key] && iTunesOpen(now)
		c.start(key, func() coverResult { return c.fromPlayer(track, image, print, orITunes) })
		return c.usable(key, now)
	}

	// No picture now: a cover that needs renewing stays until it is gone,
	// and the player is asked again in a few seconds.
	if entry != nil {
		c.retryAt[key] = now.Add(lookEvery)
		return entry.URL
	}

	// The player has no picture, or has not given it yet. Look again every
	// second for a few seconds before turning to iTunes, whose art can
	// belong to another release.
	c.noPicture[key]++
	looks := c.noPicture[key]
	if search && looks >= pictureLooks && !c.searched[key] && iTunesOpen(now) {
		c.start(key, func() coverResult { return c.lookUp(track) })
		return ""
	}
	if looks < pictureLooks {
		c.retryAt[key] = now.Add(time.Second)
	} else {
		c.retryAt[key] = now.Add(lookEvery)
	}
	return ""
}

// What a finished lookup brought, into the books.
func (c *Covers) take(key string, result coverResult, iTunesFirst bool, now time.Time) {
	if result.Answered {
		c.searched[key] = true
	}
	switch {
	case result.Entry != nil:
		c.cache[key] = result.Entry
		if result.Print != "" {
			c.byPrint[result.Print] = result.Entry
		}
		delete(c.failures, key)
		delete(c.retryAt, key)
		// The player's own picture is still looked for.
		if result.Entry.FromITunes && !iTunesFirst {
			c.retryAt[key] = now.Add(lookEvery)
		}
	case result.Failed:
		// 2, 4, 8, 16, 32 s, then once a minute while the track plays.
		c.failures[key]++
		c.retryAt[key] = now.Add(backoff(c.failures[key]))
	default:
		c.retryAt[key] = now.Add(lookEvery)
	}
}

func backoff(count int) time.Duration {
	return time.Duration(math.Min(60, math.Pow(2, float64(count)))) * time.Second
}

// The link in hand while a better one is on its way, or "".
func (c *Covers) usable(key string, now time.Time) string {
	if entry, ok := c.cache[key]; ok && now.Before(entry.Gone) {
		return entry.URL
	}
	return ""
}

// Runs a lookup in the background and tells the engine when it is done.
func (c *Covers) start(key string, work func() coverResult) {
	job := &coverJob{done: make(chan struct{})}
	c.pending[key] = job
	c.run(job, work)
}

func (c *Covers) run(job *coverJob, work func() coverResult) {
	go func() {
		defer func() {
			if failure := recover(); failure != nil {
				logf("Cover lookup failed: %v", failure)
				job.result = coverResult{Failed: true}
			}
			close(job.done)
			if c.ready != nil {
				c.ready()
			}
		}()
		job.result = work()
	}()
}

// In the background: the player's picture, uploaded (or a public link as it
// is). orITunes: when no host takes it, the album art from iTunes instead.
func (c *Covers) fromPlayer(track *Track, image *picture, print string, orITunes bool) coverResult {
	if image.URL != "" {
		logf("Cover from the player: %s %s", describe(track), image.URL)
		return coverResult{Entry: &coverEntry{URL: image.URL, Renew: forever, Gone: forever}, Print: print}
	}
	begin := c.now()
	uploaded, err := c.upload(image.Data)
	if err == nil {
		entry := c.uploaded(uploaded)
		logf("Cover from the player: %s %s (%d ms)", describe(track), entry.URL, c.now().Sub(begin).Milliseconds())
		return coverResult{Entry: entry, Print: print}
	}
	logf("Cover not uploaded, will try again: %v", err)
	if !orITunes {
		return coverResult{Failed: true}
	}
	found := c.lookUp(track)
	found.Failed = true
	return found
}

// An uploaded picture's link, uploaded again a little before the host deletes it.
func (c *Covers) uploaded(u Uploaded) *coverEntry {
	got := c.now()
	return &coverEntry{URL: u.URL, Renew: got.Add(u.Keep * 8 / 10), Gone: got.Add(u.Keep - 10*time.Minute)}
}

// A picture of one's own from a file: one's own status's, or the one shown
// when a track has no cover. Uploaded like a cover, tried again the same way
// and renewed before the host deletes it. The file is told apart by its
// time and size, not read every round.
type fileState struct {
	print    string
	entry    *coverEntry
	job      *coverJob
	retryAt  time.Time
	failures int
}

func (c *Covers) FindFile(path string) string {
	now := c.now()
	state := c.files[path]
	if state == nil {
		state = &fileState{}
		c.files[path] = state
	}
	print := ""
	if info, err := os.Stat(path); err == nil && info.Mode().IsRegular() {
		print = fmt.Sprintf("%d:%d", info.ModTime().UnixNano(), info.Size())
	}
	if print != state.print {
		*state = fileState{print: print, job: state.job}
	}
	if print == "" {
		return ""
	}
	if state.job != nil {
		if !state.job.finished() {
			return state.usable(now)
		}
		result := state.job.result
		state.job = nil
		// A picture picked since is not this one.
		if result.Print == print {
			if result.Entry != nil {
				state.entry = result.Entry
				state.failures = 0
			} else {
				state.failures++
				state.retryAt = now.Add(backoff(state.failures))
			}
		}
	}
	if state.entry != nil && now.Before(state.entry.Renew) {
		return state.entry.URL
	}
	if now.Before(state.retryAt) {
		return state.usable(now)
	}
	job := &coverJob{done: make(chan struct{})}
	state.job = job
	name := filepath.Base(path)
	c.run(job, func() coverResult {
		data, err := pictureFromFile(path)
		if err == nil {
			var uploaded Uploaded
			uploaded, err = c.uploadOwn(data)
			if err == nil {
				entry := c.uploaded(uploaded)
				logf("Picture %s: %s", name, entry.URL)
				return coverResult{Entry: entry, Print: print}
			}
		}
		logf("Picture %s not uploaded, will try again: %v", name, err)
		return coverResult{Failed: true, Print: print}
	})
	return state.usable(now)
}

func (s *fileState) usable(now time.Time) string {
	if s.entry != nil && now.Before(s.entry.Gone) {
		return s.entry.URL
	}
	return ""
}

// Whether the picture is still the previous track's: the title has changed
// but the player has not replaced the picture yet. Tracks of one album
// share a picture and are taken at once; for others the picture is read
// again a few times before the two are believed to be the same.
func (c *Covers) isStale(track *Track, print string) bool {
	before, ok := c.lastByApp[track.App]
	if !ok || before.key == track.Key() || before.print != print {
		return false
	}
	if track.Album != "" && strings.EqualFold(before.album, track.Album) {
		return false
	}
	c.staleLooks[track.Key()]++
	if c.staleLooks[track.Key()] > 4 {
		return false
	}
	logf("The picture of %s is still the previous track's, looking again", describe(track))
	return true
}

// Image servers whose links stay up and are open to anyone: Discord takes
// those as they are, nothing to upload.
var publicPictures = regexp.MustCompile(`^https://(?:i\.scdn\.co|i\.ytimg\.com|i[0-9]?\.sndcdn\.com|lh[0-9]\.googleusercontent\.com|yt[0-9]\.ggpht\.com|avatars\.yandex\.net|is[0-9]-ssl\.mzstatic\.com|static-cdn\.jtvnw\.net)/`)

const maxPicture = 8 << 20

// The picture the player gives in mpris:artUrl; nil when there is none.
func readPicture(track *Track) (*picture, error) {
	art := strings.TrimSpace(track.ArtURL)
	switch {
	case art == "":
		return nil, nil
	case publicPictures.MatchString(art) && len(art) <= 256:
		return &picture{URL: art}, nil
	case strings.HasPrefix(art, "file://"):
		parsed, err := url.Parse(art)
		if err != nil {
			return nil, err
		}
		file, err := os.Open(parsed.Path)
		if err != nil {
			return nil, err
		}
		defer file.Close()
		data, err := io.ReadAll(io.LimitReader(file, maxPicture))
		if len(data) <= 1 {
			return nil, err
		}
		return &picture{Data: data}, err
	case strings.HasPrefix(art, "data:"):
		comma := strings.Index(art, ",")
		if comma < 0 || !strings.Contains(art[:comma], ";base64") {
			return nil, errors.New("a data: picture not in base64")
		}
		data, err := base64.StdEncoding.DecodeString(art[comma+1:])
		return &picture{Data: data}, err
	case strings.HasPrefix(art, "https://") || strings.HasPrefix(art, "http://"):
		data, err := download(art)
		if err != nil {
			return nil, err
		}
		return &picture{Data: data}, nil
	}
	return nil, fmt.Errorf("unknown picture address: %s", short(art))
}

// Pictures behind other links are fetched once per address: a player's
// link does not change its picture.
var (
	downloads     = map[string][]byte{}
	downloadsLock sync.Mutex
)

func download(address string) ([]byte, error) {
	downloadsLock.Lock()
	data, ok := downloads[address]
	downloadsLock.Unlock()
	if ok {
		return data, nil
	}
	request, err := newRequest("GET", address, nil)
	if err != nil {
		return nil, err
	}
	response, err := httpClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, fmt.Errorf("%s: %d", short(address), response.StatusCode)
	}
	data, err = io.ReadAll(io.LimitReader(response.Body, maxPicture))
	if err != nil {
		return nil, err
	}
	downloadsLock.Lock()
	if len(downloads) > 50 {
		downloads = map[string][]byte{}
	}
	downloads[address] = data
	downloadsLock.Unlock()
	return data, nil
}

// iTunes turned the searches down (403, 429): none until then.
var (
	iTunesPausedUntil time.Time
	iTunesLock        sync.Mutex
)

func iTunesOpen(now time.Time) bool {
	iTunesLock.Lock()
	defer iTunesLock.Unlock()
	return !now.Before(iTunesPausedUntil)
}

// In the background: the album art from iTunes. Its links do not expire.
func fromITunes(track *Track) coverResult {
	art, answered := searchITunes(track)
	if art == "" {
		return coverResult{Answered: answered, Failed: !answered}
	}
	logf("Cover from iTunes: %s %s", describe(track), art)
	return coverResult{Entry: &coverEntry{URL: art, FromITunes: true, Renew: forever, Gone: forever}, Answered: true}
}

var (
	firstArtist = regexp.MustCompile(`(?i),|&|(?:^|\W)feat\.?|(?:^|\W)ft\.?|(?:^|\W)x(?:\W|$)`)
	brackets    = regexp.MustCompile(`\s*[(\[].*?[)\]]`)
	featuring   = regexp.MustCompile(`(?i)\s(feat|ft)\.?\s.*$`)
	notWord     = regexp.MustCompile(`[^\p{L}\p{N}]`)
)

var iTunesBase = "https://itunes.apple.com/search"

// The album art for the same artist and title in the iTunes search, or "".
// A video's own frame says more than an album, so videos are left to the
// player's picture. A search finds songs with the same title by other
// artists, so both have to match. answered: false when iTunes could not be
// asked, and the search is to be tried again.
func searchITunes(track *Track) (string, bool) {
	if track.Source.IsVideo || track.Artist == "" || track.Title == "" {
		return "", true
	}
	artist := norm(firstArtist.Split(track.Artist, 2)[0])
	title := norm(track.Title)
	if len([]rune(artist)) < 2 || len([]rune(title)) < 2 {
		return "", true
	}
	term := escapeData(track.Artist + " " + brackets.ReplaceAllString(track.Title, ""))
	countries := []string{"US"}
	if ru {
		countries = []string{"RU", "US"}
	}
	for _, country := range countries {
		request, _ := newRequest("GET", iTunesBase+"?entity=song&limit=10&country="+country+"&term="+term, nil)
		response, err := httpClient.Do(request)
		if err != nil {
			logf("iTunes search failed: %v", err)
			return "", false
		}
		data, _ := io.ReadAll(io.LimitReader(response.Body, 1<<20))
		response.Body.Close()
		if response.StatusCode == 403 || response.StatusCode == 429 {
			// Too many searches from this address: none for a while.
			iTunesLock.Lock()
			iTunesPausedUntil = time.Now().Add(10 * time.Minute)
			iTunesLock.Unlock()
		}
		if response.StatusCode != 200 {
			logf("iTunes search failed: %d", response.StatusCode)
			return "", false
		}
		var reply struct {
			Results []struct {
				ArtistName    string `json:"artistName"`
				TrackName     string `json:"trackName"`
				ArtworkURL100 string `json:"artworkUrl100"`
			} `json:"results"`
		}
		if err := json.Unmarshal(data, &reply); err != nil {
			logf("iTunes search failed: %v", err)
			return "", false
		}
		for _, result := range reply.Results {
			if result.ArtworkURL100 == "" || !alike(norm(result.ArtistName), artist) || !alike(norm(result.TrackName), title) {
				continue
			}
			return strings.Replace(result.ArtworkURL100, "100x100bb", "600x600bb", 1), true
		}
	}
	return "", true
}

// Lowercase letters and digits only, without (feat. …), [Remix] and the like.
func norm(s string) string {
	s = brackets.ReplaceAllString(s, "")
	s = featuring.ReplaceAllString(s, "")
	return notWord.ReplaceAllString(strings.ToLower(s), "")
}

// The same, or one starts the other: "Song" and "Song - Remastered 2011".
func alike(found, wanted string) bool {
	if found == wanted {
		return true
	}
	shorter := min(len([]rune(found)), len([]rune(wanted)))
	return shorter >= 4 && (strings.HasPrefix(found, wanted) || strings.HasPrefix(wanted, found))
}
