package main

import (
	"bytes"
	"crypto/rand"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"syscall"
	"time"
)

// Discord's local IPC socket, discord-ipc-N, the one games use. A frame is
// the opcode (int32 LE), the length (int32 LE) and JSON in UTF-8.
const (
	opHandshake = 0
	opFrame     = 1
	opClose     = 2
	opPing      = 3
	opPong      = 4
)

type Discord struct {
	conn     net.Conn
	clientID string
	// Why Discord was not reached last time: written to the log once, not every 3 s.
	lastMiss string
	// Discord is there but the socket turns the program away: another user's Discord.
	isDenied bool
}

// Where the socket may be: the runtime folder and the temporary ones, and
// inside each the folders of Discord from Flatpak and Snap.
func socketDirs() []string {
	var bases []string
	for _, name := range []string{"XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP"} {
		if value := os.Getenv(name); value != "" {
			bases = append(bases, value)
		}
	}
	bases = append(bases, fmt.Sprintf("/run/user/%d", os.Getuid()), "/tmp")
	var dirs []string
	seen := map[string]bool{}
	for _, base := range bases {
		for _, sub := range []string{"", "app/com.discordapp.Discord", "app/com.discordapp.DiscordCanary", "app/com.discordapp.DiscordPTB", "app/dev.vencord.Vesktop", ".flatpak/dev.vencord.Vesktop/xdg-run", "snap.discord", "snap.discord-canary"} {
			dir := filepath.Join(base, sub)
			if !seen[dir] {
				seen[dir] = true
				dirs = append(dirs, dir)
			}
		}
	}
	return dirs
}

// The sockets that exist now, discord-ipc-0 first.
func socketPaths() []string {
	var paths []string
	for index := 0; index < 10; index++ {
		for _, dir := range socketDirs() {
			path := filepath.Join(dir, fmt.Sprintf("discord-ipc-%d", index))
			if info, err := os.Stat(path); err == nil && info.Mode()&os.ModeSocket != 0 {
				paths = append(paths, path)
			}
		}
	}
	return paths
}

func (d *Discord) IsConnected() bool { return d.conn != nil }

func (d *Discord) ClientID() string { return d.clientID }

// False once Discord has closed its end: it quit or restarted. Without this
// a restart goes unnoticed until the track changes, since only a write would fail.
func (d *Discord) IsAlive() bool {
	if d.conn == nil {
		return false
	}
	unix, ok := d.conn.(*net.UnixConn)
	if !ok {
		return true
	}
	raw, err := unix.SyscallConn()
	if err != nil {
		return false
	}
	alive := true
	raw.Read(func(fd uintptr) bool {
		buffer := make([]byte, 1)
		n, _, err := syscall.Recvfrom(int(fd), buffer, syscall.MSG_PEEK|syscall.MSG_DONTWAIT)
		// Nothing waiting is fine; a read of nothing at all is the end.
		if err == nil && n == 0 {
			alive = false
		} else if err != nil && err != syscall.EAGAIN && err != syscall.EWOULDBLOCK && err != syscall.EINTR {
			alive = false
		}
		return true
	})
	return alive
}

func (d *Discord) Connect(clientID string) bool {
	paths := socketPaths()
	miss := "no discord-ipc socket: Discord is closed, or it is the browser version"
	d.isDenied = false
	for _, path := range paths {
		conn, err := net.DialTimeout("unix", path, time.Second)
		if err != nil {
			if errors.Is(err, os.ErrPermission) || errors.Is(err, syscall.EACCES) {
				d.isDenied = true
			}
			miss = path + ": " + err.Error()
			continue
		}
		err = send(conn, opHandshake, object{"v": 1, "client_id": clientID})
		var reply object
		if err == nil {
			reply, err = read(conn)
		}
		if err == nil && value(reply, "evt") == "READY" {
			d.conn = conn
			d.clientID = clientID
			user := ""
			if data, ok := reply["data"].(map[string]interface{}); ok {
				if u, ok := data["user"].(map[string]interface{}); ok {
					user = " as " + value(u, "username")
				}
			}
			logf("Connected to Discord%s (%s)", user, path)
			d.lastMiss = ""
			d.isDenied = false
			return true
		}
		if err != nil {
			logf("Discord refused: %v", err)
		} else {
			raw, _ := json.Marshal(reply)
			logf("Discord refused: %s", raw)
		}
		conn.Close()
		miss = ""
	}
	if miss != "" && miss != d.lastMiss {
		logf("Discord not found: %s", miss)
	}
	d.lastMiss = miss
	return false
}

// nil clears the status. Returns Discord's complaint, or "" when it took it.
func (d *Discord) SetActivity(activity object) (string, error) {
	arguments := object{"pid": os.Getpid()}
	if activity != nil {
		arguments["activity"] = activity
	}
	if err := send(d.conn, opFrame, object{"cmd": "SET_ACTIVITY", "args": arguments, "nonce": nonce()}); err != nil {
		return "", err
	}
	reply, err := read(d.conn)
	if err != nil {
		return "", err
	}
	if value(reply, "evt") == "ERROR" {
		message := ""
		if data, ok := reply["data"].(map[string]interface{}); ok {
			message = value(data, "message")
		}
		logf("Discord did not take the status: %s", message)
		return message, nil
	}
	return "", nil
}

func (d *Discord) Close() {
	if d.conn != nil {
		d.conn.Close()
		d.conn = nil
		d.clientID = ""
	}
}

func encodeJSON(body interface{}) []byte {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	encoder.Encode(body)
	return bytes.TrimRight(buffer.Bytes(), "\n")
}

func frame(op int, body interface{}) []byte {
	payload := encodeJSON(body)
	out := make([]byte, 8+len(payload))
	binary.LittleEndian.PutUint32(out[0:], uint32(op))
	binary.LittleEndian.PutUint32(out[4:], uint32(len(payload)))
	copy(out[8:], payload)
	return out
}

func send(conn net.Conn, op int, body interface{}) error {
	conn.SetWriteDeadline(time.Now().Add(10 * time.Second))
	_, err := conn.Write(frame(op, body))
	return err
}

// The next answer; pings are answered on the way.
func read(conn net.Conn) (object, error) {
	for {
		conn.SetReadDeadline(time.Now().Add(10 * time.Second))
		header := make([]byte, 8)
		if _, err := io.ReadFull(conn, header); err != nil {
			return nil, fmt.Errorf("Discord does not answer: %w", err)
		}
		op := int(binary.LittleEndian.Uint32(header[0:]))
		length := int(binary.LittleEndian.Uint32(header[4:]))
		if length < 0 || length > 1<<20 {
			return nil, fmt.Errorf("Discord sent a frame of %d bytes", length)
		}
		body := make([]byte, length)
		if _, err := io.ReadFull(conn, body); err != nil {
			return nil, fmt.Errorf("Discord does not answer: %w", err)
		}
		data := object{}
		if length > 0 {
			json.Unmarshal(body, &data)
		}
		switch op {
		case opPing:
			if err := send(conn, opPong, data); err != nil {
				return nil, err
			}
			continue
		case opClose:
			return nil, fmt.Errorf("Discord closed the connection: %s", value(data, "message"))
		}
		return data, nil
	}
}

func nonce() string {
	b := make([]byte, 16)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func value(values map[string]interface{}, key string) string {
	if v, ok := values[key]; ok && v != nil {
		return fmt.Sprint(v)
	}
	return ""
}
