using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nowcast
{
    // The icon by the clock and its menu. Seen only on this computer.
    class TrayApp : ApplicationContext
    {
        readonly Engine engine = new Engine();
        readonly NotifyIcon icon = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        // A window-less control to get back to the UI thread from the engine.
        readonly Control ui = new Control();
        readonly Timer updateTimer = new Timer { Interval = 6 * 60 * 60 * 1000 };

        readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
        readonly ToolStripMenuItem hideItem = new ToolStripMenuItem(Text.HideHour);
        readonly ToolStripMenuItem sourcesItem = new ToolStripMenuItem(Text.Sources);
        readonly ToolStripMenuItem sessionsItem = new ToolStripMenuItem(Text.WhatWindowsSees);
        readonly ToolStripMenuItem autostartItem = new ToolStripMenuItem(Text.Autostart) { CheckOnClick = true };
        readonly ToolStripMenuItem updateItem = new ToolStripMenuItem(Text.CheckUpdates);
        Release release;
        SettingsForm settingsForm;

        public TrayApp(bool isUpdate)
        {
            ui.CreateControl();
            // Dark, like Discord: the menu and every submenu.
            ToolStripManager.Renderer = new Theme.MenuRenderer();

            hideItem.Click += delegate
            {
                if (DateTime.Now < engine.HiddenUntil) engine.Show();
                else engine.Hide(TimeSpan.FromHours(1));
            };
            autostartItem.Click += delegate
            {
                if (autostartItem.Checked) Startup.Enable();
                else Startup.Disable();
            };
            updateItem.Click += delegate { CheckUpdates(true); };
            sessionsItem.DropDownItems.Add(new ToolStripMenuItem(Text.NoSessions) { Enabled = false });

            menu.Items.Add(statusItem);
            menu.Items.Add(hideItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(sourcesItem);
            menu.Items.Add(sessionsItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(Text.Settings, null, delegate { ShowSettings(); });
            menu.Items.Add(Text.OpenLog, null, delegate { Program.Open("notepad.exe", Log.FilePath); });
            menu.Items.Add(updateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Text.DiscordServer, null, delegate { Program.Open(Program.DiscordInvite, null); });
            menu.Items.Add(Text.GitHub, null, delegate { Program.Open("https://github.com/" + Program.Repo, null); });
            menu.Items.Add(Text.Exit, null, delegate { ExitThread(); });
            menu.Opening += delegate { RefreshMenu(); };
            menu.ShowItemToolTips = true;
            sessionsItem.DropDown.ShowItemToolTips = true;

            icon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            icon.Text = "discord-nowcast";
            icon.ContextMenuStrip = menu;
            // A left click opens the same menu as a right one.
            icon.MouseUp += (sender, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(icon, null);
            };
            icon.BalloonTipClicked += delegate
            {
                if (release != null) InstallUpdate();
            };
            icon.Visible = true;

            engine.Changed += delegate
            {
                if (ui.IsHandleCreated) ui.BeginInvoke(new Action(RefreshStatus));
            };

            if (Startup.IsNewInstall) icon.ShowBalloonTip(8000, "discord-nowcast", Text.Running, ToolTipIcon.Info);
            Startup.Install();
            if (isUpdate) icon.ShowBalloonTip(5000, "discord-nowcast", string.Format(Text.Updated, Program.Version), ToolTipIcon.Info);

            engine.Start();

            // First look ten seconds in, then every six hours.
            updateTimer.Tick += delegate { CheckUpdates(false); };
            var firstCheck = new Timer { Interval = 10000 };
            firstCheck.Tick += delegate
            {
                firstCheck.Dispose();
                CheckUpdates(false);
                updateTimer.Start();
            };
            firstCheck.Start();
        }

        void RefreshStatus()
        {
            statusItem.Text = engine.Status;
            // The tooltip holds at most 63 characters.
            var tip = "discord-nowcast\n" + engine.Status;
            icon.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        void RefreshMenu()
        {
            RefreshStatus();
            hideItem.Text = DateTime.Now < engine.HiddenUntil ? Text.ShowAgain : Text.HideHour;
            autostartItem.Checked = Startup.IsEnabled;
            if (release != null) updateItem.Text = string.Format(Text.UpdateTo, release.Tag);

            RefreshSources();

            sessionsItem.DropDownItems.Clear();
            foreach (var track in engine.Sessions)
            {
                var line = Engine.Describe(track) + " (" + (track.IsPlaying ? Text.Playing : Text.Paused) + ")";
                // A click copies the app ID, which a "player not recognized" report needs.
                var app = track.App;
                sessionsItem.DropDownItems.Add(new ToolStripMenuItem(line, null, delegate { Clipboard.SetText(app); }) { ToolTipText = app });
            }
            if (sessionsItem.DropDownItems.Count == 0)
            {
                sessionsItem.DropDownItems.Add(new ToolStripMenuItem(Text.NoSessions) { Enabled = false });
            }
        }

        // One window at a time: a second click brings the open one forward.
        void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed) settingsForm = new SettingsForm(engine);
            settingsForm.ShowAgain();
        }

        // A checkbox per source: a cleared box keeps that app out of the status.
        void RefreshSources()
        {
            sourcesItem.DropDownItems.Clear();
            foreach (var choice in engine.Settings.SourceChoices(engine.Sessions))
            {
                AddSource(choice.Name, choice.App, choice.IsShown);
            }
            if (sourcesItem.DropDownItems.Count == 0)
            {
                sourcesItem.DropDownItems.Add(new ToolStripMenuItem(Text.NoSessions) { Enabled = false });
            }
        }

        void AddSource(string name, string app, bool isShown)
        {
            var item = new ToolStripMenuItem(name) { Checked = isShown, CheckOnClick = true };
            item.Click += delegate
            {
                // From the file as it is now, so edits made by hand are kept.
                var settings = Settings.Load();
                settings.SetHidden(app, name, !item.Checked);
                settings.Save();
            };
            sourcesItem.DropDownItems.Add(item);
        }

        // byHand: from the menu, so say the result either way and install
        // what was found; on the timer, only announce a new version.
        void CheckUpdates(bool byHand)
        {
            if (!byHand && !engine.Settings.CheckUpdates) return;
            if (byHand && release != null)
            {
                InstallUpdate();
                return;
            }
            Task.Factory.StartNew(() => Updater.FindNewer()).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    Log.Write("Update check failed: " + task.Exception.GetBaseException().Message);
                    if (byHand) Say(string.Format(Text.UpdateFailed, task.Exception.GetBaseException().Message));
                    return;
                }
                if (task.Result == null)
                {
                    if (byHand) Say(string.Format(Text.UpToDate, Program.Version));
                    return;
                }
                release = task.Result;
                Log.Write("Found " + release.Tag);
                if (byHand) InstallUpdate();
                else icon.ShowBalloonTip(10000, "discord-nowcast", string.Format(Text.UpdateAvailable, release.Tag), ToolTipIcon.Info);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void InstallUpdate()
        {
            var target = release;
            Task.Factory.StartNew(() => Updater.Install(target)).ContinueWith(task =>
            {
                if (!task.IsFaulted)
                {
                    ExitThread();
                    return;
                }
                var message = task.Exception.GetBaseException().Message;
                Log.Write("Update failed: " + message);
                Say(string.Format(Text.UpdateFailed, message));
                // Perhaps no rights to the folder: the release page still works.
                if (target.PageUrl != null) Program.Open(target.PageUrl, null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Say(string text)
        {
            icon.ShowBalloonTip(6000, "discord-nowcast", text, ToolTipIcon.Info);
        }

        protected override void ExitThreadCore()
        {
            icon.Visible = false;
            engine.Stop();
            icon.Dispose();
            updateTimer.Dispose();
            base.ExitThreadCore();
        }
    }
}
