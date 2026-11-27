using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Strings = Nowcast.Text;

namespace Nowcast
{
    // The settings window from the tray menu. Every change is saved to
    // config.json at once, and the engine picks it up from there.
    class SettingsForm : Form
    {
        // What the member list shows after "Listening to", in the order of the list.
        static readonly string[] inListValues = { "source", "artist", "title", "app" };
        // Where the cover comes from, in the order of the list.
        static readonly string[] coverValues = { "player", "itunes", "picture", "none" };

        readonly Engine engine;
        readonly Panel page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Back };
        readonly FlowLayoutPanel body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 8, 0, 24),
            Margin = Padding.Empty,
            BackColor = Theme.Back,
        };
        readonly List<FlowLayoutPanel> cards = new List<FlowLayoutPanel>();
        Action openAppsPage, closePage;
        bool embedded;
        Settings displayedSettings;
        DateTime settingsWritten;
        readonly StatusPreview preview = new StatusPreview { Visible = false };
        readonly Button previewToggle = OwnForm.Flat(Strings.ShowPreview, false);
        readonly Button moreToggle = OwnForm.Flat(Strings.MoreOptions, false);
        readonly FlowLayoutPanel more = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Visible = false };
        readonly Timer refreshTimer = new Timer { Interval = 1000 };
        string sourcesKey;
        bool previewExpanded, moreExpanded;
        bool layingOut;
        readonly ComboBox inList = Choices();
        readonly TextBox activityName = OwnForm.Line();
        readonly Theme.CheckBox invisibleName = new Theme.CheckBox { Text = Strings.InvisibleActivityName };
        readonly ComboBox coverSource = Choices();
        readonly Label fallbackLabel = Note(Strings.FallbackLabel);
        readonly PicturePicker fallback = new PicturePicker(Settings.FallbackPicturePath, "Fallback picture");
        readonly Theme.CheckBox buttons = new Theme.CheckBox { Text = Strings.ButtonsOption };
        readonly Theme.CheckBox showPaused = new Theme.CheckBox { Text = Strings.ShowPausedOption };
        readonly Theme.CheckBox hideAlbum = new Theme.CheckBox { Text = Strings.HideAlbumOption };
        readonly Theme.CheckBox lyrics = new Theme.CheckBox { Text = Strings.LyricsOption };
        readonly Timer activityNameTimer = new Timer { Interval = 650 };
        // A checkbox per source, in a scrolling box.
        readonly FlowLayoutPanel sources = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Field,
            Padding = new Padding(6, 4, 6, 4),
            Size = new Size(320, 1),
        };
        readonly Theme.CheckBox autostart = new Theme.CheckBox { Text = Strings.Autostart };
        readonly Theme.CheckBox checkUpdates = new Theme.CheckBox { Text = Strings.CheckUpdatesOption };
        bool isLoading;

        public SettingsForm(Engine engine, Action openAppsPage = null, Action closePage = null)
        {
            this.engine = engine;
            this.openAppsPage = openAppsPage;
            this.closePage = closePage;
            Text = "discord-nowcast";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Keep the longer picture controls accessible on small screens.
            AutoScroll = true;
            Padding = new Padding(14);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            inList.Items.AddRange(new object[] { Strings.InListSource, Strings.InListArtist, Strings.InListTitle, Strings.InListApp });
            inList.DrawItem += DrawChoice;
            coverSource.Items.AddRange(new object[] { Strings.CoverPlayer, Strings.CoverITunes, Strings.CoverPicture, Strings.CoverNone });
            coverSource.DrawItem += DrawChoice;

            activityName.MaxLength = 128;
            var intro = new Label
            {
                Text = Strings.Ru ? "Настройки" : "Settings",
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 22, FontStyle.Bold),
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 0, 4),
            };
            body.Controls.Add(intro);
            body.Controls.Add(WideNote(Strings.Ru
                ? "Настройте, что Nowcast показывает в Discord. Изменения сохраняются автоматически."
                : "Choose what Nowcast shows on Discord. Changes are saved automatically."));
            body.Controls.Add(SectionCard(Strings.StatusHeading, Strings.Ru ? "Можно оставить имя по умолчанию или задать своё." : "Keep the default name or choose your own.",
                previewToggle, preview, Note(Strings.ActivityNameLabel), activityName, invisibleName, WideNote(Strings.ActivityNameNote),
                Note(Strings.InListLabel), inList));

            coverSource.Width = 540;
            inList.Width = 540;
            body.Controls.Add(SectionCard(Strings.Ru ? "Внешний вид статуса" : "Status appearance",
                Strings.Ru ? "Выберите, что Discord покажет рядом с названием трека." : "Choose what Discord displays with the track.",
                Note(Strings.CoverLabel), coverSource, fallbackLabel, fallback, buttons, moreToggle, more));
            more.Controls.Add(showPaused);
            more.Controls.Add(hideAlbum);
            more.Controls.Add(lyrics);
            body.Controls.Add(SectionCard(Strings.Sources,
                Strings.Ru ? "Снимите флажок, чтобы не показывать этот источник в Discord." : "Clear a box to hide that source from Discord.",
                sources, ActionButton(Strings.Ru ? "Приложения и процессы…" : "Apps and processes…", delegate
            {
                if (this.openAppsPage != null) this.openAppsPage();
                else using (var picker = new DesktopAppsForm()) picker.ShowDialog(this);
            })));
            body.Controls.Add(SectionCard(Strings.ProgramHeading,
                Strings.Ru ? "Общие параметры запуска и обновлений." : "Startup and update options.",
                autostart, checkUpdates));

            var server = ActionButton(Strings.DiscordServer, delegate { Program.Open(Program.DiscordInvite, null); });
            server.Margin = new Padding(0, 12, 0, 0);

            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 8, 0, 0) };
            links.Controls.Add(ActionButton(Strings.OpenLog, delegate { Program.Open("notepad.exe", Log.FilePath); }));
            links.Controls.Add(ActionButton(Strings.Advanced, delegate { Program.Open("notepad.exe", Settings.FilePath); }));
            links.Controls.Add(new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 3, 0, 0) });

            var close = new Button
            {
                Text = this.closePage == null ? Strings.Close : (Strings.Ru ? "Назад" : "Back"),
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(0, 12, 0, 0),
                Padding = new Padding(10, 2, 10, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Accent,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
            };
            close.FlatAppearance.BorderSize = 0;
            close.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
            close.FlatAppearance.MouseDownBackColor = Theme.AccentHover;
            close.Click += delegate { if (this.closePage != null) this.closePage(); else Close(); };
            CancelButton = close;
            body.Controls.Add(SectionCard(Strings.Ru ? "Помощь и обслуживание" : "Help and maintenance", null, server, links, close));
            page.Controls.Add(body);
            Controls.Add(page);
            page.Resize += delegate { LayoutBody(); };
            LayoutBody();

            Fill();
            invisibleName.CheckedChanged += delegate
            {
                activityName.Enabled = !invisibleName.Checked;
                Change(s => { s.ActivityName = activityName.Text; s.InvisibleActivityName = invisibleName.Checked; });
            };
            activityName.TextChanged += delegate
            {
                if (isLoading) return;
                activityNameTimer.Stop();
                activityNameTimer.Start();
            };
            activityNameTimer.Tick += delegate
            {
                activityNameTimer.Stop();
                Change(s => { s.ActivityName = activityName.Text; s.InvisibleActivityName = invisibleName.Checked; });
            };
            previewToggle.Click += delegate
            {
                previewExpanded = !previewExpanded;
                preview.Visible = previewExpanded;
                previewToggle.Text = previewExpanded ? Strings.HidePreview : Strings.ShowPreview;
                RefreshPreview(); FitOrLayout(false);
            };
            moreToggle.Click += delegate
            {
                moreExpanded = !moreExpanded;
                more.Visible = moreExpanded;
                moreToggle.Text = moreExpanded ? Strings.LessOptions : Strings.MoreOptions;
                FitOrLayout(false);
            };
            refreshTimer.Tick += delegate { RefreshPanels(); };
            VisibleChanged += delegate
            {
                refreshTimer.Enabled = Visible;
                if (Visible) RefreshPanels();
            };

            inList.SelectedIndexChanged += delegate { Change(s => s.InList = inListValues[inList.SelectedIndex]); };
            coverSource.SelectedIndexChanged += delegate
            {
                ShowPictureRow();
                Change(s => s.CoverSource = coverValues[coverSource.SelectedIndex]);
            };
            buttons.CheckedChanged += delegate { Change(s => s.Buttons = buttons.Checked); };
            showPaused.CheckedChanged += delegate { Change(s => s.ShowPaused = showPaused.Checked); };
            hideAlbum.CheckedChanged += delegate { Change(s => s.HideAlbum = hideAlbum.Checked); };
            lyrics.CheckedChanged += delegate { Change(s => s.Lyrics = lyrics.Checked); };
            checkUpdates.CheckedChanged += delegate { Change(s => s.CheckUpdates = checkUpdates.Checked); };
            autostart.CheckedChanged += delegate
            {
                if (isLoading) return;
                if (autostart.Checked) Startup.Enable();
                else Startup.Disable();
            };
        }

        // The current settings into the controls, without saving them back.
        void Fill()
        {
            isLoading = true;
            var settings = Settings.Load();
            displayedSettings = settings;
            activityName.Text = settings.ActivityName;
            invisibleName.Checked = settings.InvisibleActivityName;
            activityName.Enabled = !invisibleName.Checked;
            settingsWritten = SettingsStamp();
            var index = Array.IndexOf(inListValues, settings.InList);
            inList.SelectedIndex = index >= 0 ? index : 0;
            var cover = Array.IndexOf(coverValues, settings.CoverSource);
            coverSource.SelectedIndex = cover >= 0 ? cover : 0;
            ShowPictureRow();
            buttons.Checked = settings.Buttons;
            showPaused.Checked = settings.ShowPaused;
            hideAlbum.Checked = settings.HideAlbum;
            lyrics.Checked = settings.Lyrics;
            checkUpdates.Checked = settings.CheckUpdates;
            autostart.Checked = Startup.IsEnabled;
            fallback.ShowPicture();

            RefreshSources();
            isLoading = false;
            RefreshPreview();
        }

        // The picture under the cover list: "if a track has no cover", "on every
        // track", or none at all with "No cover, no picture".
        void ShowPictureRow()
        {
            var value = coverValues[Math.Max(0, coverSource.SelectedIndex)];
            fallbackLabel.Text = value == "picture" ? Strings.PictureLabel : Strings.FallbackLabel;
            fallbackLabel.Visible = fallback.Visible = value != "none";
        }

        void AddSource(string name, string app, bool isShown)
        {
            var box = new Theme.CheckBox { Text = name, Checked = isShown };
            box.CheckedChanged += delegate { Change(s => s.SetHidden(app, name, !box.Checked)); };
            sources.Controls.Add(box);
        }

        FlowLayoutPanel SectionCard(string title, string description, params Control[] controls)
        {
            var card = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Field,
                Padding = new Padding(18),
                Margin = new Padding(0, 0, 0, 14),
            };
            card.Controls.Add(Heading(title));
            if (!string.IsNullOrWhiteSpace(description)) card.Controls.Add(WideNote(description));
            foreach (var control in controls)
            {
                control.Margin = new Padding(0, 4, 0, 4);
                card.Controls.Add(control);
            }
            cards.Add(card);
            return card;
        }

        static Label WideNote(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(950, 0),
                ForeColor = Theme.Muted,
                Tag = "wide-note",
                Margin = new Padding(0, 0, 0, 4),
            };
        }

        void LayoutBody()
        {
            if (layingOut || page.ClientSize.Width <= 0) return;
            layingOut = true;
            try
            {
                int width = Math.Max(560, Math.Min(1060, page.ClientSize.Width - 52));
                int controlWidth = Math.Max(500, width - 72);
                body.Width = width;
                body.Left = Math.Max(24, (page.ClientSize.Width - width) / 2);
                activityName.Width = controlWidth;
                inList.Width = coverSource.Width = controlWidth;
                sources.Width = controlWidth;
                SetWideNoteWidths(body, controlWidth);
                foreach (var card in cards)
                {
                    // AutoSize otherwise shrinks cards to their shortest child,
                    // making the settings page look like unrelated columns.
                    card.MinimumSize = new Size(width, 0);
                    card.Width = width;
                    card.PerformLayout();
                }
                body.PerformLayout();
                body.Height = body.GetPreferredSize(new Size(width, 0)).Height;
                page.AutoScrollMinSize = new Size(body.Left + width + 24, body.Top + body.Height + 24);
            }
            finally { layingOut = false; }
        }

        static void SetWideNoteWidths(Control parent, int width)
        {
            foreach (Control child in parent.Controls)
            {
                var label = child as Label;
                if (label != null && string.Equals(label.Tag as string, "wide-note", StringComparison.Ordinal))
                    label.MaximumSize = new Size(width, 0);
                SetWideNoteWidths(child, width);
            }
        }

        // The list's lines, closed and open, in the window's colors.
        public static void DrawChoice(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var isSelected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var back = new SolidBrush(isSelected ? Theme.Accent : Theme.Field)) e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, ((ComboBox)sender).Items[e.Index].ToString(), e.Font, e.Bounds, isSelected ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!embedded) SettingsForm.FitToScreen(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        public void ShowAgain()
        {
            Fill();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void PrepareEmbedded(Action openApps, Action done)
        {
            embedded = true;
            openAppsPage = openApps;
            closePage = done;
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            AutoSize = false;
            Dock = DockStyle.Fill;
            page.Dock = DockStyle.Fill;
            LayoutBody();
        }

        public void FocusTop()
        {
            page.AutoScrollPosition = Point.Empty;
            page.PerformLayout();
        }

        void FitOrLayout(bool center)
        {
            if (embedded) { LayoutBody(); body.PerformLayout(); PerformLayout(); }
            else FitToScreen(this, center);
        }

        // From the file as it is now, so edits made by hand are kept.
        void Change(Action<Settings> change)
        {
            if (isLoading) return;
            var settings = Settings.Load();
            change(settings);
            settings.Save();
            displayedSettings = settings;
            settingsWritten = SettingsStamp();
            RefreshPreview();
        }

        static DateTime SettingsStamp()
        {
            try { return File.GetLastWriteTimeUtc(Settings.FilePath); }
            catch (IOException) { return DateTime.MinValue; }
            catch (UnauthorizedAccessException) { return DateTime.MinValue; }
        }

        void RefreshPanels()
        {
            bool resized = SettingsStamp() != settingsWritten;
            if (resized) Fill();
            resized = RefreshSources() || resized;
            RefreshPreview();
            if (resized && IsHandleCreated && Visible) FitOrLayout(false);
        }

        bool RefreshSources()
        {
            if (displayedSettings == null) return false;
            var choices = displayedSettings.SourceChoices(engine.Sessions);
            var keys = new List<string>();
            foreach (var choice in choices) keys.Add(choice.App + "|" + choice.Name + "|" + choice.IsShown);
            var key = string.Join("\n", keys);
            if (key == sourcesKey) return false;
            sourcesKey = key;
            sources.SuspendLayout();
            while (sources.Controls.Count > 0) sources.Controls[0].Dispose();
            foreach (var choice in choices) AddSource(choice.Name, choice.App, choice.IsShown);
            sources.BackColor = choices.Count == 0 ? Theme.Back : Theme.Field;
            if (choices.Count == 0)
                sources.Controls.Add(new Label { Text = Strings.NoSessions, AutoSize = true, MaximumSize = new Size(sources.Width - sources.Padding.Horizontal, 0), ForeColor = Theme.Muted, Margin = Padding.Empty });
            int height = sources.Padding.Vertical, rowHeight = 0;
            foreach (Control control in sources.Controls)
            {
                int row = control.GetPreferredSize(Size.Empty).Height + control.Margin.Vertical;
                rowHeight = Math.Max(rowHeight, row);
                height += row;
            }
            sources.Height = Math.Min(height, sources.Padding.Vertical + rowHeight * 5);
            sources.ResumeLayout(true);
            return true;
        }

        void RefreshPreview()
        {
            if (!previewExpanded || displayedSettings == null) return;
            if (displayedSettings.ShowsOwn)
            {
                bool hasImage = File.Exists(Settings.OwnPicturePath);
                preview.Render(StatusPreviewModel.Own(displayedSettings.OwnType, displayedSettings.OwnTitle, displayedSettings.OwnLine2, displayedSettings.OwnMinutes, hasImage, displayedSettings.ActivityNameOverride), hasImage ? Settings.OwnPicturePath : null, null);
                return;
            }
            var snapshot = engine.Preview;
            var track = snapshot == null ? null : snapshot.Track;
            if (track != null && displayedSettings.Hides(track.App, track.Source.Name)) track = null;
            bool covers = displayedSettings.CoverSource != "none";
            string url = covers && track != null ? snapshot.Image : null;
            bool picked = covers && File.Exists(Settings.FallbackPicturePath)
                && (displayedSettings.CoverSource == "picture" || url == null);
            if (displayedSettings.CoverSource == "picture") url = null;
            if (!picked && covers && url == null && displayedSettings.Image != "") url = displayedSettings.Image;
            string lyric = displayedSettings.Lyrics && snapshot != null && track != null ? snapshot.Lyric : null;
            preview.Render(StatusPreviewModel.Music(track, displayedSettings, picked || url != null || (track == null && covers), lyric), picked ? Settings.FallbackPicturePath : null, url);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { refreshTimer.Dispose(); activityNameTimer.Dispose(); }
            base.Dispose(disposing);
        }

        public static Label Heading(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 4),
            };
        }

        public static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Theme.Muted };
        }

        // A dark drop-down list, drawn by DrawChoice.
        public static ComboBox Choices()
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                DrawMode = DrawMode.OwnerDrawFixed,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Field,
                ForeColor = Theme.Text,
                Width = 320,
            };
        }

        internal static void FitToScreen(Form form, bool center = true)
        {
            var area = Screen.FromControl(form).WorkingArea;
            var location = form.Location;
            form.AutoScrollPosition = Point.Empty;
            form.MaximumSize = Size.Empty;
            form.AutoSize = true;
            form.PerformLayout();
            var preferred = form.GetPreferredSize(Size.Empty);
            form.AutoSize = false;
            form.MaximumSize = new Size(area.Width - 24, area.Height - 24);
            int width = preferred.Width + (preferred.Height > form.MaximumSize.Height ? SystemInformation.VerticalScrollBarWidth : 0);
            form.Size = new Size(Math.Min(width, form.MaximumSize.Width), Math.Min(preferred.Height, form.MaximumSize.Height));
            form.Location = center ? new Point(area.Left + (area.Width - form.Width) / 2, area.Top + (area.Height - form.Height) / 2)
                : new Point(Math.Max(area.Left, Math.Min(location.X, area.Right - form.Width)), Math.Max(area.Top, Math.Min(location.Y, area.Bottom - form.Height)));
        }

        static Button ActionButton(string text, EventHandler click)
        {
            var button = OwnForm.Flat(text, false);
            button.Margin = new Padding(0, 0, 8, 0);
            button.Click += click;
            return button;
        }

    }
}
