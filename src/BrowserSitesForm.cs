using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Nowcast
{
    // Browser allowlists have their own page so website choices do not look
    // like another group of unrelated general settings.
    sealed class BrowserSitesForm : Form
    {
        readonly Panel page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Back };
        readonly FlowLayoutPanel body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(24, 18, 24, 24),
            Margin = Padding.Empty,
            BackColor = Theme.Back,
        };
        readonly FlowLayoutPanel card = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Field,
            Padding = new Padding(20),
            Margin = new Padding(0, 14, 0, 0),
        };
        readonly ComboBox browser = SettingsForm.Choices();
        readonly Label browserLabel = SettingsForm.Note(Nowcast.Text.Ru ? "Браузер" : "Browser");
        readonly Label help = new Label
        {
            Text = Nowcast.Text.Ru
                ? "Откройте сайт в выбранном браузере — он появится в списке. Отметьте только сайты, которые хотите показывать в Discord."
                : "Open a website in the selected browser and it will appear here. Check only the sites you want to show on Discord.",
            AutoSize = true,
            ForeColor = Theme.Muted,
        };
        readonly Label activeTabNote = new Label
        {
            Text = Nowcast.Text.Ru
                ? "Nowcast распознаёт активные вкладки открытых окон браузера. Настройки хранятся отдельно для каждого браузера."
                : "Nowcast detects active tabs in open browser windows. Settings are kept separately for each browser.",
            AutoSize = true,
            ForeColor = Theme.Muted,
        };
        readonly Panel siteViewport = new Panel { AutoScroll = true, BackColor = Theme.Field, Margin = new Padding(0, 8, 0, 0) };
        readonly FlowLayoutPanel siteRows = new FlowLayoutPanel
        {
            AutoSize = false,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Field,
            Padding = new Padding(8),
            Margin = Padding.Empty,
        };
        readonly Dictionary<string, Theme.CheckBox> checks = new Dictionary<string, Theme.CheckBox>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Label> rowStates = new Dictionary<string, Label>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> siteValues = new List<string>();
        readonly string[] browserValues = { "Firefox", "Chrome", "Edge", "Yandex Browser", "Opera", "Vivaldi", "Brave", "Thorium" };
        readonly Timer refreshTimer = new Timer { Interval = 1000 };
        string optionsKey;
        HashSet<string> activeSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool loading, embedded, layingOut;

        public BrowserSitesForm()
        {
            Text = "discord-nowcast";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;

            var title = new Label
            {
                Text = Nowcast.Text.Ru ? "Сайты" : "Websites",
                AutoSize = true,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 22, FontStyle.Bold),
                ForeColor = Color.White,
                Margin = new Padding(0, 0, 0, 4),
            };
            var subtitle = new Label
            {
                Text = Nowcast.Text.Ru ? "Выберите сайты, которые Nowcast может показывать в Discord." : "Choose which websites Nowcast can show on Discord.",
                AutoSize = true,
                ForeColor = Theme.Muted,
                Margin = new Padding(0, 0, 0, 4),
            };

            browser.DropDownStyle = ComboBoxStyle.DropDownList;
            browser.DrawItem += SettingsForm.DrawChoice;
            foreach (var name in browserValues) browser.Items.Add(BrowserLabel(name));
            browser.SelectedIndex = 0;
            siteViewport.Controls.Add(siteRows);

            var cardHeading = SettingsForm.Heading(Nowcast.Text.Ru ? "Фильтр сайтов" : "Website filter");
            cardHeading.Margin = new Padding(0, 2, 0, 4);
            card.Controls.Add(cardHeading);
            help.MaximumSize = new Size(940, 0);
            help.Margin = new Padding(0, 0, 0, 8);
            activeTabNote.MaximumSize = new Size(940, 0);
            activeTabNote.Margin = new Padding(0, 6, 0, 0);
            browserLabel.Margin = new Padding(0, 4, 0, 3);
            card.Controls.Add(help);
            card.Controls.Add(browserLabel);
            card.Controls.Add(browser);
            card.Controls.Add(siteViewport);
            card.Controls.Add(activeTabNote);

            body.Controls.Add(title);
            body.Controls.Add(subtitle);
            body.Controls.Add(card);
            page.Controls.Add(body);
            Controls.Add(page);

            browser.SelectedIndexChanged += delegate
            {
                if (loading) return;
                optionsKey = null;
                RefreshSites();
            };
            refreshTimer.Tick += delegate { RefreshSites(); };
            VisibleChanged += delegate
            {
                refreshTimer.Enabled = Visible;
                if (Visible) RefreshSites();
            };
            page.Resize += delegate { LayoutBody(); };
            siteViewport.Resize += delegate { LayoutRows(); };
            RefreshSites();
            LayoutBody();
        }

        string BrowserName()
        {
            if (browser.SelectedIndex < 0) return null;
            var name = browserValues[browser.SelectedIndex];
            return name == "Yandex Browser" && Nowcast.Text.Ru ? "Яндекс Браузер" : name;
        }

        string BrowserLabel(string name)
        {
            return name == "Yandex Browser" && Nowcast.Text.Ru ? "Яндекс Браузер" : name;
        }

        void RefreshSites()
        {
            var key = BrowserName();
            if (key == null) return;
            var settings = Settings.Load();
            var allowed = new HashSet<string>(settings.BrowserSitesFor(key), StringComparer.OrdinalIgnoreCase);
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var window in WindowTitles.ReadBrowserWindows())
                if (string.Equals(window.Browser, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(window.Site))
                    open.Add(window.Site);

            var available = new HashSet<string>(open, StringComparer.OrdinalIgnoreCase);
            foreach (var site in Settings.DefaultBrowserSites) available.Add(site);
            foreach (var site in allowed) available.Add(site);
            var ordered = new List<string>();
            foreach (var site in open.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                if (available.Remove(site)) ordered.Add(site);
            foreach (var site in Settings.DefaultBrowserSites)
                if (available.Remove(site)) ordered.Add(site);
            ordered.AddRange(available.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));

            var signature = SiteSignature(ordered, open, allowed);
            activeSites = open;
            if (signature == optionsKey)
            {
                UpdateRowStates();
                return;
            }
            optionsKey = signature;
            siteValues.Clear();
            siteValues.AddRange(ordered);
            loading = true;
            siteRows.SuspendLayout();
            while (siteRows.Controls.Count > 0) siteRows.Controls[0].Dispose();
            checks.Clear();
            rowStates.Clear();
            foreach (var name in siteValues)
            {
                var row = new Panel { Height = 40, BackColor = Theme.Field, Margin = new Padding(2, 1, 2, 1) };
                var check = new Theme.CheckBox
                {
                    Text = name,
                    Checked = allowed.Contains(name),
                    AutoSize = false,
                    Height = 26,
                    Width = 240,
                    Location = new Point(4, 7),
                    BackColor = Theme.Field,
                };
                var state = new Label
                {
                    AutoSize = true,
                    ForeColor = Theme.Muted,
                    Text = activeSites.Contains(name) ? (Nowcast.Text.Ru ? "Открыт сейчас" : "Open now") : "",
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Location = new Point(0, 11),
                };
                row.Controls.Add(check);
                row.Controls.Add(state);
                row.Resize += delegate
                {
                    state.Left = Math.Max(8, row.ClientSize.Width - state.Width - 8);
                    check.Width = Math.Max(80, state.Left - check.Left - 12);
                };
                check.CheckedChanged += delegate { SaveFilter(); };
                checks[name] = check;
                rowStates[name] = state;
                siteRows.Controls.Add(row);
            }
            siteRows.Height = siteRows.Padding.Vertical + Math.Max(1, siteValues.Count) * 42;
            siteViewport.Height = Math.Min(360, Math.Max(108, siteRows.Height));
            LayoutRows();
            siteRows.ResumeLayout(true);
            loading = false;
            UpdateRowStates();
            LayoutBody();
        }

        void UpdateRowStates()
        {
            foreach (var pair in rowStates)
                pair.Value.Text = activeSites.Contains(pair.Key) ? (Nowcast.Text.Ru ? "Открыт сейчас" : "Open now") : "";
            foreach (Control control in siteRows.Controls)
                control.PerformLayout();
        }

        void SaveFilter()
        {
            if (loading || browser.SelectedIndex < 0) return;
            var settings = Settings.Load();
            var allowed = new List<string>();
            foreach (var site in siteValues)
                if (checks[site].Checked) allowed.Add(site);
            settings.BrowserSiteFilters[BrowserName()] = allowed;
            settings.Save();
            optionsKey = SiteSignature(siteValues, activeSites, new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase));
        }

        static string SiteSignature(IEnumerable<string> sites, HashSet<string> active, HashSet<string> allowed)
        {
            return string.Join("\n", sites.Select(site => site + "|" + active.Contains(site) + "|" + allowed.Contains(site)));
        }

        void LayoutRows()
        {
            int width = Math.Max(180, siteViewport.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - siteRows.Padding.Horizontal - 4);
            siteRows.Location = Point.Empty;
            siteRows.Width = width + siteRows.Padding.Horizontal;
            foreach (Control row in siteRows.Controls) row.Width = width;
            siteRows.PerformLayout();
        }

        void LayoutBody()
        {
            if (layingOut || page.ClientSize.Width <= 0) return;
            layingOut = true;
            try
            {
                int width = Math.Max(540, page.ClientSize.Width - 48);
                body.Width = width;
                body.Left = Math.Max(16, (page.ClientSize.Width - width) / 2);
                card.MinimumSize = new Size(width - body.Padding.Horizontal, 0);
                card.Width = width - body.Padding.Horizontal;
                int contentWidth = Math.Max(320, card.Width - card.Padding.Horizontal);
                browser.Width = contentWidth;
                siteViewport.Width = contentWidth;
                help.MaximumSize = activeTabNote.MaximumSize = new Size(siteViewport.Width, 0);
                LayoutRows();
                card.PerformLayout();
                body.PerformLayout();
                body.Height = body.GetPreferredSize(new Size(width, 0)).Height;
                page.AutoScrollMinSize = new Size(body.Left + width + 16, body.Top + body.Height + 16);
            }
            finally { layingOut = false; }
        }

        public void PrepareEmbedded()
        {
            embedded = true;
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            AutoSize = false;
            Dock = DockStyle.Fill;
            page.Dock = DockStyle.Fill;
            LayoutBody();
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) refreshTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
