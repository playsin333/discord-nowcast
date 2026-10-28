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
            buttons.Controls.Add(pick);
            buttons.Controls.Add(remove);
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
            try
            {
                if (File.Exists(path))
                {
                    picture.Image = Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
                }
            }
            catch (Exception e)
            {
                Log.Write(name + " not shown: " + e.Message);
            }
        }
    }
}
