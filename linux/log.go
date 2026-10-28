package main

import (
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// ~/.config/discord-nowcast: config.json, log.txt (the current run only)
// and the lock that keeps a second copy out.
var folder = configFolder()

func configFolder() string {
	base := os.Getenv("XDG_CONFIG_HOME")
	if base == "" {
		home, _ := os.UserHomeDir()
		base = filepath.Join(home, ".config")
	}
	return filepath.Join(base, "discord-nowcast")
}

func logPath() string { return filepath.Join(folder, "log.txt") }

var logGate sync.Mutex

// Also to stderr: a run from a terminal, or the journal, sees it at once.
var logToStderr = true

func logStart() {
	os.MkdirAll(folder, 0o755)
	logGate.Lock()
	defer logGate.Unlock()
	os.WriteFile(logPath(), nil, 0o644)
}

func logf(format string, args ...interface{}) {
	line := time.Now().Format("15:04:05") + " " + fmt.Sprintf(format, args...) + "\n"
	logGate.Lock()
	defer logGate.Unlock()
	if logToStderr {
		os.Stderr.WriteString(line)
	}
	// A run of weeks should not grow it without end.
	if info, err := os.Stat(logPath()); err == nil && info.Size() > 1024*1024 {
		os.WriteFile(logPath(), nil, 0o644)
	}
	file, err := os.OpenFile(logPath(), os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	file.WriteString(line)
	file.Close()
}

// logWriter takes what libraries print with the log package.
type logWriter struct{}

func (logWriter) Write(p []byte) (int, error) {
	s := string(p)
	for len(s) > 0 && s[len(s)-1] == '\n' {
		s = s[:len(s)-1]
	}
	logf("%s", s)
	return len(p), nil
}
