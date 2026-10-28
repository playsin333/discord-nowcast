package main

import (
	"encoding/json"
	"fmt"
	"io"
	"math"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

// A line of a song's text and when it is sung, ms from the start.
type lyricLine struct {
	At   int64
	Text string
}

// The song's text with timings from lrclib.net, as src/Lyrics.cs: the line
// sung now goes into the status. Looked up once per track in the background;
// a failed lookup is tried again, 2 … 60 s apart, while the track plays.
type Lyrics struct {
	// By track: its lines, empty when the database has no timed text.
	found    map[string][]lyricLine
	pending  map[string]*lyricsJob
	retryAt  map[string]time.Time
	failures map[string]int

	// Stand-ins for the tests: the clock and the lookup (nil, false: failed, try again).
	now    func() time.Time
	lookUp func(*Track) ([]lyricLine, bool)
	ready  func()
}

type lyricsJob struct {
	done  chan struct{}
	lines []lyricLine
	ok    bool
}

func newLyrics(ready func()) *Lyrics {
	return &Lyrics{
		found:    map[string][]lyricLine{},
		pending:  map[string]*lyricsJob{},
		retryAt:  map[string]time.Time{},
		failures: map[string]int{},
		now:      time.Now,
		lookUp:   fromLrclib,
		ready:    ready,
	}
}

// The track's lines, or nil while they are looked for or when there are none.
func (l *Lyrics) Find(track *Track) []lyricLine {
	key := track.Key()
	if lines, ok := l.found[key]; ok {
		return lines
	}
	if job, ok := l.pending[key]; ok {
		select {
		case <-job.done:
		default:
			return nil
		}
		delete(l.pending, key)
		if job.ok {
			if len(l.found) > 200 {
				l.found = map[string][]lyricLine{}
			}
			l.found[key] = job.lines
			delete(l.failures, key)
			delete(l.retryAt, key)
			return job.lines
		}
		l.failures[key]++
		l.retryAt[key] = l.now().Add(backoff(l.failures[key]))
		return nil
	}
	if at, ok := l.retryAt[key]; ok && l.now().Before(at) {
		return nil
	}
	job := &lyricsJob{done: make(chan struct{})}
	l.pending[key] = job
	go func() {
		defer func() {
			if failure := recover(); failure != nil {
				logf("Lyrics lookup failed: %v", failure)
				job.lines, job.ok = nil, false
			}
			close(job.done)
			if l.ready != nil {
				l.ready()
			}
		}()
		job.lines, job.ok = l.lookUp(track)
	}()
	return nil
}

// The line sung at ms from the start: "" before the first, "♪" for a pause
// between lines.
func lyricAt(lines []lyricLine, ms int64) string {
	line, seen := "", false
	for _, next := range lines {
		if next.At > ms {
			break
		}
		line, seen = next.Text, true
	}
	if !seen {
		return ""
	}
	if strings.TrimSpace(line) == "" {
		return "♪"
	}
	return strings.TrimSpace(line)
}

var lrcStamp = regexp.MustCompile(`\[(\d+):(\d+(?:[.,]\d+)?)\]`)

// LRC: "[01:02.50]words", several times before one line when it repeats.
func parseLRC(lrc string) []lyricLine {
	var lines []lyricLine
	for _, raw := range strings.Split(lrc, "\n") {
		matches := lrcStamp.FindAllStringSubmatchIndex(raw, -1)
		if len(matches) == 0 {
			continue
		}
		text := strings.TrimSpace(raw[matches[len(matches)-1][1]:])
		for _, m := range matches {
			minutes, _ := strconv.Atoi(raw[m[2]:m[3]])
			seconds, _ := strconv.ParseFloat(strings.Replace(raw[m[4]:m[5]], ",", ".", 1), 64)
			lines = append(lines, lyricLine{At: int64((float64(minutes)*60 + seconds) * 1000), Text: text})
		}
	}
	sort.SliceStable(lines, func(a, b int) bool { return lines[a].At < lines[b].At })
	return lines
}

var lrclibBase = "https://lrclib.net/api"

type lrclibTrack struct {
	SyncedLyrics string  `json:"syncedLyrics"`
	Duration     float64 `json:"duration"`
}

// In the background: the timed text from lrclib.net, by artist, title and
// length; empty when there is none, ok false when the site could not be asked.
func fromLrclib(track *Track) ([]lyricLine, bool) {
	if track.Artist == "" || track.Title == "" {
		return nil, true
	}
	query := "artist_name=" + escapeData(track.Artist) + "&track_name=" + escapeData(track.Title)
	length := ""
	if track.Duration > 0 {
		length = fmt.Sprintf("&duration=%d", int(math.Round(track.Duration)))
	}
	var exact lrclibTrack
	status, err := getJSON(lrclibBase+"/get?"+query+length, &exact)
	if err != nil || (status != 200 && status != 404) {
		logf("lrclib.net: %d %v", status, err)
		return nil, false
	}
	text := exact.SyncedLyrics
	if text == "" {
		// No exact match: the closest by length among those found.
		var results []lrclibTrack
		status, err = getJSON(lrclibBase+"/search?"+query, &results)
		if err != nil || status != 200 {
			logf("lrclib.net: %d %v", status, err)
			return nil, false
		}
		for _, result := range results {
			if result.SyncedLyrics == "" || (track.Duration > 0 && math.Abs(result.Duration-track.Duration) > 3) {
				continue
			}
			text = result.SyncedLyrics
			break
		}
	}
	lines := parseLRC(text)
	logf("Lyrics for %s: %d lines", describe(track), len(lines))
	return lines, true
}

func getJSON(url string, into interface{}) (int, error) {
	request, err := newRequest("GET", url, nil)
	if err != nil {
		return 0, err
	}
	response, err := httpClient.Do(request)
	if err != nil {
		return 0, err
	}
	defer response.Body.Close()
	data, err := io.ReadAll(io.LimitReader(response.Body, 16<<20))
	if err != nil {
		return response.StatusCode, err
	}
	if response.StatusCode == 200 {
		err = json.Unmarshal(data, into)
	}
	return response.StatusCode, err
}
