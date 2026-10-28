package main

import (
	"os"
	"strings"
)

// Interface strings: Russian when the system speaks Russian, English
// elsewhere. Strings shown in Discord follow the same rule.
var ru = isRussian()

func isRussian() bool {
	for _, name := range []string{"LC_ALL", "LC_MESSAGES", "LANG", "LANGUAGE"} {
		if value := os.Getenv(name); value != "" {
			return strings.HasPrefix(strings.ToLower(value), "ru")
		}
	}
	return false
}

func t(ruText, enText string) string {
	if ru {
		return ruText
	}
	return enText
}

var text = struct {
	AlreadyRunning, NothingPlaying, DiscordOff, DiscordDenied, HiddenUntil, MediaUnavailable, OwnShown, Starting      string
	HideHour, ShowAgain, OwnToggle, Autostart, OpenConfig, OpenLog, Diagnostics, UpdateAvailable, DiscordServer, Exit string
	Paused, ListenOn, WatchOn, FindOnYouTube, Music                                                                   string
}{
	AlreadyRunning:   t("discord-nowcast уже работает.", "discord-nowcast is already running."),
	NothingPlaying:   t("Ничего не играет", "Nothing is playing"),
	DiscordOff:       t("Discord не запущен", "Discord is not running"),
	DiscordDenied:    t("Нет доступа к Discord", "No access to Discord"),
	HiddenUntil:      t("Статус скрыт до %s", "Status hidden until %s"),
	MediaUnavailable: t("Нет связи с D-Bus: не видно, что играет", "No D-Bus connection: can't see what plays"),
	OwnShown:         t("Свой статус: %s", "Own status: %s"),
	Starting:         t("Запускается…", "Starting…"),

	HideHour:        t("Скрыть статус на час", "Hide status for an hour"),
	ShowAgain:       t("Снова показывать статус", "Show status again"),
	OwnToggle:       t("Свой статус вместо трека", "Own status instead of the track"),
	Autostart:       t("Запускать при входе в систему", "Start on login"),
	OpenConfig:      t("Открыть настройки (config.json)", "Open settings (config.json)"),
	OpenLog:         t("Открыть лог", "Open log"),
	Diagnostics:     t("Диагностика", "Diagnostics"),
	UpdateAvailable: t("Вышла версия %s", "Version %s is out"),
	DiscordServer:   t("Сервер в Discord: вопросы и баги", "Discord server: questions and bugs"),
	Exit:            t("Выход", "Exit"),

	Paused:        t("пауза", "paused"),
	ListenOn:      t("Слушать на %s", "Listen on %s"),
	WatchOn:       t("Смотреть на %s", "Watch on %s"),
	FindOnYouTube: t("Найти на YouTube", "Find on YouTube"),
	Music:         t("Музыка", "Music"),
}
