using System;
using System.Drawing;
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
        static readonly string[] coverValues = { "player", "itunes", "none" };

        readonly Engine engine;
        readonly ComboBox inList = Choices();
        readonly ComboBox coverSource = Choices();
        readonly Theme.CheckBox buttons = new Theme.CheckBox { Text = Strings.ButtonsOption };
        readonly Theme.CheckBox showPaused = new Theme.CheckBox { Text = Strings.ShowPausedOption };
        readonly Theme.CheckBox hideAlbum = new Theme.CheckBox { Text = Strings.HideAlbumOption };
        // A checkbox per source, in a scrolling box.
        readonly FlowLayoutPanel sources = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Field,
            Padding = new Padding(6, 4, 6, 4),
            Size = new Size(320, 110),
        };
        readonly Theme.CheckBox autostart = new Theme.CheckBox { Text = Strings.Autostart };
        readonly Theme.CheckBox checkUpdates = new Theme.CheckBox { Text = Strings.CheckUpdatesOption };
        bool isLoading;

        public SettingsForm(Engine engine)
        {
            this.engine = engine;
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
            Padding = new Padding(14);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            inList.Items.AddRange(new object[] { Strings.InListSource, Strings.InListArtist, Strings.InListTitle, Strings.InListApp });
            inList.DrawItem += DrawChoice;
            coverSource.Items.AddRange(new object[] { Strings.CoverPlayer, Strings.CoverITunes, Strings.CoverNone });
            coverSource.DrawItem += DrawChoice;

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            layout.Controls.Add(Heading(Strings.StatusHeading));
            layout.Controls.Add(Note(Strings.InListLabel));
            layout.Controls.Add(inList);
            layout.Controls.Add(Note(Strings.CoverLabel));
            layout.Controls.Add(coverSource);
            layout.Controls.Add(buttons);
            layout.Controls.Add(showPaused);
            layout.Controls.Add(hideAlbum);
            layout.Controls.Add(Heading(Strings.Sources));
            layout.Controls.Add(Note(Strings.SourcesNote));
            layout.Controls.Add(sources);
            layout.Controls.Add(Heading(Strings.ProgramHeading));
            layout.Controls.Add(autostart);
            layout.Controls.Add(checkUpdates);

            var server = Link(Strings.DiscordServer, delegate { Program.Open(Program.DiscordInvite, null); });
            server.Margin = new Padding(0, 12, 0, 0);
            layout.Controls.Add(server);

            var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
            links.Controls.Add(Link(Strings.OpenLog, delegate { Program.Open("notepad.exe", Log.FilePath); }));
            links.Controls.Add(Link(Strings.Advanced, delegate { Program.Open("notepad.exe", Settings.FilePath); }));
            links.Controls.Add(new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 3, 0, 0) });
            layout.Controls.Add(links);

            var close = new Button
            {
                Text = Strings.Close,
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
            close.Click += delegate { Close(); };
            CancelButton = close;
            layout.Controls.Add(close);
            Controls.Add(layout);

            Fill();

            inList.SelectedIndexChanged += delegate { Change(s => s.InList = inListValues[inList.SelectedIndex]); };
            coverSource.SelectedIndexChanged += delegate { Change(s => s.CoverSource = coverValues[coverSource.SelectedIndex]); };
            buttons.CheckedChanged += delegate { Change(s => s.Buttons = buttons.Checked); };
            showPaused.CheckedChanged += delegate { Change(s => s.ShowPaused = showPaused.Checked); };
            hideAlbum.CheckedChanged += delegate { Change(s => s.HideAlbum = hideAlbum.Checked); };
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
            var index = Array.IndexOf(inListValues, settings.InList);
            inList.SelectedIndex = index >= 0 ? index : 0;
            var cover = Array.IndexOf(coverValues, settings.CoverSource);
            coverSource.SelectedIndex = cover >= 0 ? cover : 0;
            buttons.Checked = settings.Buttons;
            showPaused.Checked = settings.ShowPaused;
            hideAlbum.Checked = settings.HideAlbum;
            checkUpdates.Checked = settings.CheckUpdates;
            autostart.Checked = Startup.IsEnabled;

            // The apps playing now, plus the hidden ones, so they can come back.
            while (sources.Controls.Count > 0) sources.Controls[0].Dispose();
            foreach (var choice in settings.SourceChoices(engine.Sessions))
            {
                AddSource(choice.Name, choice.App, choice.IsShown);
            }
            if (sources.Controls.Count == 0)
            {
                sources.Controls.Add(new Label { Text = Strings.NoSessions, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(0, 3, 0, 0) });
            }
            isLoading = false;
        }

        void AddSource(string name, string app, bool isShown)
        {
            var box = new Theme.CheckBox { Text = name, Checked = isShown };
            box.CheckedChanged += delegate { Change(s => s.SetHidden(app, name, !box.Checked)); };
            sources.Controls.Add(box);
        }

        // The list's lines, closed and open, in the window's colors.
        void DrawChoice(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var isSelected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var back = new SolidBrush(isSelected ? Theme.Accent : Theme.Field)) e.Graphics.FillRectangle(back, e.Bounds);
            TextRenderer.DrawText(e.Graphics, ((ComboBox)sender).Items[e.Index].ToString(), e.Font, e.Bounds, isSelected ? Color.White : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
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

        // From the file as it is now, so edits made by hand are kept.
        void Change(Action<Settings> change)
        {
            if (isLoading) return;
            var settings = Settings.Load();
            change(settings);
            settings.Save();
        }

        static Label Heading(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Margin = new Padding(0, 10, 0, 4),
            };
        }

        static Label Note(string text)
        {
            return new Label { Text = text, AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Theme.Muted };
        }

        // A dark drop-down list, drawn by DrawChoice.
        static ComboBox Choices()
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

        static LinkLabel Link(string text, EventHandler click)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 3, 12, 0),
                LinkColor = Theme.Link,
                ActiveLinkColor = Theme.Link,
                VisitedLinkColor = Theme.Link,
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            link.LinkClicked += (sender, e) => click(sender, e);
            return link;
        }

    }
}
