package main

import (
	"bytes"
	"errors"
	"fmt"
	"io"
	"net/http"
	"regexp"
	"strings"
	"time"
)

// Pictures upload directly to the VPS, which enforces moderation.
var reserveHost = "https://nowcast.85-192-42-111.sslip.io:10443"
var uploadClient = &http.Client{Timeout: 6 * time.Second}

// Searches for metadata can take longer than cover uploads.
var httpClient = &http.Client{Timeout: 20 * time.Second}

// An uploaded picture: its link and how long the host keeps it.
type Uploaded struct {
	URL  string
	Keep time.Duration
}

// A failed request is retried by the cover lookup without switching hosts.
func upload(image []byte) (Uploaded, error) {
	return uploadManaged(image, "")
}

func uploadManaged(image []byte, query string) (Uploaded, error) {
	url, err := toManaged(reserveHost, image, query)
	if err != nil {
		return Uploaded{}, err
	}
	keep := time.Hour
	if query != "" {
		keep = 6 * time.Hour
	}
	return Uploaded{URL: url, Keep: keep}, nil
}

// A picture of one's own status goes to our host alone, where a bad one can
// be removed and its uploader banned; renewed before its six-hour expiry.
func uploadOwn(image []byte) (Uploaded, error) {
	return uploadManaged(image, "?kind=own")
}

type uploadRejection struct {
	status  int
	message string
}

func (e *uploadRejection) Error() string { return fmt.Sprintf("%d %s", e.status, e.message) }

// The picture itself in the body, the link in the reply. Named by its
// hash, so one album's cover is kept once for everyone.
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
