using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Nowcast
{
    // Starting with Windows: a value under HKCU\...\Run pointing at this exe.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "discord-nowcast";

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
        }

        public static bool IsEnabled
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    return key != null && key.GetValue(Name) is string;
                }
            }
        }

        public static void Enable()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                key.SetValue(Name, Command);
            }
        }

        public static void Disable()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key != null) key.DeleteValue(Name, false);
            }
        }

        // The exe moved since it was added: point the entry at it again.
        public static void Refresh()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                var value = key == null ? null : key.GetValue(Name) as string;
                if (value != null && value != Command) key.SetValue(Name, Command);
            }
        }

        static readonly string installMarker = Path.Combine(Log.Folder, "installed-at");

        // A new install is this exe started from a place it has not run from
        // before: just downloaded, or moved. The marker file holds the last
        // place. Settings survive deleting the exe, so they cannot tell.
        public static bool IsNewInstall
        {
            get
            {
                var last = File.Exists(installMarker) ? File.ReadAllText(installMarker).Trim() : null;
                return !string.Equals(last, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Remember the choice for the installation, including after moving the exe.
        // Existing installations keep their current autostart setting.
        public static void Install()
        {
            bool firstRun = !File.Exists(installMarker);
            bool moved = IsNewInstall;
            if (firstRun)
            {
                if (MessageBox.Show(Text.AutostartPrompt, "discord-nowcast", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes) Enable();
                else Disable();
            }
            else Refresh();
            DesktopShortcut(firstRun);
            if (firstRun || moved)
            {
                Directory.CreateDirectory(Log.Folder);
                File.WriteAllText(installMarker, Application.ExecutablePath);
            }
        }

        // create: make it; otherwise only point an existing one at this exe.
        static void DesktopShortcut(bool create)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "discord-nowcast.lnk");
            if (!create && !File.Exists(path)) return;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                var shell = Activator.CreateInstance(shellType);
                var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                var linkType = link.GetType();
                linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath });
                linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { Application.ExecutablePath + ",0" });
                linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "discord-nowcast" });
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch (Exception e)
            {
                Log.Write("Desktop shortcut not made: " + e.Message);
            }
        }
    }
}
