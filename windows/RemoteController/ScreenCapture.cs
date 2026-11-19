using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteController
{
    internal sealed class CropFollower
    {
        private Rectangle monitor;
        private Size size;
        private double x, y, targetX, targetY;
        public Rectangle Update(Rectangle bounds, Point cursor, Size requested, double elapsed)
        {
            double scale = Math.Min(1, Math.Min(bounds.Width / (double)requested.Width, bounds.Height / (double)requested.Height));
            var nextSize = new Size(Math.Max(1, (int)Math.Round(requested.Width * scale)), Math.Max(1, (int)Math.Round(requested.Height * scale)));
            if (bounds != monitor || nextSize != size) {
                monitor = bounds; size = nextSize;
                x = targetX = Clamp(cursor.X - size.Width / 2.0, bounds.Left, bounds.Right - size.Width);
                y = targetY = Clamp(cursor.Y - size.Height / 2.0, bounds.Top, bounds.Bottom - size.Height);
            }
            if (cursor.X < targetX + size.Width * .25) targetX = cursor.X - size.Width * .25;
            if (cursor.X > targetX + size.Width * .75) targetX = cursor.X - size.Width * .75;
            if (cursor.Y < targetY + size.Height * .25) targetY = cursor.Y - size.Height * .25;
            if (cursor.Y > targetY + size.Height * .75) targetY = cursor.Y - size.Height * .75;
            targetX = Clamp(targetX, bounds.Left, bounds.Right - size.Width);
            targetY = Clamp(targetY, bounds.Top, bounds.Bottom - size.Height);
            if (cursor.X < x || cursor.X >= x + size.Width || cursor.Y < y || cursor.Y >= y + size.Height) { x = targetX; y = targetY; }
            double alpha = 1 - Math.Exp(-Math.Max(1, Math.Min(250, elapsed)) / 65);
            x += (targetX - x) * alpha; y += (targetY - y) * alpha;
            return new Rectangle((int)Math.Round(x), (int)Math.Round(y), size.Width, size.Height);
        }
        private static double Clamp(double value, double low, double high) { return Math.Max(low, Math.Min(high, value)); }
    }
    public sealed class DesktopScreenSource : IScreenSource
    {
        private readonly CropFollower follower = new CropFollower();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private double previous;
        private Bitmap bitmap;
        private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        public byte[] Capture(int width, int height, int quality)
        {
            Native.Point point;
            if (!Native.GetCursorPos(out point)) throw new Win32Exception();
            double now = clock.Elapsed.TotalMilliseconds;
            var cursor = new Point(point.x, point.y);
            Rectangle crop = follower.Update(Screen.FromPoint(cursor).Bounds, cursor, new Size(width, height), now - previous);
            previous = now;
            if (bitmap == null || bitmap.Size != crop.Size) { if (bitmap != null) bitmap.Dispose(); bitmap = new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb); }
            using (var graphics = Graphics.FromImage(bitmap)) {
                graphics.CopyFromScreen(crop.Location, Point.Empty, crop.Size, CopyPixelOperation.SourceCopy);
                var info = new Native.CursorInfo { size = Marshal.SizeOf(typeof(Native.CursorInfo)) };
                Native.IconInfo icon;
                if (Native.GetCursorInfo(ref info) && (info.flags & 1) != 0 && Native.GetIconInfo(info.cursor, out icon)) {
                    try {
                        IntPtr dc = graphics.GetHdc();
                        try { Native.DrawIconEx(dc, info.position.x - crop.X - (int)icon.hotspotX, info.position.y - crop.Y - (int)icon.hotspotY, info.cursor, 0, 0, 0, IntPtr.Zero, 3); }
                        finally { graphics.ReleaseHdc(dc); }
                    } finally { if (icon.mask != IntPtr.Zero) Native.DeleteObject(icon.mask); if (icon.color != IntPtr.Zero) Native.DeleteObject(icon.color); }
                }
            }
            using (var data = new MemoryStream()) using (var parameters = new EncoderParameters(1)) {
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                // Keep the exact phone aspect ratio even on a smaller monitor.
                if (bitmap.Width == width && bitmap.Height == height) bitmap.Save(data, Jpeg, parameters);
                else using (var output = new Bitmap(width, height, PixelFormat.Format24bppRgb)) {
                    using (var g = Graphics.FromImage(output)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(bitmap, new Rectangle(0, 0, width, height)); }
                    output.Save(data, Jpeg, parameters);
                }
                return data.ToArray();
            }
        }
        public void Dispose() { if (bitmap != null) { bitmap.Dispose(); bitmap = null; } }
        private static class Native
        {
            [StructLayout(LayoutKind.Sequential)] internal struct Point { public int x,y; }
            [StructLayout(LayoutKind.Sequential)] internal struct CursorInfo { public int size, flags; public IntPtr cursor; public Point position; }
            [StructLayout(LayoutKind.Sequential)] internal struct IconInfo { public bool icon; public uint hotspotX,hotspotY; public IntPtr mask,color; }
            [DllImport("user32.dll", SetLastError=true)] internal static extern bool GetCursorPos(out Point point);
            [DllImport("user32.dll")] internal static extern bool GetCursorInfo(ref CursorInfo info);
            [DllImport("user32.dll")] internal static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
            [DllImport("user32.dll")] internal static extern bool DrawIconEx(IntPtr dc,int x,int y,IntPtr icon,int width,int height,uint step,IntPtr brush,uint flags);
            [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr value);
        }
    }
}
