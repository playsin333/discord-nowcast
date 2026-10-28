package main

import (
	"bytes"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"sort"
	"strings"
	"sync"
	"time"
)

// Hosts for the player's picture: Discord only shows pictures by URL. Our
// own (a Cloudflare Worker, server/worker.js) first, then uguu.se, as on
// Windows; litterbox has answered "412 No file!" to everything since
// October 2026 and is left out here.
var ownHost = "https://nowcast-covers.playsin333.workers.dev"

// 20 s: a slow connection (a VPN) took over 10 s for one that went through a
// moment later; the next host starts alongside after hedge anyway.
var httpClient = &http.Client{Timeout: 20 * time.Second}

// An uploaded picture: its link and how long the host keeps it.
type Uploaded struct {
	URL  string
	Keep time.Duration
}

type host struct {
	name   string
	keep   time.Duration
	upload func([]byte) (string, error)
	// How long it took last time, in ms; the start values keep the order above.
	took      int
	downUntil time.Time
	failures  int
}

var hosts = []*host{
	{name: "nowcast", keep: 14 * 24 * time.Hour, upload: func(image []byte) (string, error) { return toOwn(image, "") }, took: 800},
	{name: "uguu.se", keep: 3 * time.Hour, upload: toUguu, took: 1000},
}

var (
	hostBook sync.Mutex
	// A host that failed is left out for 1, 5, 15, then 30 minutes.
	pauses = []time.Duration{time.Minute, 5 * time.Minute, 15 * time.Minute, 30 * time.Minute}
	hedge  = 1500 * time.Millisecond
)

// The quickest lately first. A host that has not answered within hedge
// does not hold the others back: the next one starts alongside it, and the
// first to deliver wins. When all are left out, the one due back first is
// tried, so a network that comes back is noticed.
func upload(image []byte) (Uploaded, error) {
	order := hostOrder(time.Now())
	type outcome struct {
		uploaded Uploaded
		err      error
	}
	results := make(chan outcome, len(order))
	running, next := 0, 0
	var last error
	start := func() {
		h := order[next]
		next++
		running++
		go func() {
			url, err := uploadTo(h, image)
			results <- outcome{Uploaded{URL: url, Keep: h.keep}, err}
		}()
	}
	start()
	for running > 0 {
		var timer <-chan time.Time
		if next < len(order) {
			timer = time.After(hedge)
		}
		select {
		case result := <-results:
			running--
			if result.err == nil {
				return result.uploaded, nil
			}
			last = result.err
			if running == 0 && next < len(order) {
				start()
			}
		case <-timer:
			start()
		}
	}
	return Uploaded{}, last
}

func hostOrder(now time.Time) []*host {
	hostBook.Lock()
	defer hostBook.Unlock()
	var order []*host
	for _, h := range hosts {
		if !now.Before(h.downUntil) {
			order = append(order, h)
		}
	}
	if len(order) == 0 {
		soonest := hosts[0]
		for _, h := range hosts[1:] {
			if h.downUntil.Before(soonest.downUntil) {
				soonest = h
			}
		}
		order = append(order, soonest)
	}
	sort.SliceStable(order, func(a, b int) bool { return order[a].took < order[b].took })
	return order
}

// One upload to one host, with the book-keeping: how long it took, or that the host is down.
func uploadTo(h *host, image []byte) (string, error) {
	begin := time.Now()
	url, err := h.upload(image)
	hostBook.Lock()
	defer hostBook.Unlock()
	if err != nil {
		logf("%s failed: %v", h.name, err)
		h.downUntil = time.Now().Add(pauses[min(h.failures, len(pauses)-1)])
		h.failures++
		return "", err
	}
	h.took = (h.took + int(time.Since(begin).Milliseconds())) / 2
	h.failures = 0
	return url, nil
}

// A picture of one's own status goes to our host alone, where a bad one can
// be removed and its uploader banned; it is kept there for 2 days.
func uploadOwn(image []byte) (Uploaded, error) {
	url, err := toOwn(image, "?kind=own")
	return Uploaded{URL: url, Keep: 48 * time.Hour}, err
}

// Ours: the picture itself in the body, the link in the reply. Named by its
// hash, so one album's cover is kept once for everyone.
func toOwn(image []byte, query string) (string, error) {
	kind := "image/jpeg"
	if isPNG(image) {
		kind = "image/png"
	}
	reply, err := post(ownHost+"/upload"+query, kind, image)
	if err != nil {
		return "", err
	}
	reply = strings.TrimSpace(reply)
	if !strings.HasPrefix(reply, ownHost+"/") || strings.Contains(reply, " ") {
		return "", errors.New(short(reply))
	}
	return reply, nil
}

// uguu.se: kept for three hours.
func toUguu(image []byte) (string, error) {
	kind, body := form("files[]", image)
	reply, err := post("https://uguu.se/upload", kind, body)
	if err != nil {
		return "", err
	}
	var parsed struct {
		Files []struct {
			URL string `json:"url"`
		} `json:"files"`
	}
	if json.Unmarshal([]byte(reply), &parsed) == nil && len(parsed.Files) > 0 && strings.HasPrefix(parsed.Files[0].URL, "https://") {
		return parsed.Files[0].URL, nil
	}
	return "", errors.New(short(reply))
}

// Hosts behind a firewall turn away requests that do not say what sends them.
func newRequest(method, url string, body io.Reader) (*http.Request, error) {
	request, err := http.NewRequest(method, url, body)
	if err == nil {
		request.Header.Set("User-Agent", "discord-nowcast/"+version+" (linux)")
	}
	return request, err
}

func post(url, kind string, body []byte) (string, error) {
	request, err := newRequest("POST", url, bytes.NewReader(body))
	if err != nil {
		return "", err
	}
	request.Header.Set("Content-Type", kind)
	response, err := httpClient.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	data, _ := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	if response.StatusCode < 200 || response.StatusCode > 299 {
		return "", fmt.Errorf("%d %s", response.StatusCode, short(string(data)))
	}
	return string(data), nil
}

// multipart/form-data written by hand, as a browser sends it: not every
// server understands what libraries write.
func form(field string, image []byte) (string, []byte) {
	random := make([]byte, 16)
	rand.Read(random)
	boundary := "----nowcast" + hex.EncodeToString(random)
	name, kind := "cover.jpg", "image/jpeg"
	if isPNG(image) {
		name, kind = "cover.png", "image/png"
	}
	var body bytes.Buffer
	body.WriteString("--" + boundary + "\r\n")
	body.WriteString(`Content-Disposition: form-data; name="` + field + `"; filename="` + name + "\"\r\n")
	body.WriteString("Content-Type: " + kind + "\r\n\r\n")
	body.Write(image)
	body.WriteString("\r\n--" + boundary + "--\r\n")
	return "multipart/form-data; boundary=" + boundary, body.Bytes()
}

var (
	pageTitle = regexp.MustCompile(`(?is)<title>(.*?)</title>|<h1>(.*?)</h1>`)
	spaces    = regexp.MustCompile(`\s+`)
)

// An error page's title or heading, or the start of the reply: never the
// whole page in the log.
func short(s string) string {
	if m := pageTitle.FindStringSubmatch(s); m != nil {
		s = m[1] + m[2]
	}
	s = strings.TrimSpace(spaces.ReplaceAllString(s, " "))
	if r := []rune(s); len(r) > 120 {
		s = string(r[:120]) + "…"
	}
	return s
}
