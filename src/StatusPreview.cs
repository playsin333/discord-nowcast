using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;
using Strings = Nowcast.Text;

namespace Nowcast
{
    // A local simulation of the card. Only GETs an already published cover;
    // viewing it never uploads pictures or sends a Discord activity.
    sealed class StatusPreview : FlowLayoutPanel
    {
        readonly Label header = Line(true), details = Line(true), state = Line(false), extra = Line(false);
        readonly Label member = SettingsForm.Note(""), hint = SettingsForm.Note("");
        readonly Label button = new Label { AutoSize = true, BackColor = Theme.Hover, ForeColor = Theme.Text, Padding = new Padding(10, 6, 10, 6), Margin = new Padding(0, 6, 0, 0), MaximumSize = new Size(300, 0) };
        readonly PictureBox picture = new PictureBox { Size = new Size(64, 64), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Hover, Margin = new Padding(0, 0, 12, 0) };
        MemoryStream imageStream;
        CancellationTokenSource imageRequest;
        string imageKey;
        bool disposed;

        public StatusPreview()
        {
            AutoSize = true;
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            BackColor = Theme.Field;
            Padding = new Padding(12);
            Margin = new Padding(0, 4, 0, 8);
            Controls.Add(header);
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
            var lines = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            lines.Controls.Add(details); lines.Controls.Add(state); lines.Controls.Add(extra);
            row.Controls.Add(picture); row.Controls.Add(lines); Controls.Add(row);
            Controls.Add(button);
            member.MaximumSize = hint.MaximumSize = new Size(320, 0);
            member.Margin = new Padding(0, 8, 0, 0); hint.Margin = new Padding(0, 4, 0, 0);
            Controls.Add(member); Controls.Add(hint);
            picture.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (picture.Image == null)
                    TextRenderer.DrawText(e.Graphics, "♪", Font, picture.ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            };
        }

        static Label Line(bool bold)
        {
            return new Label
            {
                AutoSize = true, MaximumSize = new Size(244, 0), ForeColor = Theme.Text,
                Font = new Font(SystemFonts.MessageBoxFont, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty,
            };
        }

        static void SetLine(Label label, string value)
        { if (label.Text != value) label.Text = value; label.Visible = value.Length > 0; }

        public void Render(StatusPreviewModel model, string localPath, string url)
        {
            SetLine(header, model.Header); SetLine(details, model.Details); SetLine(state, model.State);
            SetLine(extra, model.Extra); SetLine(button, model.Button); SetLine(member, model.Member); SetLine(hint, model.Hint);
            string key = url;
            try
            {
                if (localPath != null)
                {
                    var file = new FileInfo(localPath);
                    if (file.Exists) key = localPath + ":" + file.LastWriteTimeUtc.Ticks + ":" + file.Length;
                    else localPath = null;
                }
            }
            catch (IOException) { key = null; localPath = null; }
            catch (UnauthorizedAccessException) { key = null; localPath = null; }
            if (key == imageKey) return;
            imageKey = key;
            LoadPicture(localPath, url);
        }

        void ClearPicture()
        {
            var old = picture.Image; picture.Image = null;
            if (old != null) old.Dispose();
            if (imageStream != null) { imageStream.Dispose(); imageStream = null; }
            picture.Invalidate();
        }

        async void LoadPicture(string path, string url)
        {
            if (imageRequest != null) { imageRequest.Cancel(); imageRequest.Dispose(); imageRequest = null; }
            ClearPicture();
            if (path == null && string.IsNullOrEmpty(url)) return;
            var request = new CancellationTokenSource(); request.CancelAfter(TimeSpan.FromSeconds(20)); imageRequest = request;
            var token = request.Token;
            MemoryStream stream = null;
            Image image = null;
            try
            {
                if (path != null)
                {
                    if (new FileInfo(path).Length > GifImage.MaxBytes) return;
                    stream = new MemoryStream(File.ReadAllBytes(path));
                }
                else
                {
                    Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
                    using (var response = await Hosts.Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > GifImage.MaxBytes) return;
                        using (var source = await response.Content.ReadAsStreamAsync())
                        {
                            stream = new MemoryStream();
                            var buffer = new byte[8192]; int count;
                            while ((count = await source.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                            {
                                if (stream.Length + count > GifImage.MaxBytes) return;
                                stream.Write(buffer, 0, count);
                            }
                        }
                    }
                }
                if (token.IsCancellationRequested || disposed) return;
                var bytes = stream.ToArray();
                if (GifImage.IsGif(bytes)) GifImage.Validate(bytes);
                stream.Position = 0;
                image = Image.FromStream(stream, true, true);
                // Do not let a remote thumbnail allocate an unbounded canvas.
                if ((long)image.Width * image.Height > 16000000) return;
                imageStream = stream; stream = null;
                picture.Image = image; image = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Write("Preview picture unavailable: " + e.Message); }
            finally
            {
                if (image != null) image.Dispose();
                if (stream != null) stream.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                if (imageRequest != null) { imageRequest.Cancel(); imageRequest.Dispose(); imageRequest = null; }
                ClearPicture();
            }
            base.Dispose(disposing);
        }
    }
}
