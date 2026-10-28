package main

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

// What to send when asking for help: the version, the system, what the
// program sees and the end of the log. The home folder becomes ~, since
// the text is posted publicly.
func diagnostics(engine *Engine) string {
	var b strings.Builder
	header(&b)
	fmt.Fprintf(&b, "Status: %s\n", engine.Status())
	fmt.Fprintf(&b, "Discord: %s\n", engine.DiscordState())
	sessions(&b, engine.Tracks())
	footer(&b)
	return hideHome(b.String())
}

// The same without a running program: one look at the players and sockets.
func oneOffDiagnostics() string {
	var b strings.Builder
	header(&b)
	settings := defaultSettings()
	if data, err := os.ReadFile(configPath()); err == nil {
		settings.apply(data)
	}
	media := newMedia()
	tracks, err := media.Read(settings)
	media.Close()
	if err != nil {
		fmt.Fprintf(&b, "Players: D-Bus unavailable: %v\n", err)
	}
	fmt.Fprintf(&b, "Discord sockets: %s\n", strings.Join(socketPaths(), ", "))
	sessions(&b, tracks)
	footer(&b)
	return hideHome(b.String())
}

func header(b *strings.Builder) {
	fmt.Fprintf(b, "discord-nowcast %s (linux/%s)\n", version, runtime.GOARCH)
	fmt.Fprintf(b, "System: %s\n", osName())
	fmt.Fprintf(b, "Desktop: %s, %s\n", orNone(os.Getenv("XDG_CURRENT_DESKTOP")), orNone(os.Getenv("XDG_SESSION_TYPE")))
	if os.Getenv("APPIMAGE") != "" {
		b.WriteString("Run as: AppImage\n")
	}
	fmt.Fprintf(b, "Tray: %s\n", trayState())
}

func sessions(b *strings.Builder, tracks []*Track) {
	b.WriteString("\nPlayers (MPRIS):\n")
	if len(tracks) == 0 {
		b.WriteString("  none\n")
	}
	for _, track := range tracks {
		state := "paused"
		if track.IsPlaying {
			state = "playing"
		}
		current := ""
		if track.IsCurrent {
			current = ", current"
		}
		fmt.Fprintf(b, "  %s [%s | %s | %s]: %s, %s%s\n", track.Source.Name, track.App, track.Player.DesktopEntry, track.Player.Identity, describe(track), state, current)
		updated := "-"
		if track.UpdatedAt > 0 {
			updated = time.UnixMilli(track.UpdatedAt).Format("15:04:05")
		}
		fmt.Fprintf(b, "    position %.1f s of %.1f s (as of %s), album %q\n", track.Position, track.Duration, updated, track.Album)
		if track.ArtURL != "" {
			fmt.Fprintf(b, "    picture %s\n", short(track.ArtURL))
		}
		if track.URL != "" {
			fmt.Fprintf(b, "    page %s\n", short(track.URL))
		}
	}
}

func footer(b *strings.Builder) {
	b.WriteString("\nconfig.json:\n")
	if data, err := os.ReadFile(configPath()); err == nil {
		b.Write(data)
	} else {
		fmt.Fprintf(b, "  %v\n", err)
	}
	b.WriteString("\nLog:\n")
	if data, err := os.ReadFile(logPath()); err == nil {
		lines := strings.Split(strings.TrimRight(string(data), "\n"), "\n")
		if len(lines) > 40 {
			lines = lines[len(lines)-40:]
		}
		b.WriteString(strings.Join(lines, "\n") + "\n")
	}
}

func osName() string {
	file, err := os.Open("/etc/os-release")
	if err != nil {
		return runtime.GOOS
	}
	defer file.Close()
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		if line := scanner.Text(); strings.HasPrefix(line, "PRETTY_NAME=") {
			return strings.Trim(strings.TrimPrefix(line, "PRETTY_NAME="), `"`)
		}
	}
	return runtime.GOOS
}

func orNone(s string) string {
	if s == "" {
		return "?"
	}
	return s
}

func hideHome(s string) string {
	home, err := os.UserHomeDir()
	if err != nil || len(home) < 2 {
		return s
	}
	return strings.ReplaceAll(s, home, "~")
}

func diagnosticsPath() string { return filepath.Join(folder, "diagnostics.txt") }
