package main

import (
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// A Discord of our own on a socket in a temporary runtime folder: it says
// READY to the handshake, pings once, and keeps what it was sent.
type fakeDiscord struct {
	listener net.Listener
	frames   chan object
	ops      chan int
}

func startFakeDiscord(t *testing.T, sub string) *fakeDiscord {
	runtime := t.TempDir()
	t.Setenv("XDG_RUNTIME_DIR", runtime)
	t.Setenv("TMPDIR", runtime)
	dir := filepath.Join(runtime, sub)
	os.MkdirAll(dir, 0o700)
	listener, err := net.Listen("unix", filepath.Join(dir, "discord-ipc-0"))
	if err != nil {
		t.Fatal(err)
	}
	f := &fakeDiscord{listener: listener, frames: make(chan object, 20), ops: make(chan int, 20)}
	t.Cleanup(func() { listener.Close() })
	go func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			go f.serve(conn)
		}
	}()
	return f
}

func readFrame(conn net.Conn) (int, object, error) {
	header := make([]byte, 8)
	if _, err := io.ReadFull(conn, header); err != nil {
		return 0, nil, err
	}
	body := make([]byte, binary.LittleEndian.Uint32(header[4:]))
	if _, err := io.ReadFull(conn, body); err != nil {
		return 0, nil, err
	}
	data := object{}
	err := json.Unmarshal(body, &data)
	return int(binary.LittleEndian.Uint32(header)), data, err
}

func (f *fakeDiscord) serve(conn net.Conn) {
	defer conn.Close()
	op, hello, err := readFrame(conn)
	if err != nil || op != opHandshake || hello["client_id"] == nil {
		return
	}
	f.frames <- hello
	conn.Write(frame(opFrame, object{"cmd": "DISPATCH", "evt": "READY", "data": object{"user": object{"username": "tester"}}}))
	first := true
	for {
		op, data, err := readFrame(conn)
		if err != nil {
			return
		}
		f.ops <- op
		if op == opPong {
			continue
		}
		f.frames <- data
		if first {
			// A ping before the answer: the program must pong and still read the answer.
			conn.Write(frame(opPing, object{"n": 1}))
			first = false
		}
		conn.Write(frame(opFrame, object{"cmd": "SET_ACTIVITY", "evt": nil, "nonce": data["nonce"]}))
	}
}

func (f *fakeDiscord) next(t *testing.T) object {
	select {
	case data := <-f.frames:
		return data
	case <-time.After(5 * time.Second):
		t.Fatal("nothing came to Discord")
		return nil
	}
}

func TestDiscordFrames(t *testing.T) {
	for _, sub := range []string{"", "app/com.discordapp.Discord", "snap.discord"} {
		t.Run("in "+sub, func(t *testing.T) {
			fake := startFakeDiscord(t, sub)
			d := &Discord{}
			if !d.Connect("123") {
				t.Fatal("not connected")
			}
			hello := fake.next(t)
			if hello["client_id"] != "123" || hello["v"] != float64(1) {
				t.Fatalf("handshake: %v", hello)
			}
			if !d.IsAlive() {
				t.Fatal("alive")
			}
			complaint, err := d.SetActivity(object{"type": 2, "details": "Song"})
			if err != nil || complaint != "" {
				t.Fatalf("set: %q %v", complaint, err)
			}
			sent := fake.next(t)
			args := sent["args"].(map[string]interface{})
			if sent["cmd"] != "SET_ACTIVITY" || args["pid"] != float64(os.Getpid()) || args["activity"].(map[string]interface{})["details"] != "Song" || sent["nonce"] == "" {
				t.Fatalf("SET_ACTIVITY: %v", sent)
			}
			if _, err := d.SetActivity(nil); err != nil {
				t.Fatalf("clear after a ping: %v", err)
			}
			if _, ok := fake.next(t)["args"].(map[string]interface{})["activity"]; ok {
				t.Fatal("clearing sends no activity")
			}
			d.Close()
		})
	}
}

func TestDiscordAliveSeesTheEnd(t *testing.T) {
	runtime := t.TempDir()
	t.Setenv("XDG_RUNTIME_DIR", runtime)
	listener, err := net.Listen("unix", filepath.Join(runtime, "discord-ipc-0"))
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	accepted := make(chan net.Conn, 1)
	go func() {
		conn, _ := listener.Accept()
		readFrame(conn)
		conn.Write(frame(opFrame, object{"evt": "READY"}))
		accepted <- conn
	}()
	d := &Discord{}
	if !d.Connect("1") {
		t.Fatal("not connected")
	}
	conn := <-accepted
	if !d.IsAlive() {
		t.Fatal("alive while Discord is there")
	}
	conn.Close()
	time.Sleep(50 * time.Millisecond)
	if d.IsAlive() {
		t.Fatal("a closed Discord is seen")
	}
}
