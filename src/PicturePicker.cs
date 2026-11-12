using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Strings = Nowcast.Text;

namespace Nowcast
{
    // A picture of one's own kept in a file: its preview with "Choose…" and
    // "Remove" beside it. One's own status has one, and so does the track
    // with no cover.
    class PicturePicker : FlowLayoutPanel
    {
        readonly string path;
        // For the log: "Own picture", "Fallback picture".
        readonly string name;
        MemoryStream previewStream;
        readonly ToolTip limitsTip = new ToolTip { AutoPopDelay = 15000 };
        readonly Timer progressTimer = new Timer { Interval = 500 };
        readonly Label status = SettingsForm.Note("");
        readonly Button retry = OwnForm.Flat(Strings.RetryPicture, false);
        readonly PictureBox picture = new PictureBox
        {
            Size = new Size(96, 96),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Field,
            Margin = new Padding(0, 3, 10, 0),
        };

        public PicturePicker(string path, string name)
        {
            this.path = path;
            this.name = name;
            AutoSize = true;
            WrapContents = false;
            Controls.Add(picture);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var pick = OwnForm.Flat(Strings.OwnPick, false);
            pick.Click += delegate { Pick(); };
            var remove = OwnForm.Flat(Strings.OwnRemove, false);
            remove.Click += delegate { Remove(); };
            var pickRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var hint = SettingsForm.Note(Strings.GifHint);
            hint.Margin = new Padding(6, 7, 0, 0);
            hint.Cursor = Cursors.Help;
            limitsTip.SetToolTip(hint, Strings.GifLimits);
            limitsTip.SetToolTip(pick, Strings.GifLimits);
            limitsTip.SetToolTip(picture, Strings.GifLimits);
            pickRow.Controls.Add(pick);
            pickRow.Controls.Add(hint);
            buttons.Controls.Add(pickRow);
            buttons.Controls.Add(remove);
            var note = SettingsForm.Note(Strings.PictureModeration);
            note.MaximumSize = new Size(280, 0);
            buttons.Controls.Add(note);
            var support = OwnForm.Flat(Strings.DiscordServer, false);
            support.Click += delegate { Program.Open(Program.DiscordInvite, null); };
            buttons.Controls.Add(support);
            var copyId = OwnForm.Flat(Strings.CopyUploadId, false);
            copyId.Click += delegate
            {
                try
                {
                    Clipboard.SetText(PictureInstallation.PublicId());
                    copyId.Text = Strings.UploadIdCopied;
                }
                catch (Exception e)
                {
                    MessageBox.Show(FindForm(), string.Format(Strings.UploadIdFailed, e.Message), "discord-nowcast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            limitsTip.SetToolTip(copyId, Strings.UploadIdNote);
            buttons.Controls.Add(copyId);
            status.MaximumSize = new Size(280, 0);
            buttons.Controls.Add(status);
            retry.Click += delegate
            {
                try { Covers.RetryPicture(path); RefreshStatus(); }
                catch (Exception e) { Log.Write("Picture retry failed: " + e.Message); }
            };
            buttons.Controls.Add(retry);
            progressTimer.Tick += delegate { RefreshStatus(); };
            VisibleChanged += delegate
            {
                progressTimer.Enabled = Visible;
                if (Visible) RefreshStatus();
            };
            Controls.Add(buttons);
            ShowPicture();
        }

        // Any picture Windows can open, made as small as a cover and kept in its own file.
        void Pick()
        {
            using (var dialog = new OpenFileDialog { Filter = Strings.OwnPictures + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|*.*|*.*" })
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                try
                {
                    var bytes = CoverImage.FromFile(dialog.FileName);
                    Directory.CreateDirectory(Log.Folder);
                    File.WriteAllBytes(path, bytes);
                    Log.Write(name + " picked: " + bytes.Length / 1024 + " KB");
                }
                catch (Exception e)
                {
                    MessageBox.Show(FindForm(), string.Format(Strings.OwnPictureFailed, e.Message), "discord-nowcast", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            ShowPicture();
        }

        void Remove()
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e)
            {
                Log.Write(name + " not removed: " + e.Message);
            }
            ShowPicture();
        }

        // From a copy in memory: the file stays free to be replaced.
        public void ShowPicture()
        {
            var old = picture.Image;
            picture.Image = null;
            if (old != null) old.Dispose();
            if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
            try
            {
                if (File.Exists(path))
                {
                    previewStream = new MemoryStream(File.ReadAllBytes(path));
                    picture.Image = Image.FromStream(previewStream);
                }
            }
            catch (Exception e)
            {
                Log.Write(name + " not shown: " + e.Message);
                if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
            }
            RefreshStatus();
        }

        void RefreshStatus()
        {
            try
            {
                status.Text = Covers.PictureStatus(path);
                retry.Visible = File.Exists(path);
                retry.Enabled = status.Text != Strings.PictureUploading;
            }
            catch (IOException) { status.Text = Strings.PictureRetry; }
            catch (UnauthorizedAccessException) { status.Text = Strings.PictureRetry; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                progressTimer.Dispose();
                limitsTip.Dispose();
                var old = picture.Image;
                picture.Image = null;
                if (old != null) old.Dispose();
                if (previewStream != null) { previewStream.Dispose(); previewStream = null; }
            }
            base.Dispose(disposing);
        }
    }
}
