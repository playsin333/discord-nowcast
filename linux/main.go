// discord-nowcast for Linux: shows the track playing in any player in the
// Discord status ("Listening to …"), as the Windows program in src/ does.
// The track comes from MPRIS on D-Bus, the status goes over Discord's
// local socket.
package main

import (
	"flag"
	"fmt"
	"log"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"syscall"
)

// Set at build time from src/Program.cs: -ldflags "-X main.version=1.5.0".
var version = "dev"

const repo = "playsin333/discord-nowcast"

func main() {
	showVersion := flag.Bool("version", false, "print the version")
	diagnostics := flag.Bool("diagnostics", false, "print what the program sees: players, Discord, settings, log")
	noTray := flag.Bool("no-tray", false, "run without the tray icon")
	flag.Parse()
	if *showVersion {
		fmt.Println("discord-nowcast " + version)
		return
	}
	if *diagnostics {
		logToStderr = false
		fmt.Print(oneOffDiagnostics())
		return
	}

	os.MkdirAll(folder, 0o755)
	lock, err := takeLock()
	if err != nil {
		fmt.Fprintln(os.Stderr, text.AlreadyRunning)
		os.Exit(1)
	}
	defer lock.Close()

	logStart()
	log.SetFlags(0)
	log.SetOutput(logWriter{})
	logf("discord-nowcast %s started", version)
	setUpAutostart()

	engine := newEngine()
	engine.Start()

	// Ctrl+C or the session ending clears the status on the way out.
	signals := make(chan os.Signal, 1)
	signal.Notify(signals, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)

	if *noTray {
		<-signals
	} else {
		runTray(engine, signals)
	}
	engine.Stop()
	logf("Exited")
}

// One copy at a time: a lock on a file next to config.json, let go by the
// system when the process ends, however it ends.
func takeLock() (*os.File, error) {
	file, err := os.OpenFile(filepath.Join(folder, "lock"), os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		return nil, err
	}
	if err := syscall.Flock(int(file.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		file.Close()
		return nil, err
	}
	return file, nil
}

// A file, folder or link in whatever the desktop opens it with.
func open(target string) {
	command := exec.Command("xdg-open", target)
	if err := command.Start(); err != nil {
		logf("Could not open %s: %v", target, err)
		return
	}
	go command.Wait()
}
