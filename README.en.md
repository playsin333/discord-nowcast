<div align="center">

<img src="assets/icon.png" alt="" width="96">

# discord-nowcast

**Show the track from any app in your Discord status.**

Yandex Music, Spotify, SoundCloud, VK, Telegram, YouTube in a browser, AIMP, local files: anything that plays on Windows. There is a [Linux version](#linux-beta) (beta).

[![release](https://img.shields.io/github/v/release/playsin333/discord-nowcast?color=5865f2)](https://github.com/playsin333/discord-nowcast/releases/latest)
[![downloads](https://img.shields.io/github/downloads/playsin333/discord-nowcast/total?color=3d3c38)](https://github.com/playsin333/discord-nowcast/releases)
![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20%7C%20Linux%20(beta)-3d3c38)
[![discord](https://img.shields.io/badge/discord-server-5865f2)](https://discord.gg/SsANGaMr3M)
[![license](https://img.shields.io/badge/license-MIT-3d3c38)](LICENSE)

[Русский](README.md) · **English**

**[⬇ Download discord-nowcast.exe](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast.exe)** · [Linux](#linux-beta) · [Discord server](https://discord.gg/SsANGaMr3M) · [Discussions](https://github.com/playsin333/discord-nowcast/discussions) · [Report a bug](https://github.com/playsin333/discord-nowcast/issues/new?template=bug.yml)

</div>

## What you get

Your profile and the server member list show **Listening to SoundCloud**, **Listening to Yandex Music**, and for video **Watching YouTube** or **Watching Twitch**:

- the track title, the artist below it, and the album as a third line when there is one (on Twitch, the stream title and the channel);
- where it plays, in the header: **Listening to SoundCloud**, **Listening to AIMP**; for players without a Discord application of their own (VLC, a browser on an unknown site) as a line of its own;
- the cover art, the same picture the player shows;
- a progress bar, when the app reports the track length;
- a **Listen on SoundCloud** button (or Spotify, YouTube…; **Find on YouTube** for Yandex Music and Telegram) that finds the track for whoever clicks it.

When the music is paused or stops, the status clears itself.

## Install

1. **[Download `discord-nowcast.exe`](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast.exe)** and put it in a folder you will keep, for example `Documents\discord-nowcast`.
2. **Run it.** A raven appears by the clock, a shortcut appears on the desktop, and from now on it starts with Windows. Downloaded it again, or moved it? The same happens for the new copy.

That's all: one small file, nothing else to install. It needs Windows 10 or 11 and the Discord desktop app (not the website), with **Settings → Activity Privacy → Share your detected activities with others** turned on.

If Windows says *"Windows protected your PC"*, click **More info → Run anyway**: the file comes from the internet and is not signed.

### My antivirus says it is a virus

It is a false positive. The file has no digital signature, adds itself to startup and updates itself from GitHub; to antivirus heuristics that mix looks suspicious, though the program does nothing harmful, and all of its code is open in `src`. If Windows Defender says *"Operation did not complete successfully because the file contains a virus or potentially unwanted software"*:

1. Open **Windows Security → Virus & threat protection → Protection history**, find discord-nowcast and choose **Actions → Allow on device**.
2. Download the exe again and save it to a regular folder. Do not run it straight from the browser or Telegram: that puts it in a temporary folder, which Defender checks harder.
3. If it is still blocked, add the program's folder to the exclusions: **Virus & threat protection → Manage settings → Exclusions → Add an exclusion → Folder**.

Every new version is sent to Microsoft as a false positive; Defender usually stops flagging it within a few days.

### The tray icon says "No access to Discord: admin or antivirus"

Discord is running, but Windows will not let the program connect to it. Most often it is the antivirus: Avast and AVG run unfamiliar programs in a "sandbox" that cannot reach Discord. Add discord-nowcast.exe to the antivirus exclusions (in Avast: **Menu → Settings → General → Exceptions → Add exception**) and restart the program.

The other cause is Discord running as administrator: Windows does not let a regular program connect to one running as administrator. It can happen even when you open Discord as usual: its properties may have the run-as-administrator box checked. To check, in Task Manager go to **Details** → right-click a column header → **Select columns** → **Elevated**. If Discord.exe shows "Yes":

1. Quit Discord completely (right-click its tray icon → **Quit Discord**).
2. Right-click the Discord shortcut → **Properties** → **Compatibility**: clear **Run this program as an administrator**. Check `Update.exe` in `%LOCALAPPDATA%\Discord` the same way.
3. Open Discord again.

If that does not help, run discord-nowcast as administrator too: right-click the exe → **Run as administrator**.

## The tray icon

The raven by the clock, at the bottom right of the screen, is how you control it. Only you see it; nobody in Discord does. Hover for the current track, click for the menu:

| Item | What it does |
| --- | --- |
| Hide status for an hour | clears the status for an hour; click again to bring it back |
| Sources in the status | a checkbox per app playing now; clear one to keep, say, YouTube in the browser out of the status |
| What Windows sees | every app reporting a track right now, playing or paused; a click copies its app ID |
| Start with Windows | on by default |
| Own status… | instead of the track, "Listening to nowcast", "Watching nowcast" or "Playing nowcast" with two lines of your own (the second is optional), a picture and a time (how long it has been going, up to 100 hours). With *Playing* Discord puts nowcast under the header and the lines go below it. A click on the picture or the first line in Discord opens the nowcast server. Links, casino ads and 18+ in the lines are not shown. A check by the item means it is on |
| Settings… | the settings window: what the member list shows, where the cover comes from, your own picture for tracks without a cover, button, pause, sources, autostart, updates. Changes apply at once |
| Open log | what was sent to Discord and any errors |
| Copy diagnostics | copies all a support question needs: the program and Windows versions, what Windows sees, whether Discord was found, the settings and the end of the log. Paste it into your post in the help forum. Your user folder name is masked |
| Check for updates | downloads the new version and restarts; it also checks by itself every six hours |
| Exit | stops it and clears the status |

## Settings

Most of these are in the settings window. All of them live in `config.json` in `%APPDATA%\discord-nowcast`, which opens from the window under *Advanced*.

| Setting | What it means |
| --- | --- |
| `clientId` | the Discord application whose name follows "Listening to". Yandex Music, Spotify, SoundCloud, YouTube, YouTube Music, Twitch, VK Music, Apple Music and Telegram have their own; other players share a general one. To always use your own name, create an app at [discord.com/developers/applications](https://discord.com/developers/applications) (**New Application**) and paste its **Application ID** here |
| `inList` | what the member list shows after "Listening to": `source` where it plays (default; for a service with a Discord application of its own the status lines are the title and the artist, the source is in the "Listening to SoundCloud" header), `artist`, `title`, or `app` for the application name |
| `showPaused` | `true` keeps the status on pause, marked as paused |
| `coverSource` | where the cover comes from (in the window: *Cover art*): `player`, the player's picture, or iTunes when the player gives none or no host takes it (default); `itunes`, the album art from iTunes with nothing to upload, or the player's picture when the track is not there (handy when the hosts do not work for you); `picture`, your own picture on every track (picked in the window under the list); `none`, no cover and no picture |
| `coverSearch` | `false` with `player`: no iTunes lookup |
| `buttons` | `false` removes the "Listen on …" button |
| `hideAlbum` | `true` removes the status's third line with the album (in the window: *Hide the album*) |
| `lyrics` | `true` puts the song's lyrics in the status (in the window: *Lyrics in the status*): the line being sung, as the third line in place of the album, or the second without a picture. The timed lyrics come from the open database [lrclib.net](https://lrclib.net); a track it does not have gets the usual status. Discord takes a status every 4 seconds at most, so a quick line is sometimes skipped |
| `checkUpdates` | `false` stops the automatic update check |
| `image` | a picture URL to show when a track has no cover. Easier: pick a picture in the settings window under *Cover art*; it comes before this link, and with *My picture on every track* it stands in for all covers and lives in the `fallback-picture` file next to config.json |
| `own`, `ownType`, `ownTitle`, `ownLine2`, `ownMinutes` | your own status (the *Own status* window): on or off, the type (`listening`, `watching`, `playing`), line 1, line 2, how many minutes it has been going (up to 6000). The picture lives next to it, in the `own-picture` file |
| `ownPicture`, `fallbackPicture` | Linux only: the path to your own status's picture and to the picture for tracks without a cover |
| `hide` | apps never to show: `["chrome", "Telegram"]` |
| `names` | your own source names: `{"chrome": "YouTube"}` |

## Linux (beta)

There is a Linux version: the same status, the same Discord applications for the services, covers, the button and your own status. It is a beta: try it and tell us on the [server](https://discord.gg/SsANGaMr3M) what works and what does not.

**Install.** Pick the one for your distribution:

- Ubuntu, Debian, Mint, Pop!_OS: [discord-nowcast_amd64.deb](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast_amd64.deb), then `sudo apt install ./discord-nowcast_amd64.deb`;
- Fedora, openSUSE: [discord-nowcast.x86_64.rpm](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast.x86_64.rpm), then `sudo dnf install ./discord-nowcast.x86_64.rpm`;
- anything else: the [AppImage](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast-x86_64.AppImage) (make it executable in its properties and run it) or the plain one-file program, [discord-nowcast-linux-amd64](https://github.com/playsin333/discord-nowcast/releases/latest/download/discord-nowcast-linux-amd64).

For ARM (Raspberry Pi and the like) the [release](https://github.com/playsin333/discord-nowcast/releases/latest) has `_arm64.deb`, `.aarch64.rpm` and `discord-nowcast-linux-arm64`.

Start discord-nowcast from the app menu (or the file). On its first run it adds itself to autostart; the tray menu turns that off.

**How it sees the track.** Through MPRIS, the common Linux way for players to say what plays. Spotify, browsers (Firefox, Chrome, Chromium, Brave, Yandex Browser), Telegram, VLC, Rhythmbox, Strawberry, Elisa and nearly every other player do it. A site in a browser tab is told by the address of the page or its picture: YouTube, YouTube Music, SoundCloud, Yandex Music, VK, Twitch. When a browser gives no address, the status shows the browser's name; `names` helps then.

**The tray icon.** KDE, Cinnamon, XFCE, MATE and Budgie show the raven by themselves. GNOME (Ubuntu, Fedora) has no tray icons out of the box: it needs the [AppIndicator](https://extensions.gnome.org/extension/615/appindicator-support/) extension (Ubuntu has it on already; on Fedora `sudo dnf install gnome-shell-extension-appindicator`, then log out and in). Without the icon the program works all the same, there is just no menu to see. The menu: the current track, *Hide status for an hour*, *Own status instead of the track*, autostart, settings, log, diagnostics, exit.

**Discord from Flatpak and Snap** is found by itself, nothing to set up. Discord in a browser does not work, as on Windows. With Vesktop, its arRPC (statuses from programs) has to be on.

**Settings** live in `~/.config/discord-nowcast/config.json`, with the keys of the table above. There is no settings window on Linux: *Open settings* in the menu opens this file, and the program picks up the changes as soon as it is saved. For your own status, `ownPicture` sets the picture: a path to any image, such as `"~/Pictures/avatar.png"`, and `fallbackPicture` the one for tracks without a cover. The program scales the picture down and uploads it itself. The log is `~/.config/discord-nowcast/log.txt`. *Diagnostics* saves and opens the text for a support question; `discord-nowcast --diagnostics` prints the same in a terminal.

**Updates.** The program does not update itself on Linux: when a new version is out, the menu shows *Version … is out*. A package updates the way it was installed; an AppImage is downloaded again.

## How it works

Windows already knows what is playing: it is the track card that pops up when you press the volume keys. discord-nowcast reads those system media sessions twice a second and, when something changes, sends the status to Discord over its local IPC pipe, the same one games use.

- **Source name.** Taken from the app's ID. A site installed as an app (SoundCloud or YouTube Music via *Install* in Chrome or Edge) is named as in the Start menu. In a regular browser tab the site is told by the window title: the window whose title holds the track is searched for YouTube, Twitch, SoundCloud and the rest. So the status says SoundCloud, not Chrome.
- **"Listening to SoundCloud".** Discord puts the name of the application that sent the status after "Listening to". So each service has a Discord application of its own, and discord-nowcast reconnects to the right one when the source changes. YouTube and Twitch go out as "Watching", except YouTube music channels ("Artist - Topic" and VEVO): those read "Listening to YouTube".
- **Cover art.** The player's picture is scaled to 300 pixels. Uploads try Cloudflare first and then the program's server on a network error, timeout or temporary failure. After such a failure Cloudflare is skipped for 30 minutes; the next upload after the deadline tries it first again. Player covers are kept for one hour and renew after 48 minutes while needed. Manually picked pictures are kept for six hours and renew after 4 hours 48 minutes; only those appear in the moderation gallery. A new status is sent immediately, with its image following in the background. Direct CDN links and iTunes search work as before.
- **The button.** Players do not tell Windows the track's link, so "Listen on …" opens a search for the track in the same service; for Yandex Music, Telegram and browsers, a YouTube search (with a Yandex Music link Discord does not show the status to others). Discord does not let you click your own buttons, only other people's.
- **Rate limit.** Discord accepts at most five updates per 20 seconds, so the status changes at most once every four seconds.

## Limitations

- The app has to tell Windows what it plays. Almost every app does; if it is missing under *What Windows sees*, discord-nowcast cannot see it either.
- It needs a regular Windows 10 or 11. Stripped-down builds (Tiny11, Atlas and the like) often lack the part that players report tracks through: there is no track by the volume control, and the tray icon says "This Windows build has no media support". No program can see the track there.
- Yandex Browser does not tell Windows what plays in its tabs (seen with VK Music and SoundCloud; its flags do not help), so discord-nowcast cannot see it. Edge, Chrome, Firefox and other browsers work.
- It needs the Discord desktop app. Discord in a browser cannot talk to programs on the computer.
- The site in a browser tab is told by the window title, and Windows only shows the title of the tab in front. A track that started in a background tab shows up as the browser, Chrome for instance: switch to the music's tab, install the site as an app, or name it yourself in `names`.
- The progress bar needs the app to report the track length. Yandex Music, Spotify and most sites do; Telegram does not, so it shows elapsed time.

## Privacy

The artist and title go to Discord and, when enabled, to lrclib.net for lyrics and iTunes for cover search. Images upload through Cloudflare or directly to the program's server; unrelated file hosts are not used. The Worker forwards to that same storage, so these are two access paths to one storage service. Player covers are kept for one hour, manually picked pictures for six hours after their last upload. Both renew while needed. Only manually picked images appear in the moderation gallery. Limits and bans use a secret-keyed IP hash, not raw addresses in the database. Blocks and banned hashes survive cleanup. Update checks contact GitHub.

## Building it yourself

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

It uses the C# compiler that ships with .NET Framework 4.8 on every Windows 10 and 11, so there is nothing to install. The code is in `src`.

The checks (status lines, the button, long titles, the cover, the track time) run with:

```
powershell -ExecutionPolicy Bypass -File tests\run.ps1
```

The Linux version is written in Go and lives in `linux`. It is built and checked with (the tests need a D-Bus bus, which `dbus-run-session` gives them):

```
cd linux
go build -o discord-nowcast .
dbus-run-session -- go test ./...
```

On GitHub the program is built and checked by itself after every change.

## Questions, bugs, ideas

- [Discord server](https://discord.gg/SsANGaMr3M): help, news, status screenshots, mostly in Russian.
- [Discussions](https://github.com/playsin333/discord-nowcast/discussions): questions, ideas, show your setup.
- [Report a bug](https://github.com/playsin333/discord-nowcast/issues/new?template=bug.yml): the diagnostics help the most: tray menu → *Copy diagnostics*, then paste it into the report.

Changes are listed in [CHANGELOG.md](CHANGELOG.md).

License: [MIT](LICENSE).
