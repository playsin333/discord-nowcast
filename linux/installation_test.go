package main

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestInstallationIdentity(t *testing.T) {
	path := filepath.Join(folder, "status-installation")
	saved, _ := os.ReadFile(path)
	defer func() {
		if saved == nil {
			os.Remove(path)
		} else {
			os.WriteFile(path, saved, 0600)
		}
	}()
	os.WriteFile(path, []byte(strings.Repeat("a", 64)), 0600)
	first, err := publicInstallationID()
	expected := sha256.Sum256([]byte(strings.Repeat("a", 64)))
	if err != nil || first != hex.EncodeToString(expected[:]) {
		t.Fatalf("public hash: %q %v", first, err)
	}
	second, err := publicInstallationID()
	if err != nil || first != second {
		t.Fatal("identity changed")
	}
	os.WriteFile(path, []byte("broken"), 0600)
	if _, err := installationToken(); err == nil {
		t.Fatal("invalid token silently replaced")
	}
	data, _ := os.ReadFile(path)
	if !bytes.Equal(data, []byte("broken")) {
		t.Fatal("token file overwritten")
	}
}

func TestInstallationUploadHeaders(t *testing.T) {
	token, err := installationToken()
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Query().Get("kind") == "own" {
			if r.Header.Get("X-Nowcast-Installation") != token || r.Header.Get("X-Nowcast-Slot") != "fallback" {
				t.Error("missing installation or fallback slot")
			}
		} else if r.Header.Get("X-Nowcast-Client") != token {
			t.Error("missing cover installation header")
		}
		fmt.Fprint(w, "http://"+r.Host+"/"+strings.Repeat("a", 40)+".png")
	}))
	defer server.Close()
	if _, err := toManagedSlot(server.URL, []byte{1}, "?kind=own", "fallback"); err != nil {
		t.Fatal(err)
	}
	if _, err := toManaged(server.URL, []byte{1}, ""); err != nil {
		t.Fatal(err)
	}
}
