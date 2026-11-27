using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Nowcast
{
    sealed class DesktopAppsForm : Form
    {
        sealed class TargetRow
        {
            public string Key;
            public string Name;
            public string Description;
            public override string ToString() { return Name + "  ·  " + Description; }
        }
        sealed class ProcessRow
        {
            public DesktopApp App;
            public int ImageIndex;
        }

        readonly ListView processes = new ListView();
        readonly ImageList processImages = new ImageList { ImageSize = new Size(32, 32), ColorDepth = ColorDepth.Depth32Bit };
        readonly ListBox targets = new ListBox();
        readonly TextBox name = OwnForm.Line();
        readonly Dictionary<string, string> apps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool refreshing;
        string selectedKey;
        readonly Action done;

        public DesktopAppsForm(Action done = null)
        {
            this.done = done;
            Text = Nowcast.Text.Ru ? "Приложения для Discord" : "Apps for Discord";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(680, 640);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Width = 760;
            Height = 760;
            Padding = new Padding(14);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            layout.Controls.Add(SettingsForm.Heading(Nowcast.Text.Ru ? "Показывать приложения в Discord" : "Show apps on Discord"), 0, 0);
            layout.Controls.Add(SettingsForm.Note(Nowcast.Text.Ru
                ? "Добавьте программу из списка открытых окон или укажите .exe. Пока её процесс запущен, Discord покажет имя и заголовок окна. Имя и значок определяются автоматически; имя можно изменить ниже."
                : "Add an app from the open windows list or choose its .exe. While its process is running, Discord shows the app name and window title. The name and icon are detected automatically; you can edit the name below."), 0, 1);

            processes.Dock = DockStyle.Fill;
            processes.View = View.LargeIcon;
            processes.LargeImageList = processImages;
            processes.MultiSelect = false;
            processes.HideSelection = false;
            processes.BackColor = Theme.Field;
            processes.ForeColor = Theme.Text;
            processes.BorderStyle = BorderStyle.FixedSingle;
            layout.Controls.Add(processes, 0, 2);

            var processButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var refresh = OwnForm.Flat(Nowcast.Text.Ru ? "Обновить список" : "Refresh list", false);
            var addProcess = OwnForm.Flat(Nowcast.Text.Ru ? "Добавить процесс" : "Add process", true);
            var addExe = OwnForm.Flat(Nowcast.Text.Ru ? "Выбрать .exe…" : "Choose .exe…", false);
            refresh.Click += delegate { RefreshProcesses(); };
            addProcess.Click += delegate { AddProcess(); };
            addExe.Click += delegate { AddExe(); };
            processButtons.Controls.Add(refresh);
            processButtons.Controls.Add(addProcess);
            processButtons.Controls.Add(addExe);
            layout.Controls.Add(processButtons, 0, 3);

            layout.Controls.Add(SettingsForm.Heading(Nowcast.Text.Ru ? "Добавленные приложения" : "Added apps"), 0, 4);
            targets.Dock = DockStyle.Fill;
            targets.BackColor = Theme.Field;
            targets.ForeColor = Theme.Text;
            targets.BorderStyle = BorderStyle.FixedSingle;
            targets.SelectedIndexChanged += delegate { if (!refreshing) SelectTarget(); };
            layout.Controls.Add(targets, 0, 5);

            var edit = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            edit.Controls.Add(new Label { Text = Nowcast.Text.Ru ? "Имя в Discord:" : "Name in Discord:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
            name.Width = 300;
            edit.Controls.Add(name);
            var remove = OwnForm.Flat(Nowcast.Text.Ru ? "Убрать" : "Remove", false);
            remove.Click += delegate { RemoveTarget(); };
            edit.Controls.Add(remove);
            layout.Controls.Add(edit, 0, 6);

            var bottom = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var save = OwnForm.Flat(Nowcast.Text.Ru ? "Сохранить" : "Save", true);
            var close = OwnForm.Flat(done == null ? (Nowcast.Text.Ru ? "Закрыть" : "Close") : (Nowcast.Text.Ru ? "Назад" : "Back"), false);
            save.Click += delegate
            {
                CommitName(); Save();
                if (done == null) { DialogResult = DialogResult.OK; Close(); }
                else save.Text = Nowcast.Text.Ru ? "Сохранено ✓" : "Saved ✓";
            };
            close.Click += delegate { if (done != null) done(); else { DialogResult = DialogResult.Cancel; Close(); } };
            bottom.Controls.Add(save);
            bottom.Controls.Add(close);
            layout.Controls.Add(bottom, 0, 7);
            Controls.Add(layout);

            foreach (var pair in Settings.Load().Executables) apps[pair.Key] = pair.Value;
            name.TextChanged += delegate { CommitName(); };
            RefreshProcesses();
            RefreshTargets();
        }

        public void PrepareEmbedded()
        {
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            AutoSize = false;
            MinimumSize = Size.Empty;
            Dock = DockStyle.Fill;
        }

        void RefreshProcesses()
        {
            processes.BeginUpdate();
            processes.Items.Clear();
            processImages.Images.Clear();
            foreach (var app in DesktopApps.Available())
            {
                int index = processImages.Images.Count;
                Image image = app.ExecutablePath == "" ? null : AppIcon.Local(app.ExecutablePath);
                if (image == null) image = SystemIcons.Application.ToBitmap();
                processImages.Images.Add(image);
                var item = new ListViewItem(app.Name + (string.IsNullOrEmpty(app.Title) ? "" : "\n" + app.Title)) { Tag = new ProcessRow { App = app, ImageIndex = index }, ImageIndex = index };
                processes.Items.Add(item);
            }
            processes.EndUpdate();
        }

        void AddProcess()
        {
            if (processes.SelectedItems.Count == 0) return;
            var row = processes.SelectedItems[0].Tag as ProcessRow;
            if (row == null) return;
            var key = "process|" + row.App.ProcessName;
            CommitName();
            if (!apps.ContainsKey(key)) apps[key] = row.App.Name;
            selectedKey = null;
            name.Text = apps[key];
            selectedKey = key;
            RefreshTargets();
            SelectByKey(key);
        }

        void AddExe()
        {
            using (var dialog = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                CommitName();
                var path = Path.GetFullPath(dialog.FileName);
                var key = "exe|" + path;
                if (!apps.ContainsKey(key)) apps[key] = DesktopApps.NameFromPath(path);
                selectedKey = null;
                name.Text = apps[key];
                selectedKey = key;
                RefreshTargets();
                SelectByKey(key);
            }
        }

        void RemoveTarget()
        {
            CommitName();
            if (string.IsNullOrEmpty(selectedKey)) return;
            apps.Remove(selectedKey);
            selectedKey = null;
            name.Text = "";
            RefreshTargets();
        }

        void SelectTarget()
        {
            CommitName();
            var row = targets.SelectedItem as TargetRow;
            selectedKey = row == null ? null : row.Key;
            refreshing = true;
            name.Text = row == null ? "" : row.Name;
            refreshing = false;
        }

        void CommitName()
        {
            if (refreshing || string.IsNullOrEmpty(selectedKey) || !apps.ContainsKey(selectedKey)) return;
            var value = name.Text.Trim();
            if (value.Length >= 2) apps[selectedKey] = value;
        }

        void RefreshTargets()
        {
            refreshing = true;
            targets.Items.Clear();
            foreach (var pair in apps)
            {
                string description = pair.Key.StartsWith("process|", StringComparison.OrdinalIgnoreCase)
                    ? pair.Key.Substring(8) + " · " + (Nowcast.Text.Ru ? "процесс" : "process")
                    : pair.Key.StartsWith("exe|", StringComparison.OrdinalIgnoreCase) ? pair.Key.Substring(4)
                    : pair.Key;
                targets.Items.Add(new TargetRow { Key = pair.Key, Name = pair.Value, Description = description });
            }
            refreshing = false;
        }

        void SelectByKey(string key)
        {
            for (int i = 0; i < targets.Items.Count; i++)
                if (string.Equals(((TargetRow)targets.Items[i]).Key, key, StringComparison.OrdinalIgnoreCase)) { targets.SelectedIndex = i; break; }
        }

        void Save()
        {
            var settings = Settings.Load();
            settings.Executables = new Dictionary<string, string>(apps, StringComparer.OrdinalIgnoreCase);
            settings.Save();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this);
        }
    }
}
