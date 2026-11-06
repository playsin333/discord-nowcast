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
        public static readonly string AutostartPrompt = T(
            "Запускать Nowcast вместе с Windows? Выбор можно изменить в настройках.",
            "Start Nowcast with Windows? You can change this in settings.");
        public static readonly string PictureModeration = T(
            "Картинка и ваше имя пользователя Discord отправляются на сервер Nowcast для модерации.",
            "The picture and your Discord username are sent to the Nowcast server for moderation.");
        public static readonly string Autostart = T("Запускать вместе с Windows", "Start with Windows");
        public static readonly string Settings = T("Настройки…", "Settings…");
        public static readonly string OwnStatus = T("Свой статус…", "Own status…");

        public static readonly string Donate = T("Донат", "Donate");
        public static readonly string DonateChoose = T("Выбери способ поддержки", "Choose how to support");
        public static readonly string DonateRubles = T("Рубли", "RUB");
        public static readonly string SupportProject = T("Поддержать проект", "Support the project");
        public static readonly string SupportThanks = T("Спасибо, что пользуешься Nowcast", "Thanks for using Nowcast");
        public static readonly string SupportMessage = T(
            "Nowcast — бесплатный проект. Разработку и хостинг я оплачиваю сам. Если программа тебе полезна, буду благодарен за любую добровольную поддержку. Все функции доступны бесплатно.",
            "Nowcast is a free project. I pay for development and hosting myself. If the app is useful to you, I would appreciate any voluntary support. All features are free.");
        public static readonly string SupportRubles = T("Поддержать рублями", "Support in RUB");
        public static readonly string SupportCopy = T("Скопировать адрес", "Copy address");
        public static readonly string SupportCopied = T("Адрес скопирован", "Address copied");
        public static readonly string SupportSocial = T("Буду рад и подписке на мой Telegram — это тоже поддержка проекта.", "Following my Telegram channel also helps support the project.");
        public static readonly string SupportTelegram = T("Telegram · @playsin333tgk", "Telegram · @playsin333tgk");
        public static readonly string SupportContinue = T("Продолжить", "Continue");

        // The own status window.
        public static readonly string OwnOn = T("Показывать свой статус вместо трека", "Show my own status instead of the track");
        public static readonly string GetNowcast = T("Получить Nowcast", "Get Nowcast");
        public static readonly string OwnNote = T("Текст отправляется только в Discord, без фильтра слов. Выбранные картинки загружаются на сервер Nowcast и могут быть удалены администратором. Кнопка «Получить Nowcast» открывает наш Discord-сервер.", "Text goes only to Discord, without a word filter. Selected pictures upload to the Nowcast server and may be removed by the administrator. Get Nowcast opens our Discord server.");
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
        public static readonly string CoverPicture = T("Своя картинка на всех треках", "My picture on every track");
        public static readonly string CoverNone = T("Без обложки и картинки", "No cover, no picture");
        public static readonly string FallbackLabel = T("Картинка, если у трека нет обложки:", "Picture when a track has no cover:");
        public static readonly string PictureLabel = T("Картинка на всех треках:", "Picture on every track:");
        public static readonly string ButtonsOption = T("Кнопка «Слушать на …»", "\"Listen on …\" button");
        public static readonly string ShowPausedOption = T("Не убирать статус на паузе", "Keep the status on pause");
        public static readonly string HideAlbumOption = T("Убрать альбом", "Hide the album");
        public static readonly string LyricsOption = T("Текст песни в статусе (строка, которую поют)", "Lyrics in the status (the line being sung)");
        public static readonly string SourcesNote = T("Приложения, которые сейчас играют. Снимите галочку, чтобы не показывать его в статусе.", "Apps playing now. Clear a box to keep that app out of the status.");
        public static readonly string ProgramHeading = T("Программа", "The program");
        public static readonly string CheckUpdatesOption = T("При запуске открывать GitHub, если есть обновление", "Open GitHub on startup when an update is available");
        public static readonly string Advanced = T("Дополнительно (config.json)", "Advanced (config.json)");
        public static readonly string Close = T("Закрыть", "Close");
        public static readonly string OpenLog = T("Открыть лог", "Open log");
        public static readonly string CopyDiagnostics = T("Скопировать диагностику", "Copy diagnostics");
        public static readonly string DiagnosticsCopied = T("Диагностика скопирована. Вставьте её (Ctrl+V) в сообщение на форуме помощи.", "Diagnostics copied. Paste it (Ctrl+V) into your post in the help forum.");
        public static readonly string UpdateFailed = T("Не получилось: {0}", "That didn't work: {0}");
        public static readonly string DiscordServer = T("Сервер в Discord: вопросы и баги", "Discord server: questions and bugs");
        public static readonly string GitHub = T("Страница на GitHub", "GitHub page");
        public static readonly string Exit = T("Выход", "Exit");

        public static readonly string ListenOn = T("Слушать на {0}", "Listen on {0}");
        public static readonly string WatchOn = T("Смотреть на {0}", "Watch on {0}");
        public static readonly string FindOnYouTube = T("Найти на YouTube", "Find on YouTube");
    }
}

