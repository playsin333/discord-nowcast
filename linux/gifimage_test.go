package main

import (
	"bytes"
	"encoding/base64"
	"errors"
	"fmt"
	"image/gif"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func animatedFixture(t *testing.T) []byte {
	t.Helper()
	data, err := os.ReadFile("../tests/fixtures/animated.gif")
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestAnimatedGIFRoundtrip(t *testing.T) {
	original := animatedFixture(t)
	path := filepath.Join(t.TempDir(), "own-picture") // No extension, as in production.
	if err := os.WriteFile(path, original, 0600); err != nil {
		t.Fatal(err)
	}
	picked, err := pictureFromFile(path)
	if err != nil || !bytes.Equal(picked, original) {
		t.Fatalf("GIF selection changed bytes: %v", err)
	}
	if !bytes.Equal(preparePicture(picked), original) {
		t.Fatal("preparation discarded animation")
	}
	decoded, err := gif.DecodeAll(bytes.NewReader(picked))
	if err != nil {
		t.Fatal(err)
	}
	if len(decoded.Image) != 3 || decoded.LoopCount != 0 || decoded.Delay[0] != 8 || decoded.Delay[2] != 22 {
		t.Fatalf("animation metadata: %+v", decoded)
	}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
		body, err := io.ReadAll(request.Body)
		if err != nil || !bytes.Equal(body, original) || request.Header.Get("Content-Type") != "image/gif" {
			t.Error("GIF upload changed bytes or MIME")
		}
		fmt.Fprint(w, "http://"+request.Host+"/"+strings.Repeat("a", 40)+".gif")
	}))
	defer server.Close()
	url, err := toManaged(server.URL, picked, "?kind=own")
	if err != nil || !strings.HasSuffix(url, ".gif") {
		t.Fatalf("GIF upload reply: %s %v", url, err)
	}
}

func TestGIFBudgets(t *testing.T) {
	data := animatedFixture(t)
	wide := bytes.Clone(data)
	wide[6], wide[7] = 1, 2
	oversized := make([]byte, gifMaxBytes+1)
	copy(oversized, data)
	single, _ := base64.StdEncoding.DecodeString("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==")
	many := append([]byte{}, single[:19]...)
	for i := 0; i < 201; i++ {
		many = append(many, single[19:len(single)-1]...)
	}
	many = append(many, 0x3b)
	pixels := bytes.Clone(many)
	pixels[6], pixels[8], pixels[7], pixels[9] = 0, 0, 2, 2
	for name, bad := range map[string][]byte{
		"width": wide, "size": oversized, "frames": many, "pixels": pixels,
		"truncated": data[:len(data)-10], "empty": {},
	} {
		t.Run(name, func(t *testing.T) {
			if err := validateGIF(bad); !errors.Is(err, errInvalidGIF) {
				t.Fatalf("expected rejection: %v", err)
			}
		})
	}
}

func TestGIFPermanentRejection(t *testing.T) {
	rig := newRig()
	path := filepath.Join(t.TempDir(), "own-picture")
	if err := os.WriteFile(path, animatedFixture(t), 0600); err != nil {
		t.Fatal(err)
	}
	rig.c.uploadOwn = func(data []byte) (Uploaded, error) {
		atomic.AddInt32(&rig.uploads, 1)
		return Uploaded{}, &uploadRejection{403, "Banned"}
	}
	rig.c.FindFile(path)
	<-rig.c.files[path].job.done
	rig.c.FindFile(path)
	rig.clock = rig.clock.Add(24 * time.Hour)
	rig.c.FindFile(path)
	if atomic.LoadInt32(&rig.uploads) != 1 {
		t.Fatal("permanent rejection was retried")
	}
	if err := os.WriteFile(path, []byte("GIF89a"), 0600); err != nil {
		t.Fatal(err)
	}
	rig.c.FindFile(path)
	<-rig.c.files[path].job.done
	rig.c.FindFile(path)
	rig.clock = rig.clock.Add(24 * time.Hour)
	rig.c.FindFile(path)
	if rig.c.files[path].job != nil {
		t.Fatal("invalid GIF was retried")
	}
}
