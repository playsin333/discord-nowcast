using System.Globalization;

namespace Nowcast
{
    // Interface strings: Russian on a Russian Windows, English elsewhere.
    // Strings shown in Discord follow the same rule.
    static class Text
    {
        public static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

        static string T(string ru, string en)
        {
            return Ru ? ru : en;
        }

        public static readonly string AlreadyRunning = T(
            "discord-nowcast уже работает: его значок у часов, в правом нижнем углу экрана.",
            "discord-nowcast is already running: its icon is by the clock, at the bottom right of the screen.");
        public static readonly string Running = T(
            "discord-nowcast работает. Его значок здесь, у часов: нажмите на него, чтобы открыть меню.",
            "discord-nowcast is running. Its icon lives here, by the clock: click it for the menu.");
        public static readonly string Updated = T("Обновлено до версии {0}.", "Updated to version {0}.");

        public static readonly string NothingPlaying = T("Ничего не играет", "Nothing is playing");
        public static readonly string DiscordOff = T("Discord не запущен", "Discord is not running");
        public static readonly string DiscordElevated = T("Нет доступа к Discord: админ или антивирус", "No access to Discord: admin or antivirus");
        public static readonly string HiddenUntil = T("Статус скрыт до {0:HH:mm}", "Status hidden until {0:t}");
        public static readonly string MediaUnavailable = T("Windows не даёт узнать, что играет", "Windows won't tell what is playing");
        public static readonly string MediaRemoved = T("Урезанная сборка Windows: нет поддержки медиа", "This Windows build has no media support");
        public static readonly string MediaServiceOff = T("Отключена служба Windows (оптимизатор?)", "A Windows service is disabled (a tweaker?)");
        public static readonly string OwnShown = T("Свой статус: {0}", "Own status: {0}");
        public static readonly string Starting = T("Запускается…", "Starting…");

        public static readonly string HideHour = T("Скрыть статус на час", "Hide status for an hour");
        public static readonly string ShowAgain = T("Снова показывать статус", "Show status again");
        public static readonly string Sources = T("Источники в статусе", "Sources in the status");
        public static readonly string WhatWindowsSees = T("Что видит Windows", "What Windows sees");
        public static readonly string NoSessions = T("Ни одно приложение не сообщает о треке", "No app reports a track");
        public static readonly string Playing = T("играет", "playing");
        public static readonly string Paused = T("пауза", "paused");
        public static readonly string Autostart = T("Запускать вместе с Windows", "Start with Windows");
        public static readonly string Settings = T("Настройки…", "Settings…");
        public static readonly string OwnStatus = T("Свой статус…", "Own status…");

        // The own status window.
        public static readonly string OwnOn = T("Показывать свой статус вместо трека", "Show my own status instead of the track");
        public static readonly string OwnNote = T("Клик по картинке или строке 1 открывает сервер nowcast.", "A click on the picture or line 1 opens the nowcast server.");
        public static readonly string OwnUnclean = T("Не покажется: в строках нельзя ссылки, рекламу казино и 18+.", "Won't show: no links, casino ads or 18+ in the lines.");
        public static readonly string OwnTypeLabel = T("Тип:", "Type:");
        public static readonly string OwnListening = T("Слушает nowcast", "Listening to nowcast");
        public static readonly string OwnWatching = T("Смотрит nowcast", "Watching nowcast");
        public static readonly string OwnPlaying = T("Играет в nowcast", "Playing nowcast");
        public static readonly string OwnTitleLabel = T("Строка 1:", "Line 1:");
        public static readonly string OwnLine2Label = T("Строка 2 (необязательно):", "Line 2 (optional):");
        public static readonly string OwnPictureLabel = T("Картинка:", "Picture:");
        public static readonly string OwnPick = T("Выбрать…", "Choose…");
        public static readonly string OwnRemove = T("Убрать", "Remove");
        public static readonly string OwnPictures = T("Картинки", "Pictures");
        public static readonly string OwnPictureFailed = T("Не получилось открыть картинку: {0}", "Could not open the picture: {0}");
        public static readonly string OwnTimeLabel = T("Сколько уже играет (до 100 ч):", "How long it has been going (up to 100 h):");
        public static readonly string Hours = T("ч", "h");
        public static readonly string Minutes = T("мин", "min");

        // The settings window.
        public static readonly string StatusHeading = T("Статус в Discord", "Discord status");
        public static readonly string InListLabel = T("Что видно в списке участников после «Слушает»:", "What the member list shows after \"Listening to\":");
        public static readonly string InListSource = T("Откуда играет: SoundCloud, Telegram…", "Where it plays: SoundCloud, Telegram…");
        public static readonly string InListArtist = T("Исполнитель", "The artist");
        public static readonly string InListTitle = T("Название трека", "The track title");
        public static readonly string InListApp = T("Название приложения Discord", "The Discord application's name");
        public static readonly string CoverLabel = T("Обложка трека:", "Cover art:");
        public static readonly string CoverPlayer = T("Из плеера (своя обложка видна)", "From the player (your own cover shows)");
        public static readonly string CoverITunes = T("Из iTunes (быстрее, без загрузки)", "From iTunes (quicker, no upload)");
        public static readonly string CoverNone = T("Без обложки", "No cover");
        public static readonly string FallbackLabel = T("Картинка, если у трека нет обложки:", "Picture when a track has no cover:");
        public static readonly string ButtonsOption = T("Кнопка «Слушать на …»", "\"Listen on …\" button");
        public static readonly string ShowPausedOption = T("Не убирать статус на паузе", "Keep the status on pause");
        public static readonly string HideAlbumOption = T("Убрать альбом", "Hide the album");
        public static readonly string SourcesNote = T("Приложения, которые сейчас играют. Снимите галочку, чтобы не показывать его в статусе.", "Apps playing now. Clear a box to keep that app out of the status.");
        public static readonly string ProgramHeading = T("Программа", "The program");
        public static readonly string CheckUpdatesOption = T("Проверять обновления", "Check for updates");
        public static readonly string Advanced = T("Дополнительно (config.json)", "Advanced (config.json)");
        public static readonly string Close = T("Закрыть", "Close");
        public static readonly string OpenLog = T("Открыть лог", "Open log");
        public static readonly string CopyDiagnostics = T("Скопировать диагностику", "Copy diagnostics");
        public static readonly string DiagnosticsCopied = T("Диагностика скопирована. Вставьте её (Ctrl+V) в сообщение на форуме помощи.", "Diagnostics copied. Paste it (Ctrl+V) into your post in the help forum.");
        public static readonly string CheckUpdates = T("Проверить обновления", "Check for updates");
        public static readonly string UpdateTo = T("Обновить до {0}", "Update to {0}");
        public static readonly string UpToDate = T("У вас последняя версия, {0}.", "You have the latest version, {0}.");
        public static readonly string UpdateAvailable = T("Вышла версия {0}. Нажмите, чтобы обновить.", "Version {0} is out. Click to update.");
        public static readonly string UpdateFailed = T("Не получилось: {0}", "That didn't work: {0}");
        public static readonly string DiscordServer = T("Сервер в Discord: вопросы и баги", "Discord server: questions and bugs");
        public static readonly string GitHub = T("Страница на GitHub", "GitHub page");
        public static readonly string Exit = T("Выход", "Exit");

        public static readonly string ListenOn = T("Слушать на {0}", "Listen on {0}");
        public static readonly string WatchOn = T("Смотреть на {0}", "Watch on {0}");
        public static readonly string FindOnYouTube = T("Найти на YouTube", "Find on YouTube");
    }
}
