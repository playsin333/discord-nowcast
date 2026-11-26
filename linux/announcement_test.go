package main

import (
	"errors"
	"path/filepath"
	"strings"
	"testing"
)

func TestAnnouncementDelivery(t *testing.T) {
	marker := filepath.Join(t.TempDir(), "announcement-id")
	n := 0
	notify := func(string) error { n++; return nil }
	first := `{"id":1,"message":"one"}`
	if err := deliverAnnouncement(strings.NewReader(first), marker, func(string) error { return errors.New("no desktop") }); err == nil {
		t.Fatal("expected delivery failure")
	}
	for _, feed := range []string{`{"id":0,"message":""}`, first, first, `{"id":2,"message":"two"}`, first} {
		if err := deliverAnnouncement(strings.NewReader(feed), marker, notify); err != nil {
			t.Fatal(err)
		}
	}
	if n != 2 {
		t.Fatalf("got %d notifications, want 2", n)
	}
}
