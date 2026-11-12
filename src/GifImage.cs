using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Nowcast
{
    // Bound the container before asking GDI+ to decode any frames. Keep the
    // original bytes: drawing a GIF into a new bitmap discards its animation.
    static class GifImage
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        public const int MaxSide = 512;
        public const int MaxFrames = 200;
        public const long MaxPixels = 40000000;

        public static bool IsGif(byte[] data)
        {
            return data != null && data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F'
                && data[3] == '8' && (data[4] == '7' || data[4] == '9') && data[5] == 'a';
        }

        static int Word(BinaryReader reader) { return reader.ReadUInt16(); }
        static void Skip(BinaryReader reader, int count)
        {
            if (count > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
            reader.BaseStream.Position += count;
        }
        static void Blocks(BinaryReader reader)
        {
            int size;
            while ((size = reader.ReadByte()) != 0) Skip(reader, size);
        }
        static void Limit(bool exceeded)
        {
            if (exceeded) throw new InvalidDataException(Text.GifLimits);
        }

        public static void Validate(byte[] data)
        {
            Limit(data.Length > MaxBytes);
            try
            {
                if (!IsGif(data)) throw new InvalidDataException(Text.GifInvalid);
                using (var stream = new MemoryStream(data))
                using (var reader = new BinaryReader(stream))
                {
                    Skip(reader, 6);
                    int width = Word(reader), height = Word(reader);
                    Limit(width > MaxSide || height > MaxSide);
                    if (width == 0 || height == 0) throw new InvalidDataException(Text.GifInvalid);
                    int packed = reader.ReadByte();
                    Skip(reader, 2);
                    if ((packed & 128) != 0) Skip(reader, 3 * (1 << ((packed & 7) + 1)));
                    int frames = 0;
                    while (true)
                    {
                        int block = reader.ReadByte();
                        if (block == 0x3b)
                        {
                            if (frames == 0 || stream.Position != stream.Length) throw new InvalidDataException(Text.GifInvalid);
                            break;
                        }
                        if (block == 0x21) { reader.ReadByte(); Blocks(reader); continue; }
                        if (block != 0x2c) throw new InvalidDataException(Text.GifInvalid);
                        int left = Word(reader), top = Word(reader), w = Word(reader), h = Word(reader);
                        if (w == 0 || h == 0 || left + w > width || top + h > height) throw new InvalidDataException(Text.GifInvalid);
                        frames++;
                        Limit(frames > MaxFrames || (long)width * height * frames > MaxPixels);
                        packed = reader.ReadByte();
                        if ((packed & 128) != 0) Skip(reader, 3 * (1 << ((packed & 7) + 1)));
                        reader.ReadByte(); // LZW minimum code size; the decoder validates it.
                        Blocks(reader);
                    }
                }
                using (var stream = new MemoryStream(data))
                using (var image = Image.FromStream(stream, true, true))
                using (var frame = new Bitmap(image.Width, image.Height))
                using (var canvas = Graphics.FromImage(frame))
                {
                    int frames = image.GetFrameCount(FrameDimension.Time);
                    Limit(frames > MaxFrames || (long)image.Width * image.Height * frames > MaxPixels);
                    for (int i = 0; i < frames; i++)
                    {
                        image.SelectActiveFrame(FrameDimension.Time, i);
                        canvas.DrawImageUnscaled(image, 0, 0);
                    }
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception e) { throw new InvalidDataException(Text.GifInvalid, e); }
        }
    }
}
