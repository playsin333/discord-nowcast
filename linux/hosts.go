package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"
)

// Hosts for the player's picture: Discord only shows pictures by URL. Our
// own Cloudflare Worker first, then our VPS. Both support moderation.
var ownHost = "https://nowcast-covers.playsin333.workers.dev"
var reserveHost = "https://nowcast.85-192-42-111.sslip.io:10443"
var uploadClient = &http.Client{Timeout: 6 * time.Second}

// Searches for metadata can take longer than cover uploads.
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
	{name: "nowcast", keep: time.Hour, upload: func(image []byte) (string, error) { return toOwn(image, "") }, took: 800},
	{name: "vps", keep: time.Hour, upload: func(image []byte) (string, error) { return toManaged(reserveHost, image, "") }, took: 1000},
}

var (
	hostBook sync.Mutex
	// No new Cloudflare attempts for 30 minutes after a transient error.
	hostNow = time.Now
)

// Try Cloudflare, then the VPS on a transient failure. Rejections are final.
func upload(image []byte) (Uploaded, error) {
	return uploadManaged(image, "")
}

func uploadManaged(image []byte, query string) (Uploaded, error) {
	var last error
	for _, h := range hostOrder(hostNow()) {
		var url string
		var err error
		if query == "" {
			url, err = uploadTo(h, image)
		} else {
			origin := ownHost
			if h.name == "vps" {
				origin = reserveHost
			}
			url, err = toManaged(origin, image, query)
			recordHost(h, time.Now(), err)
		}
		if err == nil {
			keep := h.keep
			if query != "" {
				keep = 6 * time.Hour
			}
			return Uploaded{URL: url, Keep: keep}, nil
		}
		var rejected *uploadRejection
		if errors.As(err, &rejected) {
			return Uploaded{}, err
		}
		last = err
	}
	return Uploaded{}, last
}

func hostOrder(now time.Time) []*host {
	hostBook.Lock()
	defer hostBook.Unlock()
	var order []*host
	if !now.Before(hosts[0].downUntil) {
		order = append(order, hosts[0])
	}
	order = append(order, hosts[1])
	// Keep fixed priority: recovered Cloudflare regains first place.
	return order
}

// One upload to one host, with the book-keeping: how long it took, or that the host is down.
func uploadTo(h *host, image []byte) (string, error) {
	begin := time.Now()
	url, err := h.upload(image)
	recordHost(h, begin, err)
	return url, err
}

func recordHost(h *host, begin time.Time, err error) {
	hostBook.Lock()
	defer hostBook.Unlock()
	if err != nil {
		var rejected *uploadRejection
		if errors.As(err, &rejected) {
			return
		}
		logf("%s failed: %v", h.name, err)
		if h == hosts[0] {
			h.downUntil = hostNow().Add(30 * time.Minute)
		}
		h.failures++
		return
	}
	h.took = (h.took + int(time.Since(begin).Milliseconds())) / 2
	h.failures = 0
	h.downUntil = time.Time{}
}

// A picture of one's own status goes to our host alone, where a bad one can
// be removed and its uploader banned; renewed before its six-hour expiry.
func uploadOwn(image []byte) (Uploaded, error) {
	return uploadManaged(image, "?kind=own")
}

// Ours: the picture itself in the body, the link in the reply. Named by its
// hash, so one album's cover is kept once for everyone.
func toOwn(image []byte, query string) (string, error) {
	return toManaged(ownHost, image, query)
}

type uploadRejection struct {
	status  int
	message string
}

func (e *uploadRejection) Error() string { return fmt.Sprintf("%d %s", e.status, e.message) }

func toManaged(origin string, image []byte, query string) (string, error) {
	kind := "image/jpeg"
	if isPNG(image) {
		kind = "image/png"
	}
	request, err := newRequest("POST", origin+"/upload"+query, bytes.NewReader(image))
	if err != nil {
		return "", err
	}
	request.Header.Set("Content-Type", kind)
	response, err := uploadClient.Do(request)
	if err != nil {
		return "", err
	}
	defer response.Body.Close()
	data, err := io.ReadAll(io.LimitReader(response.Body, 4096))
	if err != nil {
		return "", err
	}
	reply := strings.TrimSpace(string(data))
	switch response.StatusCode {
	case 400, 413, 415, 451:
		return "", &uploadRejection{response.StatusCode, short(reply)}
	}
	if response.StatusCode == 403 && reply == "Banned" {
		return "", &uploadRejection{403, reply}
	}
	if response.StatusCode < 200 || response.StatusCode > 299 {
		return "", fmt.Errorf("%d %s", response.StatusCode, short(reply))
	}
	if !regexp.MustCompile("^" + regexp.QuoteMeta(origin) + "/[a-f0-9]{40}\\.(jpg|png)$").MatchString(reply) {
		return "", errors.New(short(reply))
	}
	return reply, nil
}

// Hosts behind a firewall turn away requests that do not say what sends them.
func newRequest(method, url string, body io.Reader) (*http.Request, error) {
	request, err := http.NewRequest(method, url, body)
	if err == nil {
		request.Header.Set("User-Agent", "discord-nowcast/"+version+" (linux)")
	}
	return request, err
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
