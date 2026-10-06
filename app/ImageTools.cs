// ImageTools.cs - screenshot and view_image: lets the (vision-capable) model see
// the phone's screen and image files. A tool leaves an ImageAttachment behind;
// Agent.cs sends it to the model as an image message after the tool result.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Utilities.Zlib;

namespace WMAI
{
    public class ImageAttachment
    {
        public string Mime;
        public byte[] Data;
        public string Label;
    }

    public delegate void VisibilityHandler(bool visible);

    public class ImageTools
    {
        const int MaxImageBytes = 600 * 1024;
        public VisibilityHandler SetWmaiVisible; // set by the UI; null when headless
        ImageAttachment pending;

        public ImageAttachment TakePending()
        {
            ImageAttachment a = pending;
            pending = null;
            return a;
        }

        static string Arg(Dictionary<string, string> a, string k)
        {
            string v;
            return a.TryGetValue(k, out v) ? v : null;
        }

        // ---- screenshot ----
        public string Screenshot(Dictionary<string, string> a)
        {
            int delay = 0;
            try { delay = Math.Min(30, Math.Max(0, int.Parse((Arg(a, "delay_seconds") ?? "0").Trim()))); }
            catch (Exception) { }
            bool hide = (Arg(a, "hide_wmai") ?? "true").ToLower() != "false";

            int w, h;
            byte[] rgb;
            bool hidden = false;
            try
            {
                if (hide && SetWmaiVisible != null) { SetWmaiVisible(false); hidden = true; Thread.Sleep(800); }
                if (delay > 0) Thread.Sleep(delay * 1000);
                rgb = CaptureScreen(out w, out h);
            }
            finally
            {
                if (hidden) SetWmaiVisible(true);
            }

            byte[] png = EncodePng(rgb, w, h);
            string dir = "\\My Documents\\WMAI";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = dir + "\\screenshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            using (FileStream fs = new FileStream(path, FileMode.Create)) fs.Write(png, 0, png.Length);

            pending = new ImageAttachment();
            pending.Mime = "image/png";
            pending.Data = png;
            pending.Label = "screenshot " + w + "x" + h + " (" + path + ")";
            return "screenshot " + w + "x" + h + " saved to " + path + " (" + png.Length / 1024 + " KB); the image is attached for you to look at";
        }

        [DllImport("coredll.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("coredll.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("coredll.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("coredll.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, byte[] bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("coredll.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("coredll.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("coredll.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("coredll.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("coredll.dll")] static extern int GetSystemMetrics(int index);

        // Returns top-down RGB bytes (3 per pixel).
        static byte[] CaptureScreen(out int w, out int h)
        {
            w = GetSystemMetrics(0); // SM_CXSCREEN
            h = GetSystemMetrics(1); // SM_CYSCREEN
            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr mem = CreateCompatibleDC(screen);
            byte[] bmi = new byte[44]; // BITMAPINFOHEADER + one RGBQUAD
            BitConverter.GetBytes(40).CopyTo(bmi, 0);
            BitConverter.GetBytes(w).CopyTo(bmi, 4);
            BitConverter.GetBytes(h).CopyTo(bmi, 8);           // bottom-up
            BitConverter.GetBytes((short)1).CopyTo(bmi, 12);   // planes
            BitConverter.GetBytes((short)24).CopyTo(bmi, 14);  // bpp, BI_RGB
            IntPtr bits;
            IntPtr dib = CreateDIBSection(screen, bmi, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) { DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen); throw new IOException("CreateDIBSection failed"); }
            IntPtr old = SelectObject(mem, dib);
            try
            {
                BitBlt(mem, 0, 0, w, h, screen, 0, 0, 0x00CC0020); // SRCCOPY
                int stride = (w * 3 + 3) & ~3;
                byte[] raw = new byte[stride * h];
                Marshal.Copy(bits, raw, 0, raw.Length);
                byte[] rgb = new byte[w * h * 3];
                for (int y = 0; y < h; y++)
                {
                    int src = (h - 1 - y) * stride, dst = y * w * 3;
                    for (int x = 0; x < w; x++, src += 3, dst += 3)
                    {
                        rgb[dst] = raw[src + 2];     // BGR -> RGB
                        rgb[dst + 1] = raw[src + 1];
                        rgb[dst + 2] = raw[src];
                    }
                }
                return rgb;
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(dib);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // ---- view_image ----
        public string ViewImage(Dictionary<string, string> a)
        {
            string path = Arg(a, "path");
            if (path == null || !File.Exists(path)) return "error: no such file: " + path;
            string ext = Path.GetExtension(path).ToLower();
            byte[] data;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                if (fs.Length > 4 * 1024 * 1024) return "error: image too large (" + fs.Length / 1024 + " KB)";
                data = new byte[fs.Length];
                int off = 0, n;
                while (off < data.Length && (n = fs.Read(data, off, data.Length - off)) > 0) off += n;
            }
            string mime;
            if (ext == ".png") mime = "image/png";
            else if (ext == ".jpg" || ext == ".jpeg") mime = "image/jpeg";
            else if (ext == ".gif") mime = "image/gif";
            else if (ext == ".bmp" || ext == ".dib" || ext == ".2bp")
            {
                int w, h;
                byte[] rgb = DecodeBmp(data, out w, out h);
                if (rgb == null) return "error: unsupported BMP format";
                data = EncodePng(rgb, w, h);
                mime = "image/png";
            }
            else return "error: unsupported image type " + ext + " (png, jpg, gif, bmp)";
            if (data.Length > MaxImageBytes)
                return "error: image is " + data.Length / 1024 + " KB; the limit for sending is " + MaxImageBytes / 1024 + " KB";

            pending = new ImageAttachment();
            pending.Mime = mime;
            pending.Data = data;
            pending.Label = path;
            return "image " + path + " (" + data.Length / 1024 + " KB) is attached for you to look at";
        }

        // 8/16/24/32-bit uncompressed (or 16/32-bit BI_BITFIELDS) BMP -> top-down RGB.
        static byte[] DecodeBmp(byte[] d, out int w, out int h)
        {
            w = h = 0;
            if (d.Length < 54 || d[0] != 'B' || d[1] != 'M') return null;
            int dataOff = BitConverter.ToInt32(d, 10), hdr = BitConverter.ToInt32(d, 14);
            w = BitConverter.ToInt32(d, 18);
            int hh = BitConverter.ToInt32(d, 22);
            int bpp = BitConverter.ToInt16(d, 28), comp = BitConverter.ToInt32(d, 30);
            bool topDown = hh < 0;
            h = Math.Abs(hh);
            if (w <= 0 || h <= 0 || w * h > 2000000) return null;
            uint rMask = 0x7C00, gMask = 0x03E0, bMask = 0x001F; // 16-bit default 555
            if (comp == 3) // BI_BITFIELDS: masks follow the header
            {
                rMask = BitConverter.ToUInt32(d, 14 + hdr);
                gMask = BitConverter.ToUInt32(d, 18 + hdr);
                bMask = BitConverter.ToUInt32(d, 22 + hdr);
            }
            else if (comp != 0) return null;
            int stride = ((w * bpp + 31) / 32) * 4;
            int palOff = 14 + hdr;
            byte[] rgb = new byte[w * h * 3];
            for (int y = 0; y < h; y++)
            {
                int row = dataOff + (topDown ? y : h - 1 - y) * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 3;
                    byte r, g, b;
                    if (bpp == 24 || bpp == 32)
                    {
                        int p = row + x * (bpp / 8);
                        b = d[p]; g = d[p + 1]; r = d[p + 2];
                    }
                    else if (bpp == 16)
                    {
                        uint v = BitConverter.ToUInt16(d, row + x * 2);
                        r = Scale(v, rMask); g = Scale(v, gMask); b = Scale(v, bMask);
                    }
                    else if (bpp == 8)
                    {
                        int p = palOff + d[row + x] * 4;
                        b = d[p]; g = d[p + 1]; r = d[p + 2];
                    }
                    else return null;
                    rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
                }
            }
            return rgb;
        }

        static byte Scale(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while (((mask >> shift) & 1) == 0) shift++;
            uint max = mask >> shift;
            return (byte)(((v & mask) >> shift) * 255 / max);
        }

        // ---- PNG encoder: RGB, 8 bits per channel, "Sub" filter, zlib via Bouncy Castle ----
        public static byte[] EncodePng(byte[] rgb, int w, int h)
        {
            MemoryStream idat = new MemoryStream();
            ZOutputStream z = new ZOutputStream(idat, JZlib.Z_BEST_SPEED);
            byte[] line = new byte[w * 3 + 1];
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 3;
                line[0] = 1; // Sub: each byte minus the same channel of the pixel to its left
                for (int i = 0; i < w * 3; i++)
                    line[i + 1] = (byte)(rgb[row + i] - (i >= 3 ? rgb[row + i - 3] : 0));
                z.Write(line, 0, line.Length);
            }
            z.Finish();
            byte[] compressed = idat.ToArray();

            MemoryStream png = new MemoryStream();
            png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            byte[] ihdr = new byte[13];
            BigEndian(w, ihdr, 0);
            BigEndian(h, ihdr, 4);
            ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
            Chunk(png, "IHDR", ihdr);
            Chunk(png, "IDAT", compressed);
            Chunk(png, "IEND", new byte[0]);
            return png.ToArray();
        }

        static void BigEndian(int v, byte[] b, int o)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        static void Chunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4], crcb = new byte[4];
            BigEndian(data.Length, len, 0);
            byte[] t = Encoding.ASCII.GetBytes(type);
            s.Write(len, 0, 4);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = Crc(0xFFFFFFFF, t);
            crc = Crc(crc, data) ^ 0xFFFFFFFF;
            BigEndian((int)crc, crcb, 0);
            s.Write(crcb, 0, 4);
        }

        static uint[] crcTable;

        static uint Crc(uint crc, byte[] data)
        {
            if (crcTable == null)
            {
                uint[] t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            for (int i = 0; i < data.Length; i++) crc = crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
