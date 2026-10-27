using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace TwinEars
{
    // The icon is drawn in code so the exe stays a single file: two overlapping "earbuds",
    // green/blue when mirroring is on, grey when off.
    internal static class AppIcon
    {
        public static Bitmap Draw(bool on, int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color front = on ? Color.FromArgb(0x1E, 0x9E, 0x55) : Color.FromArgb(0x78, 0x7F, 0x8A);
                Color back = on ? Color.FromArgb(0x25, 0x74, 0xD9) : Color.FromArgb(0xA9, 0xAE, 0xB6);
                float d = size * 0.64f;
                float m = size * 0.03f;
                var backRect = new RectangleF(size - d - m, size - d - m, d, d);
                var frontRect = new RectangleF(m, m, d, d);
                float ring = Math.Max(1f, size / 14f);
                float dot = d * 0.34f;
                using (var brush = new SolidBrush(back)) g.FillEllipse(brush, backRect);
                using (var brush = new SolidBrush(Color.White)) g.FillEllipse(brush, RectangleF.Inflate(frontRect, ring, ring));
                using (var brush = new SolidBrush(front)) g.FillEllipse(brush, frontRect);
                using (var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                {
                    g.FillEllipse(brush, frontRect.X + (d - dot) / 2, frontRect.Y + (d - dot) / 2, dot, dot);
                    g.FillEllipse(brush, backRect.X + (d - dot) / 2 + d * 0.14f, backRect.Y + (d - dot) / 2 + d * 0.14f, dot, dot);
                }
            }
            return bmp;
        }

        public static Icon Create(bool on, int size)
        {
            using (Bitmap bmp = Draw(on, size))
                return Icon.FromHandle(bmp.GetHicon());   // a handful of icons, kept for the app's lifetime
        }

        // Build-time helper ("--make-icon file.ico") that produces the icon embedded in the exe.
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
            {
                using (Bitmap b = Draw(true, s))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }
            }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) w.Write(image);
            }
        }
    }

    // A short click track for lining up the delay by ear.
    internal static class TestClicks
    {
        public const int Seconds = 10;

        public static byte[] Create()
        {
            const int rate = 48000;
            int frames = rate * Seconds;
            var stream = new MemoryStream(44 + frames * 4);
            var w = new BinaryWriter(stream);
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + frames * 4);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(rate);
            w.Write(rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(frames * 4);

            int lead = rate / 2;          // half a second of silence so sleeping Bluetooth devices wake up
            int beat = rate / 2;          // two clicks per second
            int clickLength = rate / 40;  // 25 ms
            for (int i = 0; i < frames; i++)
            {
                short v = 0;
                int pos = i - lead;
                if (pos >= 0 && pos % beat < clickLength)
                {
                    bool accent = (pos / beat) % 4 == 0;
                    double t = (double)(pos % beat) / rate;
                    v = (short)(Math.Sin(2 * Math.PI * (accent ? 1760 : 1320) * t) * Math.Exp(-t * 180) * 0.3 * 32767);
                }
                w.Write(v);
                w.Write(v);
            }
            w.Flush();
            return stream.ToArray();
        }
    }
}
