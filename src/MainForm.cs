using System;
using System.Drawing;
using System.Windows.Forms;

namespace Nowcast
{
    // One-window app shell. The tray is only a shortcut and a place to reopen it.
    sealed class MainForm : Form
    {
        readonly Engine engine;
        readonly Action exit;
        readonly Panel content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back };
        readonly Panel home = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Back, Padding = new Padding(30) };
        readonly Label current = new Label();
        readonly FlowLayoutPanel configured = new FlowLayoutPanel();
        readonly Button hideToggle = OwnForm.Flat(Nowcast.Text.HideHour, false);
        readonly Button homeNav, appsNav, sitesNav, settingsNav, ownNav;
        SettingsForm settingsPage;
        BrowserSitesForm sitesPage;
        DesktopAppsForm appsPage;
        OwnForm ownPage;
        bool allowClose;

        public MainForm(Engine engine, Action exit)
        {
            this.engine = engine;
            this.exit = exit;
            Text = "discord-nowcast";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(850, 600);
            Size = new Size(1050, 740);
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            var sidebar = new Panel { Dock = DockStyle.Left, Width = 205, BackColor = Theme.Field, Padding = new Padding(16, 22, 12, 18) };
            var brand = new Label { Text = "nowcast", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 20, FontStyle.Bold), ForeColor = Color.White, Location = new Point(16, 20) };
            var subbrand = new Label { Text = "DISCORD PRESENCE", AutoSize = true, ForeColor = Theme.Muted, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8, FontStyle.Bold), Location = new Point(18, 52) };
            sidebar.Controls.Add(brand);
            sidebar.Controls.Add(subbrand);
            var nav = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(10, 100), Padding = new Padding(0, 0, 0, 0), Margin = Padding.Empty, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            homeNav = NavButton(Nowcast.Text.Ru ? "Главная" : "Home");
            appsNav = NavButton(Nowcast.Text.Ru ? "Приложения" : "Apps");
            sitesNav = NavButton(Nowcast.Text.Ru ? "Сайты" : "Websites");
            settingsNav = NavButton(Nowcast.Text.Ru ? "Настройки" : "Settings");
            ownNav = NavButton(Nowcast.Text.Ru ? "Свой статус" : "Custom status");
            var exitNav = NavButton(Nowcast.Text.Ru ? "Выход" : "Exit");
            homeNav.Click += delegate { ShowHomePage(); };
            appsNav.Click += delegate { ShowAppsPage(); };
            sitesNav.Click += delegate { ShowSitesPage(); };
            settingsNav.Click += delegate { ShowSettingsPage(); };
            ownNav.Click += delegate { ShowOwnPage(); };
            exitNav.Click += delegate { this.exit(); };
            nav.Controls.Add(homeNav);
            nav.Controls.Add(appsNav);
            nav.Controls.Add(sitesNav);
            nav.Controls.Add(settingsNav);
            nav.Controls.Add(ownNav);
            nav.Controls.Add(exitNav);
            sidebar.Controls.Add(nav);
            var footer = new Label { Text = "v" + Program.Version, AutoSize = true, ForeColor = Theme.Muted, Dock = DockStyle.Bottom, Padding = new Padding(0, 0, 0, 3) };
            sidebar.Controls.Add(footer);
            Controls.Add(content);
            Controls.Add(sidebar);

            BuildHome();
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!allowClose) { e.Cancel = true; Hide(); }
            };
            VisibleChanged += delegate { if (Visible) { RefreshFromEngine(); RefreshApps(); } };
            Theme.DarkTitleBar(this);
            ShowHomePage();
        }

        static Button NavButton(string text)
        {
            var button = new Button { Text = "   " + text, Width = 172, Height = 42, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat, BackColor = Theme.Field, ForeColor = Theme.Text, Cursor = Cursors.Hand, Margin = new Padding(0, 4, 0, 4) };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Theme.Back;
            return button;
        }

        void BuildHome()
        {
            var title = new Label { Text = Nowcast.Text.Ru ? "Главная" : "Home", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 22, FontStyle.Bold), Location = new Point(30, 26) };
            var subtitle = new Label { Text = Nowcast.Text.Ru ? "Управляйте тем, что видно в вашем статусе Discord." : "Choose what appears in your Discord status.", AutoSize = true, ForeColor = Theme.Muted, Location = new Point(32, 66) };
            home.Controls.Add(title);
            home.Controls.Add(subtitle);

            var statusCard = new Panel { BackColor = Theme.Field, Location = new Point(30, 112), Size = new Size(760, 118), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, Padding = new Padding(20) };
            statusCard.Controls.Add(new Label { Text = Nowcast.Text.Ru ? "СЕЙЧАС В DISCORD" : "CURRENT DISCORD STATUS", AutoSize = true, ForeColor = Theme.Muted, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 8, FontStyle.Bold), Location = new Point(20, 16) });
            current.AutoSize = true;
            current.MaximumSize = new Size(700, 0);
            current.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold);
            current.Location = new Point(20, 48);
            statusCard.Controls.Add(current);
            home.Controls.Add(statusCard);

            var heading = new Label { Text = Nowcast.Text.Ru ? "Отслеживаемые приложения" : "Tracked apps", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Location = new Point(32, 258) };
            home.Controls.Add(heading);
            configured.Dock = DockStyle.Top;
            configured.Location = new Point(26, 294);
            configured.Height = 220;
            configured.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            configured.FlowDirection = FlowDirection.TopDown;
            configured.WrapContents = false;
            configured.AutoScroll = true;
            configured.Padding = new Padding(4);
            home.Controls.Add(configured);

            var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Location = new Point(30, 540), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            var add = OwnForm.Flat(Nowcast.Text.Ru ? "Добавить приложение" : "Add an app", true);
            var settings = OwnForm.Flat(Nowcast.Text.Ru ? "Настройки" : "Settings", false);
            var custom = OwnForm.Flat(Nowcast.Text.Ru ? "Свой статус" : "Custom status", false);
            add.Click += delegate { ShowAppsPage(); };
            settings.Click += delegate { ShowSettingsPage(); };
            custom.Click += delegate { ShowOwnPage(); };
            hideToggle.Click += delegate
            {
                if (DateTime.Now < engine.HiddenUntil) engine.Show();
                else engine.Hide(TimeSpan.FromHours(1));
                RefreshFromEngine();
            };
            actions.Controls.Add(add);
            actions.Controls.Add(settings);
            actions.Controls.Add(custom);
            actions.Controls.Add(hideToggle);
            home.Controls.Add(actions);
        }

        public void ShowAgain()
        {
            RefreshFromEngine();
            RefreshApps();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        public void ShowHomePage()
        {
            SetPage(home, homeNav);
            RefreshFromEngine();
            RefreshApps();
        }

        public void ShowAppsPage()
        {
            if (appsPage == null || appsPage.IsDisposed)
            {
                appsPage = new DesktopAppsForm(ShowHomePage);
                appsPage.PrepareEmbedded();
            }
            SetPage(appsPage, appsNav);
        }

        public void ShowSettingsPage()
        {
            EnsureSettingsPage();
            SetPage(settingsPage, settingsNav);
            settingsPage.FocusTop();
        }

        public void ShowSitesPage()
        {
            if (sitesPage == null || sitesPage.IsDisposed)
            {
                sitesPage = new BrowserSitesForm();
                sitesPage.PrepareEmbedded();
            }
            SetPage(sitesPage, sitesNav);
        }

        void EnsureSettingsPage()
        {
            if (settingsPage != null && !settingsPage.IsDisposed) return;
            settingsPage = new SettingsForm(engine, ShowAppsPage, ShowHomePage);
            settingsPage.PrepareEmbedded(ShowAppsPage, ShowHomePage);
        }

        public void ShowOwnPage()
        {
            if (ownPage == null || ownPage.IsDisposed)
            {
                ownPage = new OwnForm(ShowHomePage);
                ownPage.PrepareEmbedded(ShowHomePage);
            }
            SetPage(ownPage, ownNav);
        }

        void SetPage(Control page, Button active)
        {
            content.SuspendLayout();
            content.Controls.Clear();
            page.Dock = DockStyle.Fill;
            content.Controls.Add(page);
            var form = page as Form;
            if (form != null) form.Show();
            page.BringToFront();
            content.ResumeLayout(true);
            foreach (var button in new[] { homeNav, appsNav, sitesNav, settingsNav, ownNav })
            {
                button.BackColor = button == active ? Theme.Accent : Theme.Field;
                button.ForeColor = button == active ? Color.White : Theme.Text;
            }
        }

        public void RefreshFromEngine()
        {
            current.Text = engine.Status;
            hideToggle.Text = DateTime.Now < engine.HiddenUntil ? Nowcast.Text.ShowAgain : Nowcast.Text.HideHour;
        }

        void RefreshApps()
        {
            configured.SuspendLayout();
            configured.Controls.Clear();
            foreach (var pair in Settings.Load().Executables)
            {
                var key = pair.Key;
                var isProcess = key.StartsWith("process|", StringComparison.OrdinalIgnoreCase);
                var source = key.StartsWith("exe|", StringComparison.OrdinalIgnoreCase) ? key.Substring(4) : isProcess ? key.Substring(8) : key;
                var type = isProcess ? (Nowcast.Text.Ru ? "процесс" : "process") : "EXE";
                var label = new Label { Text = "●   " + pair.Value + "    ·    " + type + "    " + source, AutoSize = true, MaximumSize = new Size(740, 0), ForeColor = Theme.Text, Margin = new Padding(4, 6, 4, 6) };
                configured.Controls.Add(label);
            }
            if (configured.Controls.Count == 0)
                configured.Controls.Add(new Label { Text = Nowcast.Text.Ru ? "Пока пусто. Нажмите «Добавить приложение», чтобы выбрать окно или .exe." : "Nothing here yet. Add an app to choose an open window or .exe.", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(4, 8, 4, 4) });
            configured.ResumeLayout(true);
        }

        public void Shutdown()
        {
            allowClose = true;
            Close();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }
    }
}
