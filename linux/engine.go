package main

import (
	"fmt"
	"math"
	"os"
	"strings"
	"sync"
	"time"
)

// The work itself, as src/Engine.cs: twice a second reads what plays and,
// when it changed, sends the status to Discord.
type Engine struct {
	discord *Discord
	media   *Media
	covers  *Covers
	wake    chan struct{}
	stop    chan struct{}
	stopped chan struct{}

	settings     *Settings
	settingsSeen string

	// What is in the status now: track key, or "none".
	sentKey     string
	sentStart   *int64
	jumpedAt    time.Time
	sentPlaying *bool
	// The picture in the status now: a cover's link, the settings' own picture, or "".
	sentImage string
	// The line of the song in the status now, or "".
	sentLyric   string
	lyrics      *Lyrics
	sentAt      time.Time
	nextConnect time.Time
	resendAt    *time.Time
	shownKey    string
	timeline    timeline
	// One's own status as last set, and when it started by its time.
	ownKey   string
	ownStart int64
	// The track last seen and when, to see how long its status took.
	seenKey string
	seenAt  time.Time

	// For the tray and the diagnostics, under lock.
	lock         sync.Mutex
	status       string
	tracks       []*Track
	discordState string
	hiddenUntil  time.Time
	changed      func()
}

// The last timeline the player reported, and the start worked out from it.
type timeline struct {
	key      string
	position float64
	updated  int64
	playing  bool
	start    *int64
}

func newEngine() *Engine {
	e := &Engine{
		discord: &Discord{},
		media:   newMedia(),
		wake:    make(chan struct{}, 1),
		stop:    make(chan struct{}),
		stopped: make(chan struct{}),
		status:  text.Starting,
	}
	// A cover that is done does not wait for the next round.
	e.covers = newCovers(e.Wake)
	e.lyrics = newLyrics(e.Wake)
	e.settings = loadSettings()
	e.settingsSeen = settingsStamp()
	return e
}

func (e *Engine) Wake() {
	select {
	case e.wake <- struct{}{}:
	default:
	}
}

func (e *Engine) Start() { go e.loop() }

func (e *Engine) Stop() {
	close(e.stop)
	select {
	case <-e.stopped:
	case <-time.After(5 * time.Second):
	}
}

func (e *Engine) Hide(d time.Duration) {
	e.lock.Lock()
	e.hiddenUntil = time.Now().Add(d)
	e.lock.Unlock()
	e.Wake()
}

func (e *Engine) Show() {
	e.lock.Lock()
	e.hiddenUntil = time.Time{}
	e.lock.Unlock()
	e.Wake()
}

func (e *Engine) HiddenUntil() time.Time {
	e.lock.Lock()
	defer e.lock.Unlock()
	return e.hiddenUntil
}

func (e *Engine) Status() string {
	e.lock.Lock()
	defer e.lock.Unlock()
	return e.status
}

func (e *Engine) Tracks() []*Track {
	e.lock.Lock()
	defer e.lock.Unlock()
	return e.tracks
}

// For the diagnostics: connected, and with which application, or why not.
func (e *Engine) DiscordState() string {
	e.lock.Lock()
	defer e.lock.Unlock()
	return e.discordState
}

func (e *Engine) OnChange(handler func()) {
	e.lock.Lock()
	e.changed = handler
	e.lock.Unlock()
}

func (e *Engine) report(status string) {
	e.lock.Lock()
	if status == e.status {
		e.lock.Unlock()
		return
	}
	e.status = status
	handler := e.changed
	e.lock.Unlock()
	if handler != nil {
		handler()
	}
}

// config.json is looked at every round: its time and size tell a change.
func settingsStamp() string {
	info, err := os.Stat(configPath())
	if err != nil {
		return ""
	}
	return fmt.Sprintf("%d:%d", info.ModTime().UnixNano(), info.Size())
}

func (e *Engine) loop() {
	defer close(e.stopped)
	lastError := ""
	for {
		err := e.safeTick()
		// The same error every round would bury the log: once until it changes.
		if err != "" && err != lastError {
			logf("Error: %s", err)
		}
		lastError = err
		select {
		case <-e.stop:
			if e.discord.IsConnected() {
				e.discord.SetActivity(nil)
			}
			e.discord.Close()
			e.media.Close()
			return
		case <-e.wake:
		case <-time.After(500 * time.Millisecond):
		}
	}
}

func (e *Engine) safeTick() (failure string) {
	defer func() {
		if r := recover(); r != nil {
			failure = fmt.Sprint(r)
		}
	}()
	return e.tick()
}

func (e *Engine) tick() string {
	if stamp := settingsStamp(); stamp != e.settingsSeen {
		e.settingsSeen = stamp
		e.settings = loadSettings()
		e.sentKey = ""
		logf("Settings reloaded")
	}
	settings := e.settings

	tracks, err := e.media.Read(settings)
	mediaError := ""
	if err != nil {
		mediaError = "Media players unavailable: " + err.Error()
	}
	hiddenUntil := e.HiddenUntil()
	isHidden := time.Now().Before(hiddenUntil)
	// One's own status takes the place of whatever plays.
	isOwn := !isHidden && settings.ShowsOwn()
	var track *Track
	if !isHidden && !isOwn {
		track = e.pick(tracks)
	}
	// Turned on again later: its time counts from then.
	if !isOwn {
		e.ownKey = ""
	}
	e.lock.Lock()
	e.tracks = tracks
	e.lock.Unlock()

	if e.discord.IsConnected() && !e.discord.IsAlive() {
		logf("Discord closed the connection")
		e.discord.Close()
		e.nextConnect = time.Now()
	}
	// Each service has its own application, named after it: another
	// service means another connection. Closing one clears its status.
	clientID := e.discord.ClientID()
	switch {
	case isOwn:
		clientID = settings.OwnClientIDFor()
	case track != nil:
		clientID = settings.ClientIDFor(track.Source)
	case clientID == "":
		clientID = settings.ClientID
	}
	if e.discord.IsConnected() && e.discord.ClientID() != clientID {
		who := "one's own status"
		if !isOwn {
			who = track.Source.Name
		}
		logf("Switching to the Discord application of %s", who)
		e.discord.Close()
		e.nextConnect = time.Now()
	}
	if !e.discord.IsConnected() && !time.Now().Before(e.nextConnect) {
		if e.discord.Connect(clientID) {
			e.sentKey = ""
			// A Discord that is still starting up can drop the first
			// status, so it goes once more a little later.
			at := time.Now().Add(10 * time.Second)
			e.resendAt = &at
		} else {
			e.nextConnect = time.Now().Add(3 * time.Second)
		}
	}
	e.lock.Lock()
	if e.discord.IsConnected() {
		e.discordState = "connected, application " + e.discord.ClientID()
	} else if e.discord.lastMiss != "" {
		e.discordState = "not connected: " + e.discord.lastMiss
	} else {
		e.discordState = "not connected"
	}
	e.lock.Unlock()

	switch {
	case mediaError != "":
		e.report(text.MediaUnavailable)
	case !e.discord.IsConnected() && e.discord.isDenied:
		e.report(text.DiscordDenied)
	case !e.discord.IsConnected():
		e.report(text.DiscordOff)
	case isHidden:
		e.report(fmt.Sprintf(text.HiddenUntil, hiddenUntil.Format("15:04")))
	case isOwn:
		e.report(fmt.Sprintf(text.OwnShown, strings.TrimSpace(settings.OwnTitle)))
	case track == nil:
		e.report(text.NothingPlaying)
	default:
		e.report(describe(track))
	}
	if !e.discord.IsConnected() {
		return mediaError
	}
	if isOwn {
		e.sendOwn(settings)
		return mediaError
	}

	key := "none"
	if track != nil {
		key = track.Key()
		if e.seenKey != key {
			e.seenKey = key
			e.seenAt = time.Now()
		}
	}
	// The cover is looked for as soon as the track is seen, even when its
	// status has to wait for its turn. The status itself never waits for it.
	image := ""
	var start *int64
	var playing *bool
	if track != nil {
		image = e.image(track, settings)
		start = e.startFor(track, time.Now().UnixMilli())
		playing = &track.IsPlaying
	}
	// A seek: the start of the track moved by more than 3 seconds.
	jumped := start != nil && e.sentStart != nil && math.Abs(float64(*start-*e.sentStart)) > 3000
	// At most one seek in 30 s: a player whose timeline reads oddly must not
	// send the same track over and over.
	if jumped && key == e.sentKey && time.Since(e.jumpedAt) < 30*time.Second {
		jumped = false
	}
	isResend := e.resendAt != nil && !time.Now().Before(*e.resendAt)
	// Another picture for the same track: a cover that was not there when
	// the track started, one in place of the settings' own picture or of an
	// iTunes cover, or a new link for one the host is about to delete.
	coverCame := key == e.sentKey && track != nil && image != "" && image != e.sentImage
	playingChanged := (playing == nil) != (e.sentPlaying == nil) || (playing != nil && *playing != *e.sentPlaying)
	// The line being sung, when it is wanted: the next one goes out in its
	// turn, a quick one between two turns is skipped.
	lyric := ""
	if track != nil && settings.Lyrics && track.IsPlaying && start != nil && !track.Source.IsVideo {
		lyric = lyricAt(e.lyrics.Find(track), time.Now().UnixMilli()-*start)
	}
	lyricCame := key == e.sentKey && track != nil && lyric != e.sentLyric
	changed := key != e.sentKey || playingChanged || jumped || isResend || coverCame || lyricCame
	// Discord takes at most 5 updates per 20 seconds.
	if !changed || time.Since(e.sentAt) < 4*time.Second {
		return mediaError
	}
	if isResend {
		e.resendAt = nil
	}

	var sendErr error
	if track == nil {
		_, sendErr = e.discord.SetActivity(nil)
		if sendErr == nil {
			logf("Status cleared")
		}
	} else {
		activity := buildActivity(track, start, settings, image, lyric)
		var complaint string
		complaint, sendErr = e.discord.SetActivity(activity)
		if sendErr == nil && complaint != "" && useLinks {
			// An older Discord may not know the links on the cover and title.
			useLinks = false
			activity = buildActivity(track, start, settings, image, lyric)
			_, sendErr = e.discord.SetActivity(activity)
		}
		// A new line of the song every few seconds would bury the log.
		onlyLyric := lyricCame && key == e.sentKey && !playingChanged && !jumped && !isResend && !coverCame
		if sendErr == nil && !onlyLyric {
			note := ""
			if _, ok := activity["assets"]; !ok {
				note = " (no cover)"
			}
			if key != e.sentKey && e.seenKey == key {
				note += fmt.Sprintf(" [%d ms after the track was seen]", time.Since(e.seenAt).Milliseconds())
			}
			logf("Status: %s%s", describe(track), note)
		}
	}
	if sendErr != nil {
		logf("Lost Discord: %v", sendErr)
		e.discord.Close()
		e.nextConnect = time.Now().Add(5 * time.Second)
		return mediaError
	}
	e.shownKey = ""
	if track != nil {
		e.shownKey = key
	}
	e.sentKey = key
	e.sentImage = image
	e.sentLyric = lyric
	if jumped {
		e.jumpedAt = time.Now()
	}
	e.sentStart = start
	e.sentPlaying = playing
	e.sentAt = time.Now()
	return mediaError
}

// The picture for the status: the track's cover, or until it comes (or
// when there is none) the picture picked for that, or the settings' link;
// "" for none. With "picture" the picked one on every track, with "none" nothing.
func (e *Engine) image(track *Track, settings *Settings) string {
	if settings.CoverSource == "none" {
		return ""
	}
	if settings.Covers() {
		if cover := e.covers.Find(track, settings); cover != "" {
			return cover
		}
	}
	if picked := e.covers.FindFile(settings.FallbackPicturePath()); picked != "" {
		return picked
	}
	return settings.Image
}

// One's own status: sent when it is set or changed, or its picture comes,
// with the same limit as a track's. Its time counts from when it was set.
func (e *Engine) sendOwn(settings *Settings) {
	title := strings.TrimSpace(settings.OwnTitle)
	key := fmt.Sprintf("own|%s|%s|%s|%d", settings.OwnType, title, strings.TrimSpace(settings.OwnLine2), settings.OwnMinutes)
	if key != e.ownKey {
		e.ownKey = key
		e.ownStart = time.Now().UnixMilli() - int64(settings.OwnMinutes)*60000
	}
	image := e.covers.FindFile(settings.OwnPicturePath())
	isResend := e.resendAt != nil && !time.Now().Before(*e.resendAt)
	changed := key != e.sentKey || image != e.sentImage || isResend
	if !changed || time.Since(e.sentAt) < 4*time.Second {
		return
	}
	if isResend {
		e.resendAt = nil
	}
	complaint, err := e.discord.SetActivity(ownActivity(settings.OwnType, title, settings.OwnLine2, e.ownStart, image))
	if err == nil && complaint != "" && useLinks {
		useLinks = false
		_, err = e.discord.SetActivity(ownActivity(settings.OwnType, title, settings.OwnLine2, e.ownStart, image))
	}
	if err != nil {
		logf("Lost Discord: %v", err)
		e.discord.Close()
		e.nextConnect = time.Now().Add(5 * time.Second)
		return
	}
	note := ""
	if image == "" {
		note = " (no picture)"
	}
	logf("Status: own, %s%s", title, note)
	e.shownKey = ""
	e.sentKey = key
	e.sentImage = image
	e.sentStart = nil
	e.sentPlaying = nil
	e.sentAt = time.Now()
}

// What to show: what plays, a track with a known length first (a video
// feed in the browser usually has none), then the one already shown, then
// the current one. On pause: only with showPaused.
func (e *Engine) pick(tracks []*Track) *Track {
	var visible, pool, timed []*Track
	for _, track := range tracks {
		if !e.settings.Hides(track.App, track.Source.Name) {
			visible = append(visible, track)
		}
	}
	for _, track := range visible {
		if track.IsPlaying {
			pool = append(pool, track)
		}
	}
	if len(pool) == 0 && e.settings.ShowPaused {
		pool = visible
	}
	if len(pool) == 0 {
		return nil
	}
	for _, track := range pool {
		if track.Duration > 0 {
			timed = append(timed, track)
		}
	}
	if len(timed) > 0 {
		pool = timed
	}
	for _, track := range pool {
		if track.Key() == e.shownKey {
			return track
		}
	}
	for _, track := range pool {
		if track.IsCurrent {
			return track
		}
	}
	return pool[0]
}

// When the track started, Unix ms; nil when the player tells neither a
// length nor a position. Worked out again only when the player reports
// something new: one that keeps one position while it plays would
// otherwise creep on a second a second and go out again as a seek.
func (e *Engine) startFor(track *Track, now int64) *int64 {
	if track.Duration <= 0 && track.Position <= 0 && track.UpdatedAt == 0 {
		return nil
	}
	tl := &e.timeline
	same := track.Key() == tl.key && track.IsPlaying == tl.playing
	if same && tl.start != nil && track.Position == tl.position && track.UpdatedAt == tl.updated {
		return tl.start
	}
	// A new position under the old time: the time is stale, the position is as of now.
	updatedAt := track.UpdatedAt
	if same && track.UpdatedAt == tl.updated {
		updatedAt = 0
	}
	tl.key = track.Key()
	tl.playing = track.IsPlaying
	tl.position = track.Position
	tl.updated = track.UpdatedAt
	start := startedAt(track, updatedAt, now)
	tl.start = &start
	return tl.start
}

func startedAt(track *Track, updatedAt, now int64) int64 {
	position := track.Position * 1000
	// The position is as of updatedAt; it has played on since.
	if track.IsPlaying && updatedAt > 0 {
		position += math.Max(0, float64(now-updatedAt))
	}
	if track.Duration > 0 {
		position = math.Min(position, track.Duration*1000)
	}
	return now - int64(position)
}
