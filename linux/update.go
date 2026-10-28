package main

import (
	"encoding/json"
	"fmt"
	"io"
	"strconv"
	"strings"
)

// A newer release on GitHub. The program does not replace itself here: a
// package manager or a new AppImage does that. The tray only says it is out.
type release struct {
	Version string
	Page    string
}

func findNewer() (*release, error) {
	request, err := newRequest("GET", "https://api.github.com/repos/"+repo+"/releases/latest", nil)
	if err != nil {
		return nil, err
	}
	response, err := httpClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, fmt.Errorf("GitHub: %d", response.StatusCode)
	}
	data, _ := io.ReadAll(io.LimitReader(response.Body, 4<<20))
	var reply struct {
		Tag  string `json:"tag_name"`
		Page string `json:"html_url"`
	}
	if err := json.Unmarshal(data, &reply); err != nil {
		return nil, err
	}
	latest := strings.TrimPrefix(reply.Tag, "v")
	if !newer(latest, version) {
		return nil, nil
	}
	return &release{Version: latest, Page: reply.Page}, nil
}

// Whether a is a later version than b: 1.10.0 after 1.9.3. A build without
// a version ("dev") never asks.
func newer(a, b string) bool {
	pa, okA := parseVersion(a)
	pb, okB := parseVersion(b)
	if !okA || !okB {
		return false
	}
	for i := 0; i < len(pa) || i < len(pb); i++ {
		var x, y int
		if i < len(pa) {
			x = pa[i]
		}
		if i < len(pb) {
			y = pb[i]
		}
		if x != y {
			return x > y
		}
	}
	return false
}

func parseVersion(s string) ([]int, bool) {
	var parts []int
	for _, part := range strings.Split(s, ".") {
		n, err := strconv.Atoi(part)
		if err != nil {
			return nil, false
		}
		parts = append(parts, n)
	}
	return parts, len(parts) > 0
}
