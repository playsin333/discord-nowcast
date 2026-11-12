package main

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
)

var installationGate sync.Mutex
var validInstallation = regexp.MustCompile(`^[a-f0-9]{64}$`)

func installationToken() (string, error) {
	installationGate.Lock()
	defer installationGate.Unlock()
	path := filepath.Join(folder, "status-installation")
	read := func() (string, error) {
		data, err := os.ReadFile(path)
		if err != nil {
			return "", err
		}
		token := strings.TrimSpace(string(data))
		if !validInstallation.MatchString(token) {
			return "", fmt.Errorf("invalid installation token")
		}
		return token, nil
	}
	token, err := read()
	if err == nil || !os.IsNotExist(err) {
		return token, err
	}
	if err := os.MkdirAll(folder, 0700); err != nil {
		return "", err
	}
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", err
	}
	token = hex.EncodeToString(raw)
	file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if os.IsExist(err) {
		return read()
	}
	if err != nil {
		return "", err
	}
	_, writeErr := file.WriteString(token)
	closeErr := file.Close()
	if writeErr != nil {
		return "", writeErr
	}
	if closeErr != nil {
		return "", closeErr
	}
	return token, nil
}

func publicInstallationID() (string, error) {
	token, err := installationToken()
	if err != nil {
		return "", err
	}
	hash := sha256.Sum256([]byte(token))
	return hex.EncodeToString(hash[:]), nil
}
