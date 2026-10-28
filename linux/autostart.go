package main

import (
	"os"
	"path/filepath"
	"strings"
)

// Start on login: ~/.config/autostart/discord-nowcast.desktop. Set up when
// the program runs from a new place (the marker installed-at keeps the
// path); after that the user's choice in the tray is left alone, as on Windows.
func autostartPath() string {
	return filepath.Join(filepath.Dir(folder), "autostart", "discord-nowcast.desktop")
}

// The path to start: the AppImage itself, not the folder it unpacks to.
func executable() string {
	if image := os.Getenv("APPIMAGE"); image != "" {
		return image
	}
	path, err := os.Executable()
	if err != nil {
		return "discord-nowcast"
	}
	if resolved, err := filepath.EvalSymlinks(path); err == nil {
		path = resolved
	}
	return path
}

func setUpAutostart() {
	marker := filepath.Join(folder, "installed-at")
	path := executable()
	before, _ := os.ReadFile(marker)
	if strings.TrimSpace(string(before)) == path {
		return
	}
	// A run from the build folder or go run is not an installation.
	if strings.HasPrefix(path, os.TempDir()) {
		return
	}
	if err := setAutostart(true); err != nil {
		logf("Autostart not set: %v", err)
		return
	}
	os.WriteFile(marker, []byte(path+"\n"), 0o644)
	logf("Autostart set for %s", path)
}

func autostartOn() bool {
	data, err := os.ReadFile(autostartPath())
	if err != nil {
		return false
	}
	content := string(data)
	return !strings.Contains(content, "Hidden=true") && !strings.Contains(content, "X-GNOME-Autostart-enabled=false")
}

func setAutostart(on bool) error {
	if !on {
		err := os.Remove(autostartPath())
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	os.MkdirAll(filepath.Dir(autostartPath()), 0o755)
	entry := "[Desktop Entry]\n" +
		"Type=Application\n" +
		"Name=discord-nowcast\n" +
		"Comment=" + t("Трек из любого плеера в статусе Discord", "The track from any player in the Discord status") + "\n" +
		"Exec=" + desktopQuote(executable()) + "\n" +
		"Icon=discord-nowcast\n" +
		"Terminal=false\n" +
		"X-GNOME-Autostart-enabled=true\n"
	return os.WriteFile(autostartPath(), []byte(entry), 0o644)
}

// A path in Exec= of a .desktop file: quoted when it has spaces or quotes.
func desktopQuote(path string) string {
	if !strings.ContainsAny(path, " \t\"'\\$`") {
		return path
	}
	replacer := strings.NewReplacer(`\`, `\\\\`, `"`, `\\"`, "`", "\\\\`", `$`, `\\$`)
	return `"` + replacer.Replace(path) + `"`
}
