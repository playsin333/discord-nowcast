using System;
using System.Drawing;
using System.Windows.Forms;
using Strings = Nowcast.Text;

namespace Nowcast
{
    // The own status window from the tray menu: "Listening to nowcast" (or
    // watching, playing) with two lines, a picture and a time of one's own in
    // place of the track. Saved
    // to config.json like the settings; the picture goes to a file of its own.
    class OwnForm : Form
    {
        // The kinds of status, in the order of the list.
        static readonly string[] typeValues = { "listening", "watching", "playing" };

        readonly Theme.CheckBox on = new Theme.CheckBox { Text = Strings.OwnOn };
        readonly ComboBox type = SettingsForm.Choices();
        readonly TextBox title = Line();
        readonly TextBox line2 = Line();
        readonly Label unclean = new Label { Text = Strings.OwnUnclean, AutoSize = true, MaximumSize = new Size(330, 0), ForeColor = Color.FromArgb(0xF2, 0x3F, 0x43), Visible = false };
        readonly PicturePicker picture = new PicturePicker(Settings.OwnPicturePath, "Own picture");
        readonly NumericUpDown hours = Number(Settings.MaxOwnMinutes / 60);
        readonly NumericUpDown minutes = Number(59);
        // The title and the time are saved once typing or clicking stops, not
        // at every letter or step: each save sends the status again.
        readonly Timer typing = new Timer { Interval = 700 };
        bool isLoading;

        public OwnForm()
        {
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

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            layout.Controls.Add(SettingsForm.Heading(Strings.OwnStatus.TrimEnd('…')));
            layout.Controls.Add(on);
            layout.Controls.Add(SettingsForm.Note(Strings.OwnNote));
            type.Items.AddRange(new object[] { Strings.OwnListening, Strings.OwnWatching, Strings.OwnPlaying });
            type.DrawItem += SettingsForm.DrawChoice;
            layout.Controls.Add(Caption(Strings.OwnTypeLabel));
            layout.Controls.Add(type);
            layout.Controls.Add(Caption(Strings.OwnTitleLabel));
            layout.Controls.Add(title);
            layout.Controls.Add(Caption(Strings.OwnLine2Label));
            layout.Controls.Add(line2);
            layout.Controls.Add(unclean);

            layout.Controls.Add(Caption(Strings.OwnPictureLabel));
            layout.Controls.Add(picture);

            layout.Controls.Add(Caption(Strings.OwnTimeLabel));
            var timeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            timeRow.Controls.Add(hours);
            timeRow.Controls.Add(Unit(Strings.Hours));
            timeRow.Controls.Add(minutes);
            timeRow.Controls.Add(Unit(Strings.Minutes));
            layout.Controls.Add(timeRow);

            var close = Flat(Strings.Close, true);
            close.Anchor = AnchorStyles.Right;
            close.Margin = new Padding(0, 12, 0, 0);
            close.Click += delegate { Close(); };
            CancelButton = close;
            layout.Controls.Add(close);
            Controls.Add(layout);

            Fill();

            on.CheckedChanged += delegate { Change(s => s.Own = on.Checked); };
            type.SelectedIndexChanged += delegate { Change(s => s.OwnType = typeValues[type.SelectedIndex]); };
            title.TextChanged += delegate { Later(); };
            line2.TextChanged += delegate { Later(); };
            hours.ValueChanged += delegate { Later(); };
            minutes.ValueChanged += delegate { Later(); };
            typing.Tick += delegate { SaveTyped(); };
            FormClosing += delegate { SaveTyped(); };
        }

        // The current settings and picture into the controls, without saving them back.
        void Fill()
        {
            isLoading = true;
            var settings = Settings.Load();
            on.Checked = settings.Own;
            var index = Array.IndexOf(typeValues, settings.OwnType);
            type.SelectedIndex = index >= 0 ? index : 0;
            title.Text = settings.OwnTitle;
            line2.Text = settings.OwnLine2;
            unclean.Visible = !Settings.IsClean(title.Text) || !Settings.IsClean(line2.Text);
            hours.Value = settings.OwnMinutes / 60;
            minutes.Value = settings.OwnMinutes % 60;
            picture.ShowPicture();
            isLoading = false;
        }

        void Later()
        {
            unclean.Visible = !Settings.IsClean(title.Text) || !Settings.IsClean(line2.Text);
            if (isLoading) return;
            typing.Stop();
            typing.Start();
        }

        void SaveTyped()
        {
            if (!typing.Enabled) return;
            typing.Stop();
            var text = title.Text;
            var second = line2.Text;
            var total = Math.Min(Settings.MaxOwnMinutes, (int)hours.Value * 60 + (int)minutes.Value);
            Change(s =>
            {
                s.OwnTitle = text;
                s.OwnLine2 = second;
                s.OwnMinutes = total;
            });
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

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 3) };
        }

        static TextBox Line()
        {
            return new TextBox
            {
                Width = 320,
                MaxLength = 128,
                BackColor = Theme.Field,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
        }

        static Label Unit(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 6, 12, 0) };
        }

        static NumericUpDown Number(int max)
        {
            return new NumericUpDown
            {
                Minimum = 0,
                Maximum = max,
                Width = 60,
                BackColor = Theme.Field,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle,
            };
        }

        // A flat button: the accent color for the main one, the field color otherwise.
        public static Button Flat(string text, bool isMain)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 6),
                Padding = new Padding(10, 2, 10, 2),
                FlatStyle = FlatStyle.Flat,
                BackColor = isMain ? Theme.Accent : Theme.Field,
                ForeColor = isMain ? Color.White : Theme.Text,
                Cursor = Cursors.Hand,
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = isMain ? Theme.AccentHover : Theme.Back;
            button.FlatAppearance.MouseDownBackColor = isMain ? Theme.AccentHover : Theme.Back;
            return button;
        }
    }
}
