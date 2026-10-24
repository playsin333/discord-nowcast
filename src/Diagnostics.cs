using System;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Nowcast
{
    // Everything a support question needs, as one text for the clipboard:
    // the version, Windows, what the program sees, the settings and the end
    // of the log. It is pasted in public, so the user's folder is masked.
    static class Diagnostics
    {
        const int LogLines = 40;

        public static string Build(Engine engine)
        {
            var text = new StringBuilder();
            text.AppendLine("discord-nowcast " + Program.Version);
            text.AppendLine("Windows: " + WindowsVersion() + ", " + System.Globalization.CultureInfo.CurrentUICulture.Name);
            text.AppendLine("Status: " + engine.Status);
            text.AppendLine("Discord: " + engine.DiscordState);
            text.AppendLine();
            text.AppendLine("What Windows sees:");
            var sessions = engine.Sessions;
            if (sessions.Count == 0) text.AppendLine("  nothing");
            foreach (var track in sessions)
            {
                text.AppendLine("  " + Engine.Describe(track) + " (" + (track.IsPlaying ? "playing" : "paused") +
                    ", position " + (int)track.Position + " s" + (track.Duration > 0 ? " of " + (int)track.Duration : ", no length") +
                    (track.UpdatedAt > 0 ? ", updated " + DateTimeOffset.FromUnixTimeMilliseconds(track.UpdatedAt).ToLocalTime().ToString("HH:mm:ss") : ", no update time") +
                    (track.Album != "" ? ", album" : "") + (track.Thumbnail != null ? ", picture" : "") + ") [" + track.App + "]");
            }
            text.AppendLine();
            text.AppendLine("config.json:");
            text.AppendLine(Read(Settings.FilePath) ?? "  (none)");
            text.AppendLine("log.txt (end):");
            var log = Read(Log.FilePath);
            if (log != null)
            {
                var lines = log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                for (var i = Math.Max(0, lines.Length - LogLines); i < lines.Length; i++) text.AppendLine(lines[i]);
            }
            return Mask(text.ToString());
        }

        // "Windows 10 Pro 22H2, build 19045.5011". Windows 11 still calls itself
        // Windows 10 in the registry; the build tells them apart (22000 and up is 11).
        static string WindowsVersion()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        var name = key.GetValue("ProductName") as string ?? "Windows";
                        var release = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
                        var build = key.GetValue("CurrentBuild") as string;
                        var revision = key.GetValue("UBR");
                        return name + (release != null ? " " + release : "") +
                            (build != null ? ", build " + build + (revision != null ? "." + revision : "") : "");
                    }
                }
            }
            catch (Exception)
            {
            }
            return Environment.OSVersion.VersionString;
        }

        // The file as it is now; the log is open for writing, so it is shared.
        static string Read(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // C:\Users\name\... -> %USERPROFILE%\...: the account name stays private.
        static string Mask(string text)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (profile.Length <= 3) return text;
            // As written in the log, and as config.json escapes it.
            text = text.Replace(profile, "%USERPROFILE%");
            return text.Replace(profile.Replace(@"\", @"\\"), "%USERPROFILE%");
        }
    }
}
