package main

import (
	"context"
	_ "embed"
	"os"
	"strings"
	"time"

	"fyne.io/systray"
	"github.com/godbus/dbus/v5"
)

//go:embed icon.png
var icon []byte

// The tray icon through StatusNotifierItem: KDE, Cinnamon, XFCE and others
// show it; GNOME needs the AppIndicator extension. Without a host the
// program works on, and the icon appears if one turns up later.
func runTray(engine *Engine, signals chan os.Signal) {
	go func() {
		<-signals
		systray.Quit()
	}()
	systray.Run(func() { trayReady(engine) }, nil)
}

func trayReady(engine *Engine) {
	systray.SetIcon(icon)
	systray.SetTitle("discord-nowcast")
	systray.SetTooltip("discord-nowcast")
	if trayState() != "yes" {
		logf("No tray host (StatusNotifierWatcher): the icon shows once there is one; in GNOME that is the AppIndicator extension")
	}

	status := systray.AddMenuItem(engine.Status(), "")
	status.Disable()
	systray.AddSeparator()
	hide := systray.AddMenuItem(text.HideHour, "")
	own := systray.AddMenuItemCheckbox(text.OwnToggle, "", loadSettings().Own)
	autostart := systray.AddMenuItemCheckbox(text.Autostart, "", autostartOn())
	systray.AddSeparator()
	config := systray.AddMenuItem(text.OpenConfig, "")
	logItem := systray.AddMenuItem(text.OpenLog, "")
	diag := systray.AddMenuItem(text.Diagnostics, "")
	update := systray.AddMenuItem("", "")
	update.Hide()
	server := systray.AddMenuItem(text.DiscordServer, "")
	systray.AddSeparator()
	exit := systray.AddMenuItem(text.Exit, "")

	refresh := func() {
		line := engine.Status()
		status.SetTitle(line)
		systray.SetTooltip("discord-nowcast: " + line)
		if time.Now().Before(engine.HiddenUntil()) {
			hide.SetTitle(text.ShowAgain)
		} else {
			hide.SetTitle(text.HideHour)
		}
		if loadSettingsQuiet().Own {
			own.Check()
		} else {
			own.Uncheck()
		}
	}
	engine.OnChange(refresh)
	refresh()

	var newer *release
	go checkUpdates(func(found *release) {
		newer = found
		update.SetTitle(strings.Replace(text.UpdateAvailable, "%s", found.Version, 1))
		update.Show()
	})

	for {
		select {
		case <-hide.ClickedCh:
			if time.Now().Before(engine.HiddenUntil()) {
				engine.Show()
			} else {
				engine.Hide(time.Hour)
			}
			refresh()
		case <-own.ClickedCh:
			settings, err := readSettings()
			if err != nil {
				// A file with a mistake in it is not written over with the defaults.
				logf("config.json could not be read, own status not changed: %v", err)
				open(configPath())
				refresh()
				continue
			}
			settings.Own = !settings.Own
			if err := settings.Save(); err != nil {
				logf("config.json could not be written: %v", err)
			}
			// Nothing to show yet: the lines are written in config.json.
			if settings.Own && len([]rune(strings.TrimSpace(settings.OwnTitle))) < 2 {
				open(configPath())
			}
			engine.Wake()
			refresh()
		case <-autostart.ClickedCh:
			if err := setAutostart(!autostartOn()); err != nil {
				logf("Autostart not changed: %v", err)
			}
			if autostartOn() {
				autostart.Check()
			} else {
				autostart.Uncheck()
			}
		case <-config.ClickedCh:
			open(configPath())
		case <-logItem.ClickedCh:
			open(logPath())
		case <-diag.ClickedCh:
			if err := os.WriteFile(diagnosticsPath(), []byte(diagnostics(engine)), 0o644); err != nil {
				logf("Diagnostics not written: %v", err)
			} else {
				open(diagnosticsPath())
			}
		case <-update.ClickedCh:
			if newer != nil {
				open(newer.Page)
			}
		case <-server.ClickedCh:
			open(discordInvite)
		case <-exit.ClickedCh:
			systray.Quit()
			return
		}
	}
}

// The settings as they are on disk, without writing the defaults or the log.
func loadSettingsQuiet() *Settings {
	settings := defaultSettings()
	if data, err := os.ReadFile(configPath()); err == nil {
		settings.apply(data)
	}
	return settings
}

// Once after the start and then once a day, unless checkUpdates is false.
func checkUpdates(found func(*release)) {
	time.Sleep(10 * time.Second)
	for {
		if loadSettingsQuiet().CheckUpdates {
			if release, err := findNewer(); err != nil {
				logf("Update check failed: %v", err)
			} else if release != nil {
				logf("Version %s is out", release.Version)
				found(release)
				return
			}
		}
		time.Sleep(24 * time.Hour)
	}
}

// Whether a tray host is there: "yes", "no", or why it is not known.
func trayState() string {
	conn, err := dbus.ConnectSessionBus()
	if err != nil {
		return "no D-Bus: " + err.Error()
	}
	defer conn.Close()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	var has bool
	if err := conn.BusObject().CallWithContext(ctx, "org.freedesktop.DBus.NameHasOwner", 0, "org.kde.StatusNotifierWatcher").Store(&has); err != nil {
		return err.Error()
	}
	if has {
		return "yes"
	}
	return "no"
}
