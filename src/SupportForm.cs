using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Strings = Nowcast.Text;

namespace Nowcast
{
    // Optional support, shown once per Windows profile, independent of exe location.
    class SupportForm : Form
    {
        internal const string Wallet = "TS8tAA4nV6xqrHVJUZSUC7fDdb9P8pML3H";
        internal const string Rubles = "https://t.tb.ru/pm_short/4V9Cl4H1CdZ";
        internal const string Telegram = "https://t.me/playsin333tgk";
        static readonly string SeenPath = Path.Combine(Log.Folder, "support-shown");

        public static bool ShouldShow { get { return !File.Exists(SeenPath); } }

        public SupportForm(bool donationOnly = false)
        {
            Text = donationOnly ? Strings.Donate : Strings.SupportProject;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(20);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            var layout = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
                Margin = Padding.Empty, Padding = Padding.Empty
            };
            layout.Controls.Add(SettingsForm.Heading(donationOnly ? Strings.DonateChoose : Strings.SupportThanks));
            if (!donationOnly) layout.Controls.Add(Note(Strings.SupportMessage));
            var rubles = Button(donationOnly ? Strings.DonateRubles : Strings.SupportRubles, false);
            rubles.Click += delegate { Program.Open(Rubles, null); };
            layout.Controls.Add(rubles);
            var crypto = new FlowLayoutPanel {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
                Visible = !donationOnly, Margin = Padding.Empty
            };
            if (donationOnly) {
                var trc20 = Button("USDT · TRC20 (TRON)", false);
                trc20.Click += delegate { crypto.Visible = true; };
                layout.Controls.Add(trc20);
            }
            crypto.Controls.Add(Note("USDT · TRC20 (TRON)"));
            var address = new TextBox {
                Text = Wallet, ReadOnly = true, Width = 440,
                BackColor = Theme.Field, ForeColor = Theme.Text,
                BorderStyle = BorderStyle.FixedSingle
            };
            crypto.Controls.Add(address);
            var copy = Button(Strings.SupportCopy, false);
            copy.Click += delegate {
                try { Clipboard.SetText(Wallet); copy.Text = Strings.SupportCopied; }
                catch (Exception e) {
                    Log.Write("Support address not copied: " + e.GetType().Name);
                    address.Focus(); address.SelectAll();
                }
            };
            crypto.Controls.Add(copy);
            layout.Controls.Add(crypto);
            if (!donationOnly) {
                layout.Controls.Add(Note(Strings.SupportSocial));
                var telegram = Button(Strings.SupportTelegram, false);
                telegram.Click += delegate { Program.Open(Telegram, null); };
                layout.Controls.Add(telegram);
            }
            var close = Button(donationOnly ? Strings.Close : Strings.SupportContinue, true);
            close.Margin = new Padding(3, 16, 3, 3);
            close.Click += delegate { Close(); };
            layout.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            Controls.Add(layout);

            Shown += delegate {
                if (donationOnly) return;
                try {
                    Directory.CreateDirectory(Log.Folder);
                    File.WriteAllText(SeenPath, "shown");
                }
                catch (Exception e) { Log.Write("Support window preference not saved: " + e.GetType().Name); }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }

        static Label Note(string message)
        {
            return new Label { Text = message, AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 6, 3, 8) };
        }

        static Button Button(string label, bool primary)
        {
            var button = new Button {
                Text = label, AutoSize = true, FlatStyle = FlatStyle.Flat,
                Padding = new Padding(12, 5, 12, 5), Cursor = Cursors.Hand,
                BackColor = primary ? Theme.Accent : Theme.Field,
                ForeColor = primary ? Color.White : Theme.Text
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.Hover;
            return button;
        }
    }
}
