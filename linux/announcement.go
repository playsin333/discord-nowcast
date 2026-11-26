package main

import (
	"context"
	"encoding/json"
	"fmt"
	"html"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
	"unicode/utf16"

	"github.com/godbus/dbus/v5"
)

func checkAnnouncement() {
	time.Sleep(10 * time.Second)
	if !loadSettingsQuiet().CheckUpdates {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, "GET", "https://raw.githubusercontent.com/"+repo+"/main/announcement.json", nil)
	if err == nil {
		var res *http.Response
		res, err = httpClient.Do(req)
		if err == nil {
			defer res.Body.Close()
			if res.StatusCode != http.StatusOK {
				err = fmt.Errorf("HTTP %d", res.StatusCode)
			} else if loadSettingsQuiet().CheckUpdates {
				err = deliverAnnouncement(res.Body, filepath.Join(folder, "announcement-id"), desktopNotice)
			}
		}
	}
	if err != nil {
		logf("Announcement unavailable: %v", err)
	}
}

func deliverAnnouncement(body io.Reader, marker string, notify func(string) error) error {
	data, err := io.ReadAll(io.LimitReader(body, 4097))
	if err != nil || len(data) > 4096 {
		return fmt.Errorf("cannot read announcement within size limit")
	}
	var item struct {
		ID      int32  `json:"id"`
		Message string `json:"message"`
	}
	if err := json.Unmarshal(data, &item); err != nil {
		return err
	}
	message := strings.TrimSpace(item.Message)
	if item.ID <= 0 || message == "" || len(utf16.Encode([]rune(message))) > 240 {
		return nil
	}
	if seen, err := os.ReadFile(marker); err == nil {
		last, err := strconv.Atoi(strings.TrimSpace(string(seen)))
		if err != nil || last >= int(item.ID) {
			return err
		}
	} else if !os.IsNotExist(err) {
		return err
	}
	if err := notify(message); err != nil {
		return err
	}
	if err := os.WriteFile(marker+".tmp", []byte(strconv.Itoa(int(item.ID))), 0o600); err != nil {
		return err
	}
	return os.Rename(marker+".tmp", marker)
}

func desktopNotice(message string) error {
	conn, err := dbus.ConnectSessionBus()
	if err != nil {
		return err
	}
	defer conn.Close()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	obj := conn.Object("org.freedesktop.Notifications", "/org/freedesktop/Notifications")
	return obj.CallWithContext(ctx, "org.freedesktop.Notifications.Notify", 0, "discord-nowcast",
		uint32(0), "", "Nowcast", html.EscapeString(message), []string{}, map[string]dbus.Variant{}, int32(-1)).Err
}
