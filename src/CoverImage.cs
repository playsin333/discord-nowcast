using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Nowcast
{
    // The player's picture made ready for upload, and told apart from another.
    static class CoverImage
    {
        // The picture as it is uploaded. Spotify's is the artwork with its logo
        // below it on a transparent strip, and Discord would show all of it: the
        // strip is cut off. A picture bigger than Discord shows is scaled down
        // to MaxSide, and one with nothing see-through in it becomes a JPEG: a
        // 640 px PNG took seconds to upload, this is a few kilobytes. A small
        // picture without a strip is sent as it came.
        const int MaxSide = 300;

        // A picture of one's own from a file of any kind Windows reads: no
        // bigger than a cover, as PNG when it has transparent parts, else JPEG.
        public static byte[] FromFile(string path)
        {
            using (var source = new Bitmap(path))
            {
                var isOpaque = !Image.IsAlphaPixelFormat(source.PixelFormat);
                var scale = Math.Min(1.0, (double)MaxSide / Math.Max(source.Width, source.Height));
                var width = Math.Max(1, (int)Math.Round(source.Width * scale));
                var height = Math.Max(1, (int)Math.Round(source.Height * scale));
                using (var picture = new Bitmap(width, height, isOpaque ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb))
                {
                    using (var canvas = Graphics.FromImage(picture))
                    {
                        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        canvas.DrawImage(source, new Rectangle(0, 0, width, height));
                    }
                    return Encode(picture, isOpaque);
                }
            }
        }

        public static byte[] Prepare(byte[] image)
        {
            try
            {
                using (var stream = new MemoryStream(image))
                using (var source = new Bitmap(stream))
                {
                    bool isOpaque;
                    var area = ArtArea(source, out isOpaque);
                    var isCut = area.Width != source.Width || area.Height != source.Height;
                    var scale = Math.Min(1.0, (double)MaxSide / Math.Max(area.Width, area.Height));
                    if (!isCut && scale >= 1.0) return image;
                    var width = Math.Max(1, (int)Math.Round(area.Width * scale));
                    var height = Math.Max(1, (int)Math.Round(area.Height * scale));
                    using (var picture = new Bitmap(width, height, isOpaque ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb))
                    {
                        using (var canvas = Graphics.FromImage(picture))
                        using (var attributes = new ImageAttributes())
                        {
                            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            canvas.SmoothingMode = SmoothingMode.HighQuality;
                            canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            if (isOpaque) canvas.Clear(Color.Black);
                            attributes.SetWrapMode(WrapMode.TileFlipXY);
                            canvas.DrawImage(source, new Rectangle(0, 0, width, height), area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attributes);
                        }
                        var bytes = Encode(picture, isOpaque);
                        Log.Write("Cover prepared: " + source.Width + "x" + source.Height + ", " + image.Length / 1024 + " KB -> " + width + "x" + height + ", " + bytes.Length / 1024 + " KB");
                        return bytes;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Write("Cover not prepared, sent as it is: " + e.GetBaseException().Message);
                return image;
            }
        }

        // The artwork's part of the picture: the tallest block of rows with
        // something opaque in it, when a smaller one lies apart from it (a logo
        // on a transparent strip); else the whole picture. isOpaque: next to
        // nothing in that part is see-through, so it can be a JPEG.
        static Rectangle ArtArea(Bitmap source, out bool isOpaque)
        {
            var width = source.Width;
            var height = source.Height;
            var area = new Rectangle(0, 0, width, height);
            isOpaque = true;
            if (!Image.IsAlphaPixelFormat(source.PixelFormat)) return area;
            // What cannot be read is taken as see-through, and goes out as a PNG.
            isOpaque = false;
            if (width < 32 || height < 32) return area;

            var data = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels;
            int stride;
            try
            {
                stride = data.Stride;
                if (stride < width * 4) return area;
                pixels = new byte[stride * height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            }
            finally
            {
                source.UnlockBits(data);
            }

            // For every row: the first and the last column with something opaque in it.
            var first = new int[height];
            var last = new int[height];
            for (var y = 0; y < height; y++)
            {
                first[y] = -1;
                last[y] = -1;
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    if (pixels[row + x * 4 + 3] <= 32) continue;
                    if (first[y] < 0) first[y] = x;
                    last[y] = x;
                }
            }

            // Runs of rows with something in them. The artwork is the tallest one;
            // the logo is a small run of its own, apart from it.
            int bestTop = -1, bestBottom = -1, runs = 0;
            var top = -1;
            for (var y = 0; y <= height; y++)
            {
                var filled = y < height && first[y] >= 0;
                if (filled && top < 0) top = y;
                if (!filled && top >= 0)
                {
                    runs++;
                    if (bestTop < 0 || y - top > bestBottom - bestTop + 1)
                    {
                        bestTop = top;
                        bestBottom = y - 1;
                    }
                    top = -1;
                }
            }
            if (runs >= 2)
            {
                var artHeight = bestBottom - bestTop + 1;
                if (artHeight >= 32 && artHeight >= height / 2)
                {
                    int left = width, right = 0;
                    for (var y = bestTop; y <= bestBottom; y++)
                    {
                        if (first[y] >= 0 && first[y] < left) left = first[y];
                        if (last[y] > right) right = last[y];
                    }
                    if (right - left + 1 >= 32) area = new Rectangle(left, bestTop, right - left + 1, artHeight);
                }
            }

            // How much of the artwork is see-through: up to half a percent is let go
            // (rounded corners), and the picture is a JPEG with black behind them.
            long seeThrough = 0;
            for (var y = area.Top; y < area.Bottom; y++)
            {
                var row = y * stride;
                for (var x = area.Left; x < area.Right; x++)
                {
                    if (pixels[row + x * 4 + 3] < 250) seeThrough++;
                }
            }
            isOpaque = seeThrough * 200 <= (long)area.Width * area.Height;
            return area;
        }

        // A JPEG of good quality, or a PNG when something in the picture is see-through.
        static byte[] Encode(Bitmap picture, bool asJpeg)
        {
            using (var output = new MemoryStream())
            {
                ImageCodecInfo jpeg = null;
                if (asJpeg)
                {
                    foreach (var codec in ImageCodecInfo.GetImageEncoders())
                    {
                        if (codec.MimeType == "image/jpeg") jpeg = codec;
                    }
                }
                if (jpeg == null)
                {
                    picture.Save(output, ImageFormat.Png);
                    return output.ToArray();
                }
                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
                    picture.Save(output, jpeg, parameters);
                    return output.ToArray();
                }
            }
        }

        // The size and a hash of the bytes: enough to tell two pictures apart.
        public static string Fingerprint(byte[] image)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (var i = 0; i < image.Length; i++) hash = (hash ^ image[i]) * 1099511628211UL;
                return image.Length + ":" + hash.ToString("x");
            }
        }
    }
}
